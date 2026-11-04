using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 14 (ADR 0005): trips among Kerbin's moons, flown back to back on one ship in one launch. Each is checked as a
    // trip to Duna is (TestHarness.Trips): the orbit within 5 % of the parking altitude, the engine off, MechJeb's controls,
    // warp rates and settings back, out of warp before arrival, the arrival message; with the planner (showing the moon
    // list), the flip and the arrival in map view photographed. Only when asked: "moontrips".
    public partial class TestHarness
    {
        private static readonly TripSpec[] MoonTrips =
        {
            new TripSpec
            {
                Name = "M1", Prefix = "moon-1-mun", Target = "Mun",
                What = "Kerbin orbit to the Mun, full burn at 1 g, cut, flip, relight; infinite propellant and ignore max temperature",
                AccelG = 1.0, Trip = TripType.FullBurn, FlipStyle = 0, InfinitePropellant = true, IgnoreMaxTemperature = true, MaxPhysicsRate = 10,
            },
            new TripSpec
            {
                Name = "M2", Prefix = "moon-2-kerbin", Target = "Kerbin",
                What = "the Mun back to Kerbin orbit, full burn at 1 g with a skew flip; infinite propellant and ignore max temperature",
                AccelG = 1.0, Trip = TripType.FullBurn, FlipStyle = 1, InfinitePropellant = true, IgnoreMaxTemperature = true, MaxPhysicsRate = 10,
            },
            new TripSpec
            {
                Name = "M3", Prefix = "moon-3-minmus", Target = "Minmus",
                What = "Kerbin orbit to Minmus, coast trip at 1 g; fuel counted (lowered to 25 km/s of delta-v so the trip has to coast), " +
                       "ignore max temperature (the drive can't hold 1 g cool)",
                AccelG = 1.0, Trip = TripType.CoastInTheMiddle, FlipStyle = 0, IgnoreMaxTemperature = true, MaxPhysicsRate = 10,
            },
        };

        private IEnumerator ProofMoonTrips()
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            Flown cur = f;
            // FLIPANDBURN_MOON_TRIPS (e.g. "M3") flies only some of them.
            string only = Environment.GetEnvironmentVariable("FLIPANDBURN_MOON_TRIPS");
            foreach (TripSpec spec in MoonTrips.Where(s => string.IsNullOrEmpty(only) || only.Split(',').Contains(s.Name)))
            {
                if (spec.Trip == TripType.CoastInTheMiddle)
                {
                    // The Bebop carries far more delta-v than a moon needs, and with that much the fastest coast trip is a full
                    // burn. Lower its propellant (in memory, test copy only) so a coast is what fits.
                    yield return LowerDeltaV(cur, CoastDeltaV, spec.AccelG * 9.81);
                    if (failed) yield break;
                }

                // The "Fly to" list as the planner has it from where the ship is now.
                cur.Module.Planner.Refresh(cur.Ship);
                Log($"moon trips: orbiting {cur.Ship.mainBody.bodyName}; Fly to: " + string.Join("; ",
                    cur.Module.Planner.TargetGroups.Select(g => g.Heading + ": " + string.Join(", ", g.Bodies.Select(b => b.bodyName)))) +
                    (cur.Module.Planner.TargetListNote != null ? " | note: " + cur.Module.Planner.TargetListNote : ""));
                var ok = new CaptureResult();
                yield return RunTrip(cur, spec, ok, x => cur = x);
                if (failed || !ok.Ok) yield break;
                yield return new WaitForSecondsRealtime(5f);
            }

            Log(string.IsNullOrEmpty(only) ? "moon trips: ALL THREE ARRIVED CLEANLY" : $"moon trips: {only} ARRIVED CLEANLY");
        }

        /// <summary>The coast trip's delta-v (m/s): about half a full burn's to Minmus at 1 g (44 km/s), so it has to coast.</summary>
        private const double CoastDeltaV = 25_000;

        // Scale the active engines' propellants down until the planner reads about this much delta-v at this acceleration.
        private IEnumerator LowerDeltaV(Flown f, double target, double acceleration)
        {
            Vessel v = f.Ship;
            var names = new HashSet<string>(v.FindPartModulesImplementing<ModuleEngines>().Where(e => e.EngineIgnited)
                .SelectMany(e => e.propellants.Select(p => p.name)));
            var tanks = v.parts.SelectMany(p => p.Resources.Cast<PartResource>()).Where(r => names.Contains(r.resourceName)).ToList();
            var full = tanks.ToDictionary(r => r, r => r.amount);
            double DeltaV(double share)
            {
                foreach (PartResource r in tanks) r.amount = full[r] * share;
                f.Module.Planner.Refresh(v);
                return f.Module.Planner.Ship?.DeltaVAtAcceleration(acceleration) ?? 0;
            }

            double before = DeltaV(1), lo = 0, hi = 1, got = before;
            for (int i = 0; i < 30 && Math.Abs(got - target) > 0.01 * target; i++)
            {
                double mid = 0.5 * (lo + hi);
                got = DeltaV(mid);
                if (got > target) hi = mid; else lo = mid;
            }

            Log($"moon trips: propellant ({string.Join(", ", names)}) lowered for the coast trip: delta-v at {acceleration / 9.81:F1} g " +
                $"{before:F0} -> {got:F0} m/s (in memory, test copy)");
            if (Math.Abs(got - target) > 0.05 * target) { Fail("moon trips", "couldn't set the delta-v for the coast trip"); failed = true; }
            yield return new WaitForSecondsRealtime(1f);
        }
    }
}
