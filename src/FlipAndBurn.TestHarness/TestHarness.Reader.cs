using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 11: the delta-v reader fix, checked independently (tools/verify/check_reader.py works the same figures out
    // from the save and the part configs). For the Bebop and a stock ship: what the planner shows at 0.4, 1 and 2 g with
    // the engines idle; for the Bebop also while its drive fires at part throttle, the new reading beside the old one (the
    // engine's live thrust and Isp curve, which KSP Interstellar rewrites while the drive fires).
    public partial class TestHarness
    {
        private static readonly double[] ReaderCheckG = { 0.4, 1.0, 2.0 };

        private IEnumerator ProofReaderCheck(Flown f, CaptureResult outcome, bool fire)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            Vessel v = f.Ship;
            CheatOptions.InfinitePropellant = false;
            module.SelectTripType(TripType.FullBurn);
            yield return Diagnostics(v, f.Core, module);
            LogReader(v, module, "idle");
            if (!fire) { outcome.Ok = true; yield break; }

            // Fire the drive at the Bebop's 0.4 g throttle, the harness holding the throttle (no trip running).
            const float throttle = 0.126f;
            FlightInputHandler.state.mainThrottle = throttle;
            v.ctrlState.mainThrottle = throttle;
            float until = Time.realtimeSinceStartup + 4f;
            while (Time.realtimeSinceStartup < until)
            {
                FlightInputHandler.state.mainThrottle = throttle;
                yield return new WaitForFixedUpdate();
            }

            double thrust = v.FindPartModulesImplementing<ModuleEngines>().Sum(e => (double)e.finalThrust);
            Log($"reader check: drive firing at throttle {throttle}: thrust {thrust:F1} kN");
            LogReader(v, module, $"firing at throttle {throttle}");
            FlightInputHandler.state.mainThrottle = 0;
            v.ctrlState.mainThrottle = 0;
            yield return new WaitForSecondsRealtime(2f);
            outcome.Ok = thrust > 0;
            if (!outcome.Ok) { Fail("readercheck", "the drive didn't fire"); failed = true; }
        }

        // The planner's rows at each acceleration, the reader's figures, and the old reader (the live curve) beside them.
        private static void LogReader(Vessel v, MechJebModuleFlipAndBurn module, string state)
        {
            double fuelUnits = 0;
            foreach (Part p in v.parts)
            foreach (PartResource r in p.Resources)
                if (r.resourceName == "LqdDeuterium") fuelUnits += r.amount;
            Log($"reader check [{v.vesselName}, {state}]: vessel.totalMass {v.totalMass:R} t; LqdDeuterium {fuelUnits:R} units");

            module.Planner.Refresh(v);
            ShipPerformance now = module.Planner.Ship;
            string problem = string.Join(" ", module.Planner.Notes.Where(n => n.Contains("engine")));
            // The old reader: thrust and Isp straight from the engine module (maxThrust, the Isp curve at vacuum).
            List<ModuleEngines> running = v.FindPartModulesImplementing<ModuleEngines>().Where(e => e.EngineIgnited && e.isEnabled && !e.flameout).ToList();
            ShipPerformance old = null;
            if (running.Count == 1 && now != null)
            {
                ModuleEngines e = running[0];
                double rawThrust = e.maxThrust * (e.thrustPercentage / 100.0) * 1000, rawIsp = e.atmosphereCurve.Evaluate(0f);
                old = new ShipPerformance(rawThrust, rawIsp, now.IspThrottleExponent, now.StartMass, now.EndMass);
                Log($"reader check [{v.vesselName}, {state}]: engine module: maxThrust {e.maxThrust:R} kN, Isp curve at vacuum {rawIsp:R} s, " +
                    $"current throttle {e.currentThrottle:F3}");
            }

            Log($"reader check [{v.vesselName}, {state}]: reader (new): thrust {now?.FullThrust:R} N, vacuum Isp {now?.VacuumIsp:R} s, " +
                $"throttle exponent {now?.IspThrottleExponent:R}, start {now?.StartMass:R} kg, end {now?.EndMass:R} kg; notes on engines: {(problem.Length > 0 ? problem : "none")}");
            foreach (double g in ReaderCheckG)
            {
                module.AccelerationG.Val = g;
                module.Planner.Refresh(v);
                string rows = string.Join("; ", module.Planner.ShipRows.Select(r => $"{r.Key.Trim()} = {r.Value}"));
                double a = g * TripCalculator.OneG;
                Log($"reader check [{v.vesselName}, {state}] {g:0.0} g: new {now?.DeltaVAtAcceleration(a):F1} m/s, old {old?.DeltaVAtAcceleration(a):F1} m/s; " +
                    $"planner shows: {rows}");
            }
        }
    }
}
