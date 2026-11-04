using System.Collections;
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
                What = "Kerbin orbit to Minmus, coast trip at 1 g; fuel counted, ignore max temperature (the drive can't hold 1 g cool)",
                AccelG = 1.0, Trip = TripType.CoastInTheMiddle, FlipStyle = 0, IgnoreMaxTemperature = true, MaxPhysicsRate = 10,
            },
        };

        private IEnumerator ProofMoonTrips()
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            Flown cur = f;
            foreach (TripSpec spec in MoonTrips)
            {
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

            Log("moon trips: ALL THREE ARRIVED CLEANLY");
        }
    }
}
