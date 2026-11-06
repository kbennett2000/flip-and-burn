using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using MuMech;
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

        // The Bebop from its Kerbin orbit to the Mun at 1 g, full burn, with real heat: fuel counted, no cheats, the planner's
        // warp choice at its default (10x, put back afterwards). Not a pass or fail: what happens is recorded (does it arrive,
        // step down, or stop, and where does the ship end up). Only when asked: "moonheat".
        private IEnumerator ProofMoonHeat()
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = double.NaN;   // the player's choice
            int warpBefore = module.PhysicsWarpIndex;
            module.PhysicsWarpIndex = 0;
            if (!module.SelectTarget("Mun")) { Fail("moon heat", "could not select the Mun"); failed = true; yield break; }
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 1.0;
            module.SelectTripType(TripType.FullBurn);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            Planner planner = module.Planner;
            Log($"moon heat: no cheats (infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}); " +
                $"max physics warp choice {PhysicsWarpChoice.Names[module.PhysicsWarpIndex]} (was {PhysicsWarpChoice.Names[PhysicsWarpChoice.Clamp(warpBefore)]}); " +
                $"planner can start {planner.CanStart}");
            foreach (KeyValuePair<string, string> row in planner.TripRows.Concat(planner.ShipRows)) Log($"moon heat: planner row {row.Key}: {row.Value}");
            foreach (string note in planner.Notes) Log("moon heat: planner note: " + note);
            HideMechJebMenu(f);
            Frame(f.Ship, 0.30f, 0.9f, 1.4f);
            yield return Shot("moon-heat-1-planner.png", f);

            string problem = planner.CanStart ? module.PressStart() : "the planner won't start";
            if (problem != null)
            {
                Log("moon heat: Start refused: " + problem + " | " + string.Join(" | ", planner.Notes));
                yield return EndMoonHeat(f, module, warpBefore);
                yield break;
            }

            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            statusEvery = 30f;
            int replans = 0, shots = 0;
            double startUT = Planetarium.GetUniversalTime();
            float t0 = Time.realtimeSinceStartup;
            while (pilot.Running && Time.realtimeSinceStartup - t0 < 45 * 60)
            {
                if (pilot.Replans != replans)
                {
                    replans = pilot.Replans;
                    GuidanceCommand c = pilot.Last;
                    Log($"moon heat: step-down {replans} at {Planetarium.GetUniversalTime() - startUT:F0} s into the trip: now {pilot.CurrentAcceleration / 9.81:F2} g; " +
                        $"phase {c?.Phase}; altitude {f.Ship.altitude / 1000:F0} km above {f.Ship.mainBody.bodyName}; hottest part {HottestFraction(f.Ship) * 100:F0}%; notice: {pilot.Notice}");
                    if (shots++ < 2) yield return Shot($"moon-heat-2-step-down-{replans}.png", f);
                }

                yield return Fly(pilot, () => pilot.Replans != replans, 5, "moon heat");
                yield return new WaitForFixedUpdate();
            }

            Log($"moon heat: trip over after {Planetarium.GetUniversalTime() - startUT:F0} game s: running {pilot.Running}, arrived {pilot.Arrived}, " +
                $"step-downs {pilot.Replans}, acceleration at the end {pilot.CurrentAcceleration / 9.81:F2} g; stop reason: {pilot.StopReason}");
            yield return EndMoonHeat(f, module, warpBefore);
        }

        private IEnumerator EndMoonHeat(Flown f, MechJebModuleFlipAndBurn module, int warpBefore)
        {
            Vessel v = f.Ship;
            Orbit o = v.orbit;
            Log($"moon heat: the ship ends up {v.situation} around {v.mainBody.bodyName}: periapsis {o.PeA / 1000:F1} km, apoapsis {o.ApA / 1000:F1} km, " +
                $"eccentricity {o.eccentricity:F4}; LqdDeuterium {v.parts.Sum(p => p.Resources.Get("LqdDeuterium")?.amount ?? 0):F0} units");
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("moon-heat-3-end.png", f);
            yield return MapShot(f, "moon-heat-4-map.png");
            module.PhysicsWarpIndex = warpBefore;
            Log($"moon heat: max physics warp choice put back to {PhysicsWarpChoice.Names[PhysicsWarpChoice.Clamp(warpBefore)]}");
            step++;
        }

        private static double HottestFraction(Vessel v) => v.parts.Count == 0 ? 0
            : v.parts.Max(p => Math.Max(p.maxTemp > 0 ? p.temperature / p.maxTemp : 0, p.skinMaxTemp > 0 ? p.skinTemperature / p.skinMaxTemp : 0));

        // A watched coast trip from a low Jool orbit to Tylo (cycle 15: the last-burn fix, for the arrival that missed in the
        // simulator). The test copy has no ship near Jool: KSP's own "set orbit" cheat (test copy only) puts the Bebop in a
        // 220 km orbit around Jool, and its propellant is lowered in memory so the trip has to coast. Only when asked: "tylocoast".
        private IEnumerator ProofTyloCoast()
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            CelestialBody jool = FlightGlobals.Bodies.First(b => b.bodyName == "Jool");
            double sma = jool.Radius + 220_000;
            FlightGlobals.fetch.SetShipOrbit(FlightGlobals.Bodies.IndexOf(jool), 0, sma, 0, 0, 0, 0, 0);
            float until = Time.realtimeSinceStartup + 60;
            while (Time.realtimeSinceStartup < until && (f.Ship.mainBody != jool || f.Ship.packed)) yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);
            Orbit o = f.Ship.orbit;
            Log($"tylo coast: the Bebop set into orbit around {f.Ship.mainBody.bodyName} with KSP's set-orbit cheat (test copy): periapsis {o.PeA / 1000:F1} km, " +
                $"apoapsis {o.ApA / 1000:F1} km");
            if (f.Ship.mainBody != jool) { Fail("tylo coast", "the Bebop isn't around Jool"); failed = true; yield break; }
            yield return LowerDeltaV(f, 30_000, 9.81);
            if (failed) yield break;
            var spec = new TripSpec
            {
                Name = "T1", Prefix = "tylo-coast", Target = "Tylo",
                What = "low Jool orbit to Tylo, coast trip at 1 g; fuel counted (lowered to 30 km/s so the trip coasts), ignore max temperature",
                AccelG = 1.0, Trip = TripType.CoastInTheMiddle, FlipStyle = 0, IgnoreMaxTemperature = true, MaxPhysicsRate = 10,
            };
            var ok = new CaptureResult();
            yield return RunTrip(f, spec, ok, null);
            if (!failed && ok.Ok) Log("tylo coast: ARRIVED CLEANLY");
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
