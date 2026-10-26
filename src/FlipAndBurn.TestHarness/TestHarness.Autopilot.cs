using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // The autopilot proofs (cycle 9): (a) a full burn to Duna with infinite propellant through the flip, and (b) a coast
    // trip to Duna into the coast. Each ends with Abort and a check that the player's MechJeb settings are unchanged.
    public partial class TestHarness
    {
        /// <summary>A ship in flight, for the proofs.</summary>
        private sealed class Flown : CaptureResult
        {
            public MechJebCore Core;
            public MechJebModuleFlipAndBurn Module;
            public Vessel Ship;
        }

        // Physics warp rates to try while burning at 1 g; the highest stable one is used from then on.
        // With overheating ignored the ship stays cool: 10x and 25x (25x destroyed the hot drive in an earlier run).
        private static readonly float[] ProbeRates = { 10, 25 };
        private const float ProbeSeconds = 20f;          // real seconds at each rate
        private const float StatusEvery = 2f;            // real seconds between status lines
        private const double CoastAcceleration = 0.4;    // g, for (b): below what the Bebop's drive sustains (it was still cooling at 0.48 g)

        private double lastStatusReal, statusMaxAccelError, statusMaxPointing;

        // ------------------------------------------------------------------ (a)

        private IEnumerator ProofFullBurn(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            // The flip at 1 g. The Bebop's drive can't sustain 1 g (see ProofOverheat), so this leg also ignores overheating:
            // Kris's choice (2026-10-26), to reach the flip in reasonable time. Both cheats: this harness only, this test copy
            // only, and off again at the end.
            module.FlipStyleIndex = 0;   // cut, flip, relight
            module.SelectTripType(TripType.FullBurn);
            module.AccelerationG.Val = 1.0;
            CheatOptions.InfinitePropellant = true;
            CheatOptions.IgnoreMaxTemperature = true;
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (a) flip run, full burn to Duna at {module.AccelerationG.Val} g, infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}, planner can start: {module.Planner.CanStart}");
            string before = MechJebSettingsFingerprint(f);

            string problem = module.PressStart();
            if (problem != null)
            {
                Fail("step " + step, "Start refused: " + problem);
                yield break;
            }

            LogPlan(pilot);
            step++;

            // Climb out and settle into the burn.
            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Accelerate && !pilot.Last.Departing &&
                                          pilot.PointingError < 1 && Math.Abs(pilot.MeasuredAcceleration / pilot.Last.Acceleration - 1) < 0.02, 600, "settled at 1 g");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }

            // The probe needs a steady burn: wait until the climb out of Kerbin's sphere of influence is over (the re-plan
            // restarts it) and the ship has held its direction for 30 game seconds.
            double steadySince = double.NaN;
            yield return Fly(pilot, () =>
            {
                double now = Planetarium.GetUniversalTime();
                bool steady = !pilot.Last.Departing && pilot.Last.Phase == GuidancePhase.Accelerate && pilot.PointingError < 1;
                if (!steady) steadySince = double.NaN;
                else if (double.IsNaN(steadySince)) steadySince = now;
                return steady && now - steadySince > 30;
            }, 1800, "climb out done, steady burn");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            Log($"step {step}: steady burn at {pilot.CurrentAcceleration / 9.81:F2} g, ready for the warp probe");
            step++;

            // Warp probe.
            float best = 4;
            foreach (float rate in ProbeRates)
            {
                // KSP doesn't re-apply a slot's new value while warping in it: step down a slot first, and the autopilot steps
                // back up into it (one slot per real second).
                if (TimeWarp.CurrentRateIndex == 3) TimeWarp.SetRate(2, true);
                TimeWarp.fetch.physicsWarpRates[3] = rate;
                MechJebModuleFlipAndBurnAutopilot.MaxPhysicsRate = rate;
                int parts = f.Ship.parts.Count;
                double ut0 = Planetarium.GetUniversalTime();
                float real0 = Time.realtimeSinceStartup;
                statusMaxAccelError = statusMaxPointing = 0;
                // Let warp reach the rate (one step per real second), then measure.
                yield return Fly(pilot, () => Time.realtimeSinceStartup - real0 > 5, 30, $"warp up to x{rate}");
                statusMaxAccelError = statusMaxPointing = 0;
                double ut1 = Planetarium.GetUniversalTime();
                float real1 = Time.realtimeSinceStartup;
                yield return Fly(pilot, () => Time.realtimeSinceStartup - real1 > ProbeSeconds, ProbeSeconds + 10, $"probe x{rate}");
                if (!pilot.Running) { Fail("step " + step, "trip stopped during the warp probe: " + pilot.StopReason); yield break; }
                double speed = (Planetarium.GetUniversalTime() - ut1) / (Time.realtimeSinceStartup - real1);
                bool lostParts = f.Ship.parts.Count != parts;
                bool stable = !lostParts && statusMaxAccelError < 0.02 && statusMaxPointing < 2 && TimeWarp.CurrentRate >= rate * 0.99f;
                Log($"warp probe x{rate}: game/real speed {speed:F1}, max acceleration error {statusMaxAccelError * 100:F2}%, " +
                    $"max pointing error {statusMaxPointing:F2} deg, parts {parts} -> {f.Ship.parts.Count}, current rate x{TimeWarp.CurrentRate} -> " +
                    (stable ? "stable" : "NOT stable"));
                if (!stable || speed < 0.5 * rate) break;
                best = rate;
            }

            TimeWarp.fetch.physicsWarpRates[3] = best;
            MechJebModuleFlipAndBurnAutopilot.MaxPhysicsRate = best;
            Log($"step {step}: highest stable physics warp on this machine: x{best}");
            step++;

            // On to the flip.
            yield return Fly(pilot, () => pilot.Last.Phase == GuidancePhase.Accelerate && pilot.Last.TimeToFlip < 20 && TimeWarp.CurrentRate <= 1.01f,
                3 * 3600, "approach the flip");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            yield return Shot("f-1-before-flip.png", f);
            yield return Fly(pilot, () => pilot.Last.Phase == GuidancePhase.Flip && pilot.PointingError < 120 && pilot.PointingError > 60, 600, "flip under way");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            yield return Shot("f-2-during-flip.png", f);
            yield return Fly(pilot, () => pilot.Last.Phase == GuidancePhase.Decelerate && !double.IsNaN(pilot.FlipSeconds), 600, "flip finished");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            double flipEnd = Planetarium.GetUniversalTime();
            Log($"step {step}: flip took {pilot.FlipSeconds:F1} s of game time");
            yield return Fly(pilot, () => Planetarium.GetUniversalTime() - flipEnd > 30, 120, "settle into braking");
            yield return Shot("f-3-after-flip.png", f);
            yield return Fly(pilot, () => Planetarium.GetUniversalTime() - flipEnd > 240, 600, "brake a few minutes");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }

            module.PressAbort();
            Log($"step {step}: Abort pressed. {pilot.StopReason} {pilot.SettingsCheck}");
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(f);
            yield return Shot("f-4-after-abort.png", f);
            string after = MechJebSettingsFingerprint(f);
            Log($"step {step}: MechJeb settings files {(before == after ? "byte-identical" : "DIFFERENT")} before Start and after Abort: {after}");
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            Log($"step {step}: infinite propellant cheat back off: {!CheatOptions.InfinitePropellant}; ignore max temperature cheat back off: {!CheatOptions.IgnoreMaxTemperature}");
            step++;
            outcome.Ok = before == after;
            if (!outcome.Ok) Fail("step " + step, "MechJeb settings changed: before " + before);
        }

        // ------------------------------------------------------------------ (a) overheating

        private IEnumerator ProofOverheat(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;

            // Above 1 g works: the planner on this ship at 2 g (its maximum is 3.17 g).
            module.FlipStyleIndex = 0;   // cut, flip, relight
            module.SelectTripType(TripType.FullBurn);
            module.AccelerationG.Val = 2.0;
            yield return new WaitForSecondsRealtime(SettleSeconds);
            int planStep = step;
            yield return Capture(step, "planner-bebop-2g.png", new CaptureResult(), () => LogPlanner(planStep, module, f.Ship));
            step++;

            // 1 g with infinite propellant only: the drive overheats; the autopilot must drop to what the ship can sustain,
            // re-plan, say so, and keep flying.
            module.AccelerationG.Val = 1.0;
            CheatOptions.InfinitePropellant = true;
            CheatOptions.IgnoreMaxTemperature = false;
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (a) overheating run, full burn to Duna at {module.AccelerationG.Val} g, infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}, planner can start: {module.Planner.CanStart}");
            string before = MechJebSettingsFingerprint(f);
            string problem = module.PressStart();
            if (problem != null)
            {
                Fail("step " + step, "Start refused: " + problem);
                yield break;
            }

            LogPlan(pilot);
            step++;
            yield return Fly(pilot, () => pilot.Replans >= 1, 900, "overheat and re-plan");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            if (pilot.Replans == 0) { Fail("step " + step, "no re-plan happened"); yield break; }
            Log($"step {step}: re-planned: now holding {pilot.CurrentAcceleration / 9.81:F2} g. Notice: {pilot.Notice}");
            yield return Shot("a-1-overheat-replanned.png", f);
            yield return Fly(pilot, () => pilot.Replans >= 2, 900, "keep flying; re-plan again if still too hot");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            Log($"step {step}: re-plans so far {pilot.Replans}, now holding {pilot.CurrentAcceleration / 9.81:F2} g. Notice: {pilot.Notice}");
            yield return Shot("a-2-overheat-replanned-again.png", f);
            double t0 = Planetarium.GetUniversalTime();
            yield return Fly(pilot, () => Planetarium.GetUniversalTime() - t0 > 120, 300, "fly on at the reduced acceleration");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }

            module.PressAbort();
            Log($"step {step}: Abort pressed. {pilot.StopReason} {pilot.SettingsCheck}");
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(f);
            yield return Shot("a-3-overheat-after-abort.png", f);
            string after = MechJebSettingsFingerprint(f);
            Log($"step {step}: MechJeb settings files {(before == after ? "byte-identical" : "DIFFERENT")} before Start and after Abort: {after}");
            CheatOptions.InfinitePropellant = false;
            Log($"step {step}: infinite propellant cheat back off: {!CheatOptions.InfinitePropellant}");
            step++;
            outcome.Ok = before == after;
            if (!outcome.Ok) Fail("step " + step, "MechJeb settings changed: before " + before);
        }

        // ------------------------------------------------------------------ (b)

        private IEnumerator ProofCoast(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            CheatOptions.InfinitePropellant = false;
            // Back to the shipped warp limit (the flip run raised it with overheating ignored).
            MechJebModuleFlipAndBurnAutopilot.MaxPhysicsRate = 10;
            if (TimeWarp.fetch != null) TimeWarp.fetch.physicsWarpRates[3] = 25;
            // No cheats: a real trip, so at an acceleration the Bebop can hold without overheating.
            module.AccelerationG.Val = CoastAcceleration;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (b) coast trip to Duna at {CoastAcceleration} g, infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}, planner can start: {module.Planner.CanStart}");
            string before = MechJebSettingsFingerprint(f);
            string problem = module.PressStart();
            if (problem != null)
            {
                Fail("step " + step, "Start refused: " + problem);
                yield break;
            }

            LogPlan(pilot);
            step++;
            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Coast, 3600, "burn up to the coast");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            Log($"step {step}: coast reached at UT {F(Planetarium.GetUniversalTime())}");
            yield return Fly(pilot, () => TimeWarp.CurrentRate > 1.01f && TimeWarp.fetch.Mode == TimeWarp.Modes.HIGH, 600, "normal warp in the coast");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            double warpStart = Planetarium.GetUniversalTime();
            yield return Fly(pilot, () => Time.realtimeSinceStartup - (float)lastStatusReal > 0 && Planetarium.GetUniversalTime() - warpStart > 20000, 120, "coast under warp");
            yield return Shot("b-1-coast.png", f);
            module.PressAbort();
            Log($"step {step}: Abort pressed. {pilot.StopReason} {pilot.SettingsCheck}");
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(f);
            yield return Shot("b-2-after-abort.png", f);
            string after = MechJebSettingsFingerprint(f);
            Log($"step {step}: MechJeb settings files {(before == after ? "byte-identical" : "DIFFERENT")} before Start and after Abort: {after}");
            step++;
            outcome.Ok = before == after;
            if (!outcome.Ok) Fail("step " + step, "MechJeb settings changed: before " + before);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Let the trip fly until <paramref name="until"/> holds, logging status every few real seconds.</summary>
        private IEnumerator Fly(MechJebModuleFlipAndBurnAutopilot pilot, Func<bool> until, float timeoutReal, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (pilot.Running)
            {
                bool done;
                try
                {
                    done = until();
                }
                catch (Exception)
                {
                    done = false;
                }

                Status(pilot, what);
                if (done) break;
                if (Time.realtimeSinceStartup - start > timeoutReal)
                {
                    Log($"status: waiting for '{what}' timed out after {timeoutReal:F0} s real");
                    break;
                }

                yield return new WaitForFixedUpdate();
            }

            if (!pilot.Running) Log("status: trip not running: " + pilot.StopReason);
        }

        private void Status(MechJebModuleFlipAndBurnAutopilot pilot, string what)
        {
            GuidanceCommand c = pilot.Last;
            if (c == null) return;
            bool thrusting = c.Acceleration > 0 && c.Phase != GuidancePhase.Flip && c.Phase != GuidancePhase.Coast && pilot.PointingError < 1 &&
                             pilot.MeasuredAcceleration > 0.5 * c.Acceleration;
            if (thrusting)
                statusMaxAccelError = Math.Max(statusMaxAccelError, Math.Abs(pilot.MeasuredAcceleration / c.Acceleration - 1));
            if (c.Phase != GuidancePhase.Flip) statusMaxPointing = Math.Max(statusMaxPointing, pilot.PointingError);

            float now = Time.realtimeSinceStartup;
            if (now - lastStatusReal < StatusEvery) return;
            lastStatusReal = now;
            Vessel v = FlightGlobals.ActiveVessel;
            Log($"status [{what}]: UT {Planetarium.GetUniversalTime():F1} phase {c.Phase}{(c.Departing ? " (climbing out)" : "")}{(c.Correcting ? " (correcting)" : "")} " +
                $"accel {pilot.MeasuredAcceleration:F3} m/s^2 = {pilot.MeasuredAcceleration / 9.81:F4} g (target {c.Acceleration / 9.81:F4} g) " +
                $"pointing error {pilot.PointingError:F2} deg throttle {v.ctrlState.mainThrottle:F3} " +
                $"warp {(TimeWarp.CurrentRate > 1.01f ? (TimeWarp.fetch.Mode == TimeWarp.Modes.HIGH ? "normal" : "physics") : "none")} x{TimeWarp.CurrentRate:0.##} " +
                $"toFlip {c.TimeToFlip:F0} s toBurn {c.TimeToBurn:F0} s toArrival {c.TimeToArrival:F0} s speed vs {v.mainBody.bodyName} {v.obt_speed:F1} m/s " +
                $"| {pilot.WarpStatus} | {DriveState(v)}, hottest part {pilot.HottestPart} {pilot.HottestFraction * 100:F1}% of limit, parts {v.parts.Count}");
        }

        // KSP Interstellar fusion drive: how much of its power it gets (fusionRatio), its live maximum thrust, and how full the
        // ship's waste heat and power bars are. It loses thrust when power or cooling falls short.
        private static string DriveState(Vessel v)
        {
            try
            {
                var bits = new List<string>();
                foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                    if (m.GetType().Name == "DaedalusEngineController")
                        bits.Add($"fusionRatio {Member(m, "fusionRatio")} maxThrust {Member(m, "effectiveMaxThrustInKiloNewton")} kN power {Member(m, "powerUsage")}");
                foreach (string res in new[] { "WasteHeat", "Megajoules" })
                {
                    double amount = 0, max = 0;
                    foreach (Part p in v.parts)
                    {
                        PartResource r = p.Resources.Get(res);
                        if (r == null) continue;
                        amount += r.amount;
                        max += r.maxAmount;
                    }

                    if (max > 0) bits.Add($"{res} {amount / max * 100:F1}%");
                }

                return string.Join(", ", bits);
            }
            catch (Exception e)
            {
                return "drive state unavailable: " + e.Message;
            }
        }

        private static void LogPlan(MechJebModuleFlipAndBurnAutopilot pilot)
        {
            TripStart t = pilot.Trip;
            Log($"plan: target {t.Target.bodyName}, {t.Trip}, {t.Acceleration / 9.81:F2} g, flip {t.Flip}, parking {t.ParkingAltitude:F0} m, " +
                $"planned trip {t.PlannedTripTime:F0} s, coast budget {t.DeltaVBudget:F0} m/s, turn rate {pilot.TurnRate * 180 / Math.PI:F2} deg/s, " +
                $"started at UT {Planetarium.GetUniversalTime():F1}");
        }

        private static void LogAfterAbort(Flown f)
        {
            Vessel v = f.Ship;
            MechJebModuleThrustController thrust = f.Core.Thrust;
            Log($"after abort: running {f.Module.Autopilot.Running}, throttle {v.ctrlState.mainThrottle:F3}, warp x{TimeWarp.CurrentRate}, " +
                $"physics rates [{string.Join(",", TimeWarp.fetch.physicsWarpRates)}], thrust users {thrust.Users.Count}, attitude users {f.Core.Attitude.Users.Count}, " +
                $"LimitAcceleration {thrust.LimitAcceleration}, MaxAcceleration {thrust.MaxAcceleration.Val}, " +
                $"accel {v.geeForce_immediate * 9.80665:F3} m/s^2");
        }

        private IEnumerator Shot(string file, Flown f)
        {
            var r = new CaptureResult();
            int s = step;
            yield return Capture(step, file, r, () =>
            {
                MechJebModuleFlipAndBurnAutopilot p = f.Module.Autopilot;
                Log($"screenshot {file}: phase {p.Last?.Phase} running {p.Running} pointing {p.PointingError:F2} accel {p.MeasuredAcceleration:F3} warp {p.WarpStatus} stop '{p.StopReason}'");
            });
            if (!r.Ok) Log("step " + s + ": screenshot " + file + " failed: " + r.Error);
            step++;
        }

        /// <summary>
        /// Make MechJeb write its settings files now (its own save routine) and fingerprint them, plus the settings it keeps
        /// in the vessel.
        /// </summary>
        private static string MechJebSettingsFingerprint(Flown f)
        {
            f.Core.OnSave(null);
            var local = new ConfigNode("local");
            f.Core.OnSave(local);
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2");
            var parts = new List<string>();
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string file in Directory.GetFiles(folder, "mechjeb_settings_*.cfg").OrderBy(x => x))
                    parts.Add(Path.GetFileName(file) + "=" + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").Substring(0, 16));
                parts.Add("vessel-local=" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(local.ToString()))).Replace("-", "").Substring(0, 16));
            }

            return string.Join(" ", parts);
        }
    }
}
