using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Trips among a planet's moons, flown in the simulator (ADR 0005): every trip must arrive in its parking orbit (Pe and
    /// Ap within 5 % of the altitude), keep clear of the planet and every moon, and never set off the arrival safety net.
    /// The full sweep is long (Category=MoonSweep, run on demand: dotnet test --filter Category=MoonSweep); a handful run
    /// with the everyday suite.
    /// </summary>
    public class MoonTripSweepTests
    {
        private readonly ITestOutputHelper output;

        public MoonTripSweepTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public sealed class Route
        {
            public string Name;
            public PlanetRecord Planet, From, To;
            public double[] Accelerations;   // g
        }

        private static readonly Route[] Routes =
        {
            new Route { Name = "Kerbin to the Mun", Planet = KerbolSystem.Kerbin, From = KerbolSystem.Kerbin, To = KerbolSystem.Mun, Accelerations = new[] { 1.0, 2.0 } },
            new Route { Name = "Kerbin to Minmus", Planet = KerbolSystem.Kerbin, From = KerbolSystem.Kerbin, To = KerbolSystem.Minmus, Accelerations = new[] { 1.0, 2.0 } },
            new Route { Name = "the Mun to Minmus", Planet = KerbolSystem.Kerbin, From = KerbolSystem.Mun, To = KerbolSystem.Minmus, Accelerations = new[] { 0.5, 1.0, 2.0 } },
            new Route { Name = "the Mun to Kerbin", Planet = KerbolSystem.Kerbin, From = KerbolSystem.Mun, To = KerbolSystem.Kerbin, Accelerations = new[] { 1.0, 2.0 } },
            new Route { Name = "Duna to Ike", Planet = KerbolSystem.Duna, From = KerbolSystem.Duna, To = KerbolSystem.Ike, Accelerations = new[] { 0.5, 1.0, 2.0 } },
            new Route { Name = "Jool to Laythe", Planet = KerbolSystem.Jool, From = KerbolSystem.Jool, To = KerbolSystem.Laythe, Accelerations = new[] { 1.0, 2.0 } },
            new Route { Name = "Jool to Tylo", Planet = KerbolSystem.Jool, From = KerbolSystem.Jool, To = KerbolSystem.Tylo, Accelerations = new[] { 1.0, 2.0 } },
        };

        /// <summary>One trip. Coast trips get a budget of 40 % of what a full burn at that acceleration takes.</summary>
        public static FlightResult FlyRoute(Route r, double g, TripType trip, FlipManeuver flip, double angle)
        {
            SimSystem sys = SimSystem.Around(r.Planet, r.From, SimSystem.DefaultParking(r.From == r.Planet ? r.Planet.AtCentre() : r.From), angle);
            PlanetRecord target = sys.TargetRecord(r.To);
            double parking = SimSystem.DefaultParking(target);
            double budget = 0;
            if (trip == TripType.CoastInTheMiddle)
            {
                FlightResult probe = Flights.Fly("probe", target, g, flip, 0, parking, system: sys, stopAfterSeconds: 1);
                budget = 0.4 * probe.PlannedDeltaV;
            }

            string name = $"{r.Name}, {(trip == TripType.FullBurn ? "full burn" : "coast")}, {g:0.0} g, {(flip == FlipManeuver.SkewFlip ? "skew" : "cut-flip-relight")}, start {angle * 180 / Math.PI:0} deg";
            return Flights.Fly(name, target, g, flip, 0, parking, system: sys, trip: trip, deltaVBudget: budget, wallSeconds: 150, maxTimeFactor: 3);
        }

        /// <summary>Why a trip didn't meet the standard, or null.</summary>
        public static string Problem(FlightResult f)
        {
            if (f.TimedOut) return "timed out";
            if (!f.Finished) return "didn't finish";
            if (f.SafeOrbitReason != null || f.EndedInSafeOrbit) return "the safety net fired: " + f.SafeOrbitReason;
            double tol = 0.05 * f.ParkingAltitude;
            if (Math.Abs(f.FinalPeriapsisAltitude - f.ParkingAltitude) > tol || Math.Abs(f.FinalApoapsisAltitude - f.ParkingAltitude) > tol)
                return $"orbit {f.FinalPeriapsisAltitude / 1000:F1} x {f.FinalApoapsisAltitude / 1000:F1} km, not within 5 % of {f.ParkingAltitude / 1000:F0} km";
            if (f.ClosestToObstacle < 0) return $"went {-f.ClosestToObstacle / 1000:F1} km below another body's safe height";
            if (f.MinAltitudeAtTarget < 0) return "hit the target";
            return null;
        }

        private string Row(FlightResult f)
        {
            string p = Problem(f);
            return $"{(p == null ? "OK  " : "FAIL")} | {f.Name} | orbit {f.FinalPeriapsisAltitude / 1000:F1} x {f.FinalApoapsisAltitude / 1000:F1} km (target {f.ParkingAltitude / 1000:F0}) | " +
                   $"time {f.TripTime:F0} s, planned {f.PlannedTripTime:F0} ({(f.TripTime / f.PlannedTripTime - 1) * 100:+0.0;-0.0} %) | " +
                   $"delta-v {f.DeltaVUsed / 1000:F2} km/s, planned {f.PlannedDeltaV / 1000:F2} ({(f.DeltaVUsed / f.PlannedDeltaV - 1) * 100:+0.0;-0.0} %)" +
                   (f.Coast ? $", budget {f.CoastBudget / 1000:F2}" : "") +
                   $" | clear by {f.ClosestToObstacle / 1000:F1} km | watch peak {f.WatchPeakShare:F2}{(p == null ? "" : " | " + p)}";
        }

        private void Run(IEnumerable<(Route Route, double G, TripType Trip, FlipManeuver Flip, double Angle)> cases)
        {
            var rows = new List<FlightResult>();
            foreach (var c in cases)
            {
                FlightResult f = FlyRoute(c.Route, c.G, c.Trip, c.Flip, c.Angle);
                rows.Add(f);
                output.WriteLine(Row(f));
            }

            int ok = rows.Count(f => Problem(f) == null), alarms = rows.Count(f => f.SafeOrbitReason != null);
            output.WriteLine($"TOTAL: {rows.Count} trips, {ok} arrived as planned, {rows.Count - ok} didn't; safety net fired {alarms} times");
            foreach (FlightResult f in rows.Where(f => Problem(f) != null))
                foreach (string l in f.PhaseLog) output.WriteLine($"   {f.Name}: {l}");
            Assert.True(ok == rows.Count, $"{rows.Count - ok} of {rows.Count} moon trips failed");
        }

        private static IEnumerable<(Route, double, TripType, FlipManeuver, double)> AllCases()
        {
            foreach (Route r in Routes)
            {
                foreach (double g in r.Accelerations)
                    foreach (double angle in new[] { 0.0, 2.2 })
                        yield return (r, g, TripType.FullBurn, FlipManeuver.CutFlipRelight, angle);
                yield return (r, 1.0, TripType.FullBurn, FlipManeuver.SkewFlip, 4.0);
                yield return (r, 1.0, TripType.CoastInTheMiddle, FlipManeuver.CutFlipRelight, 1.0);
            }
        }

        [Fact]
        [Trait("Category", "MoonSweep")]
        public void Sweep_EveryRoute() => Run(AllCases());

        /// <summary>The everyday subset: one of each kind of moon trip.</summary>
        [Fact]
        public void MoonTrips_OneOfEachKind() => Run(new[]
        {
            (Routes[0], 1.0, TripType.FullBurn, FlipManeuver.CutFlipRelight, 2.2),      // planet to moon, waits for the window
            (Routes[3], 1.0, TripType.FullBurn, FlipManeuver.SkewFlip, 0.0),            // moon to planet, skew flip
            (Routes[2], 1.0, TripType.FullBurn, FlipManeuver.CutFlipRelight, 0.0),      // moon to moon
            (Routes[1], 1.0, TripType.CoastInTheMiddle, FlipManeuver.CutFlipRelight, 1.0), // coast trip
        });
    }
}
