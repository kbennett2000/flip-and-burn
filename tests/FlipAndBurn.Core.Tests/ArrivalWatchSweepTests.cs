using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// The arrival watch (ADR 0003, "Arriving") must never fire on a normal trip. Normal trips to Duna, Dres and Eve: full
    /// burns with both flip styles and coast trips, at accelerations from 0.25 g (below the Bebop's sustainable 0.45 g) to
    /// 3 g, all with the watch at its most sensitive (engines that keep up exactly twice the chosen acceleration). Several
    /// minutes, so left out of the normal run: <c>dotnet test --filter Category=LongRunning</c>. The normal run's eight
    /// simulated trips (GuidanceSimulationTests) check the same thing.
    /// </summary>
    [Trait("Category", "LongRunning")]
    public class ArrivalWatchSweepTests
    {
        private readonly ITestOutputHelper output;
        public ArrivalWatchSweepTests(ITestOutputHelper output) { this.output = output; }

        private sealed class Case
        {
            public PlanetRecord Target;
            public double AccelG, Parking;
            public TripType Trip;
            public FlipManeuver Flip;
            public string Name => $"{Target.Name} {AccelG:0.##} g {(Trip == TripType.CoastInTheMiddle ? "coast" : Flip == FlipManeuver.SkewFlip ? "skew" : "cut")}";
        }

        private static IEnumerable<Case> Cases()
        {
            var targets = new[] { (KerbolSystem.Duna, 60_000.0, new[] { 1.0, 0.45, 0.25 }), (KerbolSystem.Dres, 20_000.0, new[] { 1.0, 0.45, 0.25 }),
                                  (KerbolSystem.Eve, 100_000.0, new[] { 3.0, 2.0 }) };
            foreach ((PlanetRecord target, double parking, double[] accels) in targets)
            foreach (double g in accels)
            {
                yield return new Case { Target = target, AccelG = g, Parking = parking, Trip = TripType.FullBurn, Flip = FlipManeuver.CutFlipRelight };
                yield return new Case { Target = target, AccelG = g, Parking = parking, Trip = TripType.FullBurn, Flip = FlipManeuver.SkewFlip };
                if (g >= 0.45 && !(target == KerbolSystem.Eve && g < 3))
                    yield return new Case { Target = target, AccelG = g, Parking = parking, Trip = TripType.CoastInTheMiddle, Flip = FlipManeuver.CutFlipRelight };
            }
        }

        // A coast trip needs a budget: 60 % of what the full burn needs, so there is a real coast.
        private static double CoastBudget(Case c)
        {
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            var sim = new ShipSimulator(kerbin, c.Target, 0.1);
            sim.StartInOrbit(kerbin, 100_000, 0);
            TargetPlanet target = c.Target.AsTarget();
            target.SphereOfInfluence = c.Target.SphereOfInfluence;
            TargetPlanet departure = kerbin.AsTarget();
            departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            TripPlan plan = TripCalculator.Plan(new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = 0, Target = target, Departure = departure,
                Acceleration = c.AccelG * TripCalculator.OneG, ParkingAltitude = c.Parking, SunGravParameter = KerbolSystem.SunGravParameter,
            });
            return 0.6 * plan.TotalDeltaV;
        }

        [Fact]
        public void NeverFiresOnANormalTrip()
        {
            List<Case> cases = Cases().ToList();
            var results = new ConcurrentDictionary<string, FlightResult>();
            Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, c =>
                results[c.Name] = Flights.Fly(c.Name, c.Target, c.AccelG, c.Flip, 0, c.Parking, trip: c.Trip,
                    deltaVBudget: c.Trip == TripType.CoastInTheMiddle ? CoastBudget(c) : 0,
                    wallSeconds: 600));   // many trips share the cores here

            int falseAlarms = 0, arrived = 0;
            foreach (Case c in cases)
            {
                FlightResult r = results[c.Name];
                bool parked = r.Finished && !r.EndedInSafeOrbit && Math.Abs(r.FinalPeriapsisAltitude - c.Parking) <= 0.1 * c.Parking && r.FinalEccentricity < 0.05;
                if (r.SafeOrbitReason != null) falseAlarms++;
                if (parked) arrived++;
                output.WriteLine($"{c.Name,-18} {(parked ? "parked" : "NOT PARKED"),-11} periapsis {r.FinalPeriapsisAltitude / 1000,7:F1} km, e {r.FinalEccentricity:F4}; " +
                                 $"closest to the watch's line {r.WatchPeakShare / ArrivalSafety.RescueMargin * 100,5:F1}% of it; watch {r.SafeOrbitReason ?? "quiet"}; " +
                                 $"{r.WallSeconds:F0} s{(r.TimedOut ? " TIMED OUT" : "")}");
            }

            output.WriteLine($"{cases.Count} simulated trips, {arrived} parked, {falseAlarms} false alarms");
            Assert.Equal(0, falseAlarms);
            Assert.Equal(cases.Count, arrived);
        }
    }
}
