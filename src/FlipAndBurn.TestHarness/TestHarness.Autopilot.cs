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
        private static float statusEvery = 2f;           // real seconds between status lines (the whole trips use more)
        private const double CoastAcceleration = 0.4;    // g, for (b): below what the Bebop's drive sustains (it was still cooling at 0.48 g)

        private double lastStatusReal, statusMaxAccelError, statusMaxPointing;
        // Called every physics step while a trip flies (the whole-trip proofs watch the throttle and the pointing).
        private Action<MechJebModuleFlipAndBurnAutopilot> stepWatch;

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
            beforeSettingsText = lastSettingsText;
            LogBeforeStart(f);

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
                SetTripTopRate(pilot, rate);
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

            SetTripTopRate(pilot, best);
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
            Log($"step {step}: MechJeb settings files before Start and after Abort: {SettingsVerdict()} ({lastSettingsText.Count} files)");
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            Log($"step {step}: infinite propellant cheat back off: {!CheatOptions.InfinitePropellant}; ignore max temperature cheat back off: {!CheatOptions.IgnoreMaxTemperature}");
            step++;
            if (before != after) LogSettingsDiff();
            outcome.Ok = true;   // the settings verdict is in the log; carry on with the other proofs
        }

        // ------------------------------------------------------------------ diagnostic: does the engine cut?

        /// <summary>Everything that decides an engine's throttle, for each running engine.</summary>
        private static string EngineInternals(Vessel v)
        {
            var bits = new List<string> { $"ctrl {v.ctrlState.mainThrottle:F3} input {FlightInputHandler.state.mainThrottle:F3}" };
            foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited) continue;
                bits.Add($"{e.part.partInfo.name}: requested {e.requestedThrottle:F3} current {e.currentThrottle:F3} locked {e.throttleLocked} " +
                         $"independent {e.independentThrottle} ({e.independentThrottlePercentage:F1}%) response {e.useEngineResponseTime} " +
                         $"accel {e.engineAccelerationSpeed:F3} decel {e.engineDecelerationSpeed:F3} finalThrust {e.finalThrust:F1} kN maxThrust {e.maxThrust:F1} kN");
            }

            return string.Join(" | ", bits);
        }

        private IEnumerator CutTest(Flown f, string label, bool ignoreTemperature, float warp)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            module.FlipStyleIndex = 0;
            module.SelectTripType(TripType.FullBurn);
            module.AccelerationG.Val = 1.0;
            CheatOptions.InfinitePropellant = true;
            CheatOptions.IgnoreMaxTemperature = ignoreTemperature;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 1;
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"cut test {label}: ignore max temperature {CheatOptions.IgnoreMaxTemperature}, warp x{warp}");
            LogBeforeStart(f);
            string problem = module.PressStart();
            if (problem != null) { Log($"cut test {label}: Start refused: {problem}"); yield break; }
            yield return Fly(pilot, () => pilot.Last != null && !pilot.Last.Departing && pilot.PointingError < 1 &&
                                          Math.Abs(pilot.MeasuredAcceleration / pilot.Last.Acceleration - 1) < 0.02, 300, $"cut test {label}: settle");
            if (warp > 1)
            {
                SetTripTopRate(pilot, warp);
                float t0 = Time.realtimeSinceStartup;
                yield return Fly(pilot, () => Time.realtimeSinceStartup - t0 > 30, 40, $"cut test {label}: warp x{warp}");
            }

            Log($"cut test {label}: before Abort: accel {pilot.MeasuredAcceleration:F3} | {EngineInternals(f.Ship)}");
            module.PressAbort();
            for (int i = 1; i <= 5; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                Log($"cut test {label}: {i} s after Abort: accel {f.Ship.geeForce_immediate * 9.80665:F3} warp x{TimeWarp.CurrentRate} | {EngineInternals(f.Ship)}");
            }

            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
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
            beforeSettingsText = lastSettingsText;
            LogBeforeStart(f);
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
            Log($"step {step}: MechJeb settings files before Start and after Abort: {SettingsVerdict()} ({lastSettingsText.Count} files)");
            CheatOptions.InfinitePropellant = false;
            Log($"step {step}: infinite propellant cheat back off: {!CheatOptions.InfinitePropellant}");
            step++;
            if (before != after) LogSettingsDiff();
            outcome.Ok = true;   // the settings verdict is in the log; carry on with the other proofs
        }

        // ------------------------------------------------------------------ (b)

        private IEnumerator ProofCoast(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            CheatOptions.InfinitePropellant = false;
            // Back to the shipped warp limit (the flip run raised it with overheating ignored).
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
            // No cheats: a real trip, so at an acceleration the Bebop can hold without overheating.
            module.AccelerationG.Val = CoastAcceleration;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (b) coast trip to Duna at {CoastAcceleration} g, infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}, planner can start: {module.Planner.CanStart}");
            string before = MechJebSettingsFingerprint(f);
            beforeSettingsText = lastSettingsText;
            LogBeforeStart(f);
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
            Log($"step {step}: MechJeb settings files before Start and after Abort: {SettingsVerdict()} ({lastSettingsText.Count} files)");
            step++;
            if (before != after) LogSettingsDiff();
            outcome.Ok = true;   // the settings verdict is in the log; carry on with the other proofs
        }

        // ------------------------------------------------------------------ warp rates and throttle users (cycle 9 review)

        /// <summary>
        /// Start, a minute of burn, Abort: the player's warp rates and MechJeb throttle users are as before Start, and the
        /// player's throttle works. Then Start again and leave the flight scene mid-trip: the rates are back there too.
        /// </summary>
        private IEnumerator ProofRates(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            module.FlipStyleIndex = 0;
            // No cheats: a coast trip (a full burn needs more delta-v than the Bebop has) at an acceleration it holds
            // without overheating. Both start with a burn.
            module.SelectTripType(TripType.CoastInTheMiddle);
            module.AccelerationG.Val = CoastAcceleration;
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (r) warp rates and throttle users, coast trip to Duna at {module.AccelerationG.Val} g, TimeWarp object {TimeWarp.fetch.GetInstanceID()}");
            string before = MechJebSettingsFingerprint(f);
            beforeSettingsText = lastSettingsText;
            LogBeforeStart(f);
            string problem = module.PressStart();
            if (problem != null) { Fail("step " + step, "Start refused: " + problem); yield break; }
            LogPlan(pilot);
            Log($"step {step}: during the trip: physics rates {Rates(TimeWarp.fetch.physicsWarpRates)}, thrust users {Users(f.Core.Thrust)}, attitude users {Users(f.Core.Attitude)}");
            step++;

            double burnSince = double.NaN;
            yield return Fly(pilot, () =>
            {
                bool burning = pilot.Last != null && pilot.MeasuredAcceleration > 0.5 * pilot.Last.Acceleration;
                if (burning && double.IsNaN(burnSince)) burnSince = Planetarium.GetUniversalTime();
                return burning && Planetarium.GetUniversalTime() - burnSince > 60;
            }, 600, "a minute of burn");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            Log($"step {step}: a minute of burn done: physics rates {Rates(TimeWarp.fetch.physicsWarpRates)}, thrust users {Users(f.Core.Thrust)}, warp x{TimeWarp.CurrentRate}");

            module.PressAbort();
            Log($"step {step}: Abort pressed. {pilot.StopReason} {pilot.SettingsCheck}");
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(f);
            MechJebSettingsFingerprint(f);
            Log($"step {step}: MechJeb settings files before Start and after Abort: {SettingsVerdict()} ({lastSettingsText.Count} files)");
            step++;

            // The player's throttle, as the throttle keys set it: the ship must follow it with no MechJeb throttle control left on.
            Vessel v = f.Ship;
            foreach (float want in new[] { 0.3f, 0f })
            {
                FlightInputHandler.state.mainThrottle = want;
                for (int i = 1; i <= 3; i++)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    Log($"manual throttle {want:F1}, {i} s: accel {v.geeForce_immediate * 9.80665:F3} m/s^2 | {EngineInternals(v)}");
                }
            }

            bool manual = Math.Abs(v.ctrlState.mainThrottle) < 0.001;
            Log($"step {step}: manual throttle after Abort: the ship followed the player's throttle to 0.3 and back to 0 (see the lines above); ends at {v.ctrlState.mainThrottle:F3}: {(manual ? "ok" : "NOT ok")}");
            step++;

            // Leave the flight scene mid-trip, as the Space Center button does.
            problem = module.PressStart();
            if (problem != null) { Fail("step " + step, "second Start refused: " + problem); yield break; }
            yield return Fly(pilot, () => pilot.Last != null && pilot.MeasuredAcceleration > 0.5 * pilot.Last.Acceleration, 600, "burning again");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            Log($"step {step}: trip running again, physics rates {Rates(TimeWarp.fetch.physicsWarpRates)}; leaving for the Space Center mid-trip");
            int flightTimeWarp = TimeWarp.fetch.GetInstanceID();
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            float deadline = Time.realtimeSinceStartup + 300;
            while (!(HighLogic.LoadedScene == GameScenes.SPACECENTER && TimeWarp.fetch != null && TimeWarp.fetch.GetInstanceID() != flightTimeWarp) &&
                   Time.realtimeSinceStartup < deadline)
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);   // BetterTimeWarp sets its rates when the scene's GUI is ready
            Log($"step {step}: in scene {HighLogic.LoadedScene}, autopilot running {pilot.Running}, stop reason '{pilot.StopReason}', {pilot.SettingsCheck}");
            Log($"step {step}: Space Center: TimeWarp object {(TimeWarp.fetch == null ? "none" : TimeWarp.fetch.GetInstanceID().ToString())} (flight's was {flightTimeWarp}), " +
                $"physics rates {Rates(TimeWarp.fetch?.physicsWarpRates)}, values {(SameValues(TimeWarp.fetch?.physicsWarpRates) ? "same as" : "DIFFERENT from")} before Start {ratesBeforeStart}");
            step++;
            outcome.Ok = !pilot.Running && manual;
            if (!outcome.Ok) Fail("step " + step, pilot.Running ? "the trip kept running after leaving the flight scene" : "the ship didn't follow the player's throttle after Abort");
        }

        private static bool SameValues(float[] now) => ratesBeforeStart != null && now != null && ratesBeforeStart.ToString() == Rates(now);

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

                stepWatch?.Invoke(pilot);
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
            if (now - lastStatusReal < statusEvery) return;
            lastStatusReal = now;
            Vessel v = FlightGlobals.ActiveVessel;
            Log($"status [{what}]: UT {Planetarium.GetUniversalTime():F1} phase {c.Phase}{(c.Departing ? " (climbing out)" : "")}{(c.Correcting ? " (correcting)" : "")} " +
                $"accel {pilot.MeasuredAcceleration:F3} m/s^2 = {pilot.MeasuredAcceleration / 9.81:F4} g (target {c.Acceleration / 9.81:F4} g) " +
                $"pointing error {pilot.PointingError:F2} deg throttle {v.ctrlState.mainThrottle:F3} engine throttle {EngineThrottle(v):F3} " +
                $"warp {(TimeWarp.CurrentRate > 1.01f ? (TimeWarp.fetch.Mode == TimeWarp.Modes.HIGH ? "normal" : "physics") : "none")} x{TimeWarp.CurrentRate:0.##} " +
                $"toFlip {c.TimeToFlip:F0} s toBurn {c.TimeToBurn:F0} s toArrival {c.TimeToArrival:F0} s speed vs {v.mainBody.bodyName} {v.obt_speed:F1} m/s " +
                $"| {pilot.WarpStatus} | {DriveState(v)}, hottest part {pilot.HottestPart} {pilot.HottestFraction * 100:F1}% of limit, parts {v.parts.Count}" +
                (c.Phase == GuidancePhase.Flip || c.Phase == GuidancePhase.Coast || what.Contains("flip") ? " | " + EngineInternals(v) : ""));
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

        // What the engines are actually doing (they follow the commanded throttle at their own pace).
        private static float EngineThrottle(Vessel v)
        {
            float max = 0;
            foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
                if (e.EngineIgnited) max = Math.Max(max, e.currentThrottle);
            return max;
        }

        private static void LogPlan(MechJebModuleFlipAndBurnAutopilot pilot)
        {
            TripStart t = pilot.Trip;
            Log($"plan: target {t.Target.bodyName}, {t.Trip}, {t.Acceleration / 9.81:F2} g, flip {t.Flip}, parking {t.ParkingAltitude:F0} m, " +
                $"planned trip {t.PlannedTripTime:F0} s, coast budget {t.DeltaVBudget:F0} m/s, turn rate {pilot.TurnRate * 180 / Math.PI:F2} deg/s, " +
                $"started at UT {Planetarium.GetUniversalTime():F1}");
        }

        // The player's physics warp rates and MechJeb's controller users just before Start, to compare after Abort.
        private static WarpRatesSnapshot ratesBeforeStart;
        private static string thrustUsersBeforeStart, attitudeUsersBeforeStart;

        /// <summary>Who is using a MechJeb controller. The thrust controller always lists itself: MechJeb adds it when it starts.</summary>
        private static string Users(ComputerModule m) => $"{m.Users.Count} [{string.Join(", ", m.Users.Select(u => u.GetType().Name))}]";

        private static string Rates(float[] r) => r == null ? "none" : "[" + string.Join(",", r) + "]";

        private static void LogBeforeStart(Flown f)
        {
            ratesBeforeStart = WarpRatesSnapshot.Take(TimeWarp.fetch.physicsWarpRates);
            thrustUsersBeforeStart = Users(f.Core.Thrust);
            attitudeUsersBeforeStart = Users(f.Core.Attitude);
            Log($"before start: physics rates {ratesBeforeStart}, rails rates {Rates(TimeWarp.fetch.warpRates)}, thrust users {thrustUsersBeforeStart}, " +
                $"attitude users {attitudeUsersBeforeStart}, LimitAcceleration {f.Core.Thrust.LimitAcceleration}, MaxAcceleration {f.Core.Thrust.MaxAcceleration.Val}");
        }

        /// <summary>
        /// The test helper writes a warp rate only into the trip's own rates (the autopilot swaps them in at Start and drops them at
        /// the end), never into the player's.
        /// </summary>
        private static void SetTripTopRate(MechJebModuleFlipAndBurnAutopilot pilot, float rate)
        {
            if (!pilot.Running || ratesBeforeStart == null || ReferenceEquals(TimeWarp.fetch.physicsWarpRates, ratesBeforeStart.Array))
            {
                Log($"warp rate x{rate} not set: no trip is running, so these are the player's rates");
                return;
            }

            TimeWarp.fetch.physicsWarpRates[3] = rate;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = rate;
        }

        private static void LogAfterAbort(Flown f)
        {
            Vessel v = f.Ship;
            MechJebModuleThrustController thrust = f.Core.Thrust;
            Log($"after abort: {EngineInternals(v)}");
            Log($"after abort: running {f.Module.Autopilot.Running}, throttle {v.ctrlState.mainThrottle:F3}, engine throttle {EngineThrottle(v):F3}, warp x{TimeWarp.CurrentRate}, " +
                $"physics rates {Rates(TimeWarp.fetch.physicsWarpRates)}, thrust users {Users(thrust)}, attitude users {Users(f.Core.Attitude)}, " +
                $"LimitAcceleration {thrust.LimitAcceleration}, MaxAcceleration {thrust.MaxAcceleration.Val}, " +
                $"accel {v.geeForce_immediate * 9.80665:F3} m/s^2");
            CheckRatesAndUsers(f, "after abort");
        }

        /// <summary>Warp rates and controller users as they were before Start? If the rates aren't, put them back and say so.</summary>
        private static void CheckRatesAndUsers(Flown f, string when)
        {
            float[] now = TimeWarp.fetch.physicsWarpRates;
            bool same = ratesBeforeStart != null && ratesBeforeStart.Matches(now);
            // After a quickload the flight scene is new, with its own array: the player's values in it are what count.
            bool newScene = !same && ratesBeforeStart != null && ratesBeforeStart.SameValues(now);
            bool rates = same || newScene;
            Log($"{when}: physics warp rates {(same ? "same as before Start (same array, same values)" : newScene ? "same values as before Start (a new flight scene since Start, so a new array)" : "DIFFERENT from before Start")}: " +
                $"before {ratesBeforeStart}, now {Rates(now)}");
            if (!rates && ratesBeforeStart != null)
            {
                TimeWarp.fetch.physicsWarpRates = ratesBeforeStart.Restore();
                Log($"{when}: the test helper put the physics warp rates back to {ratesBeforeStart}");
            }

            if (f.Core == null) return;
            string thrust = Users(f.Core.Thrust), attitude = Users(f.Core.Attitude);
            Log($"{when}: thrust users {(thrust == thrustUsersBeforeStart ? "same as" : "DIFFERENT from")} before Start: before {thrustUsersBeforeStart}, now {thrust}; " +
                $"attitude users {(attitude == attitudeUsersBeforeStart ? "same as" : "DIFFERENT from")} before Start: before {attitudeUsersBeforeStart}, now {attitude}");
        }

        private IEnumerator Shot(string file, Flown f)
        {
            var r = new CaptureResult();
            int s = step;
            yield return Capture(step, file, r, () =>
            {
                if (f?.Module == null) { Log($"screenshot {file}"); return; }
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
        private static Dictionary<string, string> lastSettingsText = new Dictionary<string, string>();
        private static Dictionary<string, string> beforeSettingsText = new Dictionary<string, string>();

        /// <summary>
        /// "identical", "identical apart from window sizes" (MechJeb records each window's position and size, which
        /// follow what the window shows), or "DIFFERENT".
        /// </summary>
        private static string SettingsVerdict()
        {
            bool onlyWindows = true, any = false;
            foreach (KeyValuePair<string, string> kv in lastSettingsText)
            {
                if (!beforeSettingsText.TryGetValue(kv.Key, out string was)) { onlyWindows = false; any = true; continue; }
                if (was == kv.Value) continue;
                any = true;
                string[] a = was.Split('\n'), b = kv.Value.Split('\n');
                if (a.Length != b.Length) { onlyWindows = false; continue; }
                for (int i = 0; i < a.Length; i++)
                    if (a[i] != b[i] && !(a[i].Trim().StartsWith("WindowVector") && b[i].Trim().StartsWith("WindowVector")))
                        onlyWindows = false;
            }

            return !any ? "byte-identical" : onlyWindows ? "identical apart from window sizes" : "DIFFERENT";
        }

        /// <summary>Log every line that differs between the settings files at Start and after Abort.</summary>
        private static void LogSettingsDiff()
        {
            foreach (KeyValuePair<string, string> kv in lastSettingsText)
            {
                if (!beforeSettingsText.TryGetValue(kv.Key, out string was) || was == kv.Value) continue;
                string[] a = was.Split('\n'), b = kv.Value.Split('\n');
                int shown = 0;
                for (int i = 0; i < Math.Max(a.Length, b.Length) && shown < 30; i++)
                {
                    string x = i < a.Length ? a[i].TrimEnd() : "(none)", y = i < b.Length ? b[i].TrimEnd() : "(none)";
                    if (x == y) continue;
                    // The node a line is in: the nearest unindented-ish name above it.
                    string node = "";
                    for (int j = i; j >= 0 && j < a.Length; j--)
                        if (a[j].Trim().Length > 0 && !a[j].Contains("=") && a[j].Trim() != "{" && a[j].Trim() != "}") { node = a[j].Trim(); break; }
                    Log($"settings diff {kv.Key} line {i + 1} [{node}]: before '{x.Trim()}' after '{y.Trim()}'");
                    shown++;
                }
            }
        }

        private static string MechJebSettingsFingerprint(Flown f)
        {
            lastSettingsText = new Dictionary<string, string>();
            f.Core.OnSave(null);
            var local = new ConfigNode("local");
            f.Core.OnSave(local);
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2");
            var parts = new List<string>();
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string file in Directory.GetFiles(folder, "mechjeb_settings_*.cfg").OrderBy(x => x))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    lastSettingsText[Path.GetFileName(file)] = System.Text.Encoding.UTF8.GetString(bytes);
                    parts.Add(Path.GetFileName(file) + "=" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 16));
                }
                parts.Add("vessel-local=" + BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(local.ToString()))).Replace("-", "").Substring(0, 16));
            }

            return string.Join(" ", parts);
        }
    }
}
