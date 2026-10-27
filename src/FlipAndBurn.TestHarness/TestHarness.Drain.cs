using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 11 investigation: where does propellant go when the engine isn't firing? Trip B lost about 0.9 t of the Bebop's
    // 11.2 t of liquid deuterium over a 19-day coast, other than for thrust. The Bebop coasts in orbit, no trip running,
    // while every resource on every part is logged at regular game-time intervals, in three set-ups:
    //  1. the engine as a player leaves it (active, throttle at zero);
    //  2. the engine shut down, as Flip and Burn's coast does it;
    //  3. shut down, with the ship's electric charge emptied (does the loss depend on power?).
    public partial class TestHarness
    {
        private const double DrainHours = 24;          // game hours per set-up
        private const double DrainSampleHours = 3;

        private IEnumerator ProofDrain(Flown f, CaptureResult outcome)
        {
            Vessel v = f.Ship;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            HashSet<string> propellants = new HashSet<string>(v.FindPartModulesImplementing<ModuleEngines>()
                .SelectMany(e => e.propellants.Select(p => p.name)));
            Log($"drain: engine propellants: {string.Join(", ", propellants)}; resources on the ship: " +
                string.Join(", ", v.parts.SelectMany(p => p.Resources.Cast<PartResource>().Select(r => r.resourceName)).Distinct()));
            DumpModules(v, propellants, "at load");

            yield return DrainPhase(v, "1 engine active, throttle zero (as a player leaves it)", propellants);

            foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited) continue;
                e.requestedThrottle = 0f;
                e.currentThrottle = 0f;
                e.Shutdown();
            }

            yield return new WaitForSecondsRealtime(3f);
            DumpModules(v, propellants, "engine shut down");
            yield return DrainPhase(v, "2 engine shut down (as Flip and Burn's coast does it)", propellants);

            foreach (Part p in v.parts)
            {
                PartResource ec = p.Resources.Get("ElectricCharge");
                if (ec != null) ec.amount = 0;
            }

            yield return new WaitForSecondsRealtime(3f);
            DumpModules(v, propellants, "engine shut down, electric charge emptied");
            yield return DrainPhase(v, "3 engine shut down, no electric charge", propellants);
            outcome.Ok = true;
        }

        private IEnumerator DrainPhase(Vessel v, string name, HashSet<string> propellants)
        {
            // Normal ("on rails") warp, as a coast is flown: 1000x.
            TimeWarp tw = TimeWarp.fetch;
            int index = Array.FindIndex(tw.warpRates, r => r >= 999f);
            tw.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(index < 0 ? tw.warpRates.Length - 1 : index, false);
            double start = Planetarium.GetUniversalTime(), next = start;
            Dictionary<string, double> first = null;
            Dictionary<string, double> last = null;
            Log($"drain [{name}]: start at UT {start:F0}, warp x{tw.warpRates[Math.Max(0, index)]}");
            while (Planetarium.GetUniversalTime() - start < DrainHours * 3600)
            {
                double ut = Planetarium.GetUniversalTime();
                if (ut >= next)
                {
                    next += DrainSampleHours * 3600;
                    Dictionary<string, double> now = Totals(v);
                    first ??= now;
                    last = now;
                    Log($"drain [{name}] {(ut - start) / 3600,5:F1} h: " + string.Join(", ", now.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:F2}")) +
                        " | per tank: " + string.Join("; ", PerPart(v, propellants)));
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            TimeWarp.SetRate(0, true);
            yield return new WaitForSecondsRealtime(3f);
            Dictionary<string, double> end = Totals(v);
            double hours = (Planetarium.GetUniversalTime() - start) / 3600;
            foreach (string res in end.Keys.Union(first.Keys).OrderBy(k => k))
            {
                double a = first.TryGetValue(res, out double x) ? x : 0, b = end.TryGetValue(res, out double y) ? y : 0;
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(res);
                double kg = (b - a) * (def?.density ?? 0) * 1000;
                Log($"drain [{name}] result: {res} {a:F3} -> {b:F3} units over {hours:F1} game h: {(b - a) / hours:+0.000;-0.000} units/h" +
                    (def != null && def.density > 0 ? $", {kg / hours:+0.000;-0.000} kg/h" : ""));
            }
        }

        private static Dictionary<string, double> Totals(Vessel v)
        {
            var totals = new Dictionary<string, double>();
            foreach (Part p in v.parts)
            foreach (PartResource r in p.Resources)
                totals[r.resourceName] = (totals.TryGetValue(r.resourceName, out double a) ? a : 0) + r.amount;
            return totals;
        }

        private static IEnumerable<string> PerPart(Vessel v, HashSet<string> propellants)
        {
            foreach (Part p in v.parts)
            foreach (PartResource r in p.Resources)
                if (propellants.Contains(r.resourceName))
                    yield return $"{p.partInfo.title} #{p.persistentId} {r.resourceName} {r.amount:F2}/{r.maxAmount:F0}";
        }

        // Every module on the engine parts and the parts holding propellant, with the fields that could matter: boil-off,
        // cooling, power, temperature, whether it's running.
        private static void DumpModules(Vessel v, HashSet<string> propellants, string when)
        {
            string[] keys = { "boil", "cool", "power", "temp", "enable", "active", "throttle", "fuel", "consum", "rate", "insul", "cryo", "loss", "mode", "state" };
            foreach (Part p in v.parts)
            {
                bool tank = p.Resources.Cast<PartResource>().Any(r => propellants.Contains(r.resourceName));
                bool engine = p.Modules.OfType<ModuleEngines>().Any();
                if (!tank && !engine) continue;
                foreach (PartModule m in p.Modules)
                {
                    var bits = new List<string>();
                    foreach (BaseField field in m.Fields)
                    {
                        string n = field.name.ToLowerInvariant();
                        if (!keys.Any(k => n.Contains(k))) continue;
                        object value;
                        try { value = field.GetValue(m); } catch { value = "?"; }
                        bits.Add($"{field.name}={value}");
                    }

                    Log($"drain modules [{when}]: {p.partInfo.title} #{p.persistentId} {m.GetType().Name} (enabled {m.isEnabled}): {string.Join(", ", bits.Take(40))}");
                }
            }
        }

        // ------------------------------------------------------------------ the coast trip, measured

        // Trip B's set-up (coast to Duna at 0.4 g, no cheats), flown to the start of braking, with the propellant, power and
        // the cryostats logged as it goes; then Abort.
        private IEnumerator ProofCoastDrain(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 0.4;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Planner.Refresh(f.Ship);
            string problem = module.PressStart();
            if (problem != null) { Fail("coastdrain", "Start refused: " + problem); failed = true; yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            double start = Planetarium.GetUniversalTime(), next = start;
            float t0 = Time.realtimeSinceStartup;
            statusEvery = 30f;
            while (pilot.Running && Time.realtimeSinceStartup - t0 < 3 * 3600)
            {
                double ut = Planetarium.GetUniversalTime();
                GuidanceCommand c = pilot.Last;
                if (ut >= next)
                {
                    next = ut + (c != null && c.Phase == GuidancePhase.Coast ? 6 * 3600 : 900);
                    LogTankState(f.Ship, pilot, $"{(ut - start) / 3600:F2} h, {c?.Phase}");
                }

                if (c != null && c.Phase == GuidancePhase.Decelerate) break;
                yield return Fly(pilot, () => true, 1, "coast drain");
                yield return new WaitForFixedUpdate();
            }

            LogTankState(f.Ship, pilot, "braking starts");
            module.PressAbort();
            yield return new WaitForSecondsRealtime(3f);
            statusEvery = 2f;
            outcome.Ok = true;
        }

        // ------------------------------------------------------------------ the planner allowing for a measured loss

        // Trip B's set-up planned three ways: with no loss measured (no allowance, no note); with the loss the drain test
        // measured on the Bebop with its electric charge gone (0.3 kg/h: a slower trip, the note, 10% still kept); and with a
        // loss too big for any coast (refused before Start, with the cause). Only plans: nothing is flown.
        private IEnumerator ProofLossPlan(Flown f, CaptureResult outcome)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            CheatOptions.InfinitePropellant = false;
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 0.4;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            bool ok = true;
            CoastPlan none = null;
            foreach ((string name, double kgPerHour) in new[] { ("no loss measured", 0.0), ("0.299 kg/h (drain test, no electric charge)", 0.299), ("100 kg/h", 100.0) })
            {
                pilot.Loss.Restore(kgPerHour * 24, 24 * 3600);
                module.Planner.Refresh(f.Ship);
                Planner p = module.Planner;
                TripStart start = p.BuildTripStart();
                Log($"loss plan [{name}]: rate {pilot.Loss.RateKgPerSecond * 3600:F3} kg/h; coast feasible {p.Coast?.Feasible}; trip {p.Coast?.TripTime:F0} s; " +
                    $"delta-v needed {p.Coast?.DeltaVNeeded:F0} m/s; set aside {p.LossAllowance:F0} m/s; budget {start.DeltaVBudget:F0} of {start.StartDeltaV:F0} m/s; " +
                    $"reserve at arrival {(start.StartDeltaV - start.DeltaVNeeded - p.LossAllowance) / start.StartDeltaV * 100:F2}%; can start {p.CanStart}; blocked by the loss {p.LossBlocks}");
                foreach (string note in p.Notes) Log($"loss plan [{name}] note: {note}");
                foreach (KeyValuePair<string, string> row in p.TripRows) Log($"loss plan [{name}] row: {row.Key} = {row.Value}");
                bool noted = p.Notes.Any(n => n.Contains("boil-off"));
                if (kgPerHour == 0)
                {
                    none = p.Coast;
                    ok &= p.CanStart && p.LossAllowance == 0 && !noted;
                }
                else if (kgPerHour < 1)
                {
                    double reserve = (start.StartDeltaV - start.DeltaVNeeded - p.LossAllowance) / start.StartDeltaV;
                    ok &= p.CanStart && p.LossAllowance > 0 && noted && p.Coast.TripTime > none.TripTime && reserve >= Planner.Reserve - 1e-6;
                }
                else
                {
                    ok &= !p.CanStart && p.LossBlocks && noted;
                }

                Log($"loss plan [{name}]: {(ok ? "as expected" : "NOT as expected")}");
            }

            pilot.Loss.Restore(0, 0);
            module.Planner.Refresh(f.Ship);
            if (!ok) { Fail("lossplan", "the planner didn't allow for the measured loss as expected"); failed = true; }
            outcome.Ok = ok;
        }

        private static void LogTankState(Vessel v, MechJebModuleFlipAndBurnAutopilot pilot, string when) =>
            LogTankState(v, pilot, "coast drain", when, 0.4 * 9.80665);

        private static void LogTankState(Vessel v, MechJebModuleFlipAndBurnAutopilot pilot, string label, string when, double acceleration)
        {
            Dictionary<string, double> totals = Totals(v);
            double fuel = totals.TryGetValue("LqdDeuterium", out double d) ? d : 0;
            double density = PartResourceLibrary.Instance.GetDefinition("LqdDeuterium")?.density ?? 0;
            double boil = 0, power = 0, temp = 0, partTemp = 0;
            int n = 0;
            foreach (Part p in v.parts)
            {
                bool tank = false;
                foreach (PartModule m in p.Modules)
                {
                    if (m.GetType().Name != "ModuleStorageCryostat") continue;
                    if (m.Fields["boilOffRate"]?.GetValue(m) is double b) boil += b;
                    else if (m.Fields["boilOffRate"]?.GetValue(m) is float bf) boil += bf;
                    if (m.Fields["powerReqKW"]?.GetValue(m) is double kw) power += kw;
                    if (m.Fields["storedTemp"]?.GetValue(m) is double st) temp = st;
                    tank = true;
                }

                if (tank) { partTemp += p.temperature; n++; }
            }

            double reader = pilot.DeltaVNow(acceleration);
            Log($"{label} [{when}]: LqdDeuterium {fuel:F1} units ({fuel * density:F4} t), ship mass {v.totalMass:F3} t; ElectricCharge {(totals.TryGetValue("ElectricCharge", out double ec) ? ec : 0):F0}, " +
                $"Megajoules {(totals.TryGetValue("Megajoules", out double mj) ? mj : 0):F1}, WasteHeat {(totals.TryGetValue("WasteHeat", out double wh) ? wh : 0):F0}; " +
                $"cryostats: boil-off rate sum {boil:G4}, power wanted {power:F2} kW (all modules), stored temp {temp:F1} K, tank parts at {(n > 0 ? partTemp / n : 0):F1} K; " +
                $"delta-v used (measured) {pilot.Record?.DeltaVUsed:F0} m/s; delta-v left (reader, at {acceleration / 9.80665:F2} g) {reader:F0} m/s; reserve expected at arrival {pilot.ReserveAtArrival:F0} m/s ({pilot.ReserveShare * 100:F2}%)");
        }
    }
}
