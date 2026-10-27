using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System;
using System.Diagnostics;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Flies the guidance in the simulator from a 100 km Kerbin orbit and checks the pass bar:
    /// ends in a closed orbit around the target with periapsis within 10 % of the chosen altitude and eccentricity under
    /// 0.05; thrust acceleration within 2 % of the chosen value outside the flip; never inside the Sun safety distance;
    /// never below the target's surface.
    /// </summary>
    [Trait("Category", "Simulation")]
    public class GuidanceSimulationTests
    {
        // When Duna is most nearly opposite Kerbin in the first three Kerbin years (179.97 degrees): the Sun is in the way.
        public const double DunaBehindSunUT = 17_226_000;

        /// <summary>
        /// The Bebop Refueller in Kris's game: 4,746.1 kN, Isp 281,250 s with KSPIE's throttle rule, 152.64 t to 141.42 t
        /// (logged in flight, docs/test-evidence/test-run.log of the planner cycle): 116.05 km/s while holding 1 g.
        /// </summary>
        public static readonly ShipPerformance Bebop = new ShipPerformance(4_746_093.26171875, 281_249.96875, 0.5, 152_642.51278340816, 141_422.38260853774);
        public static double BebopDeltaV => Bebop.DeltaVAtAcceleration(TripCalculator.OneG);
        public static double BebopBudget => 0.9 * BebopDeltaV;   // 10 % reserve (ADR 0002 addendum)

        private readonly ITestOutputHelper output;

        public GuidanceSimulationTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public static FlightResult FlyCase(string name)
        {
            switch (name)
            {
                case "Duna 1g cut":       return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.CutFlipRelight, 0, 60_000);
                case "Duna 1g skew":      return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.SkewFlip, 0, 60_000);
                case "Jool 1g cut":       return Flights.Fly(name, KerbolSystem.Jool, 1.0, FlipManeuver.CutFlipRelight, 0, 220_000);
                case "Jool 1g skew":      return Flights.Fly(name, KerbolSystem.Jool, 1.0, FlipManeuver.SkewFlip, 0, 220_000);
                case "Duna Sun in way":   return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.CutFlipRelight, DunaBehindSunUT, 60_000);
                case "Duna 0.25g cut":    return Flights.Fly(name, KerbolSystem.Duna, 0.25, FlipManeuver.CutFlipRelight, 0, 60_000);
                case "Duna 1g every step": return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.CutFlipRelight, 0, 60_000, cruiseWarp: 1);
                case "Duna coast Bebop":  return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.CutFlipRelight, 0, 60_000,
                                              trip: TripType.CoastInTheMiddle, deltaVBudget: BebopBudget);
                case "Jool coast Bebop":  return Flights.Fly(name, KerbolSystem.Jool, 1.0, FlipManeuver.CutFlipRelight, 0, 220_000,
                                              trip: TripType.CoastInTheMiddle, deltaVBudget: BebopBudget);
                default: throw new ArgumentException(name);
            }
        }

        [Theory]
        [InlineData("Duna 1g cut")]
        [InlineData("Duna 1g skew")]
        [InlineData("Jool 1g cut")]
        [InlineData("Jool 1g skew")]
        [InlineData("Duna Sun in way")]
        [InlineData("Duna 0.25g cut")]
        [InlineData("Duna coast Bebop")]
        [InlineData("Jool coast Bebop")]
        public void FliesToAParkingOrbit(string name)
        {
            FlightResult r = Flown(name);
            Report(r);
            CheckBar(r);
        }

        /// <summary>
        /// The same Duna trip with 0.02 s steps all the way (no physics warp in the cruise). About eight minutes, so
        /// it is left out of the normal run: <c>dotnet test --filter Category=LongRunning</c>.
        /// </summary>
        [Fact]
        [Trait("Category", "LongRunning")]
        public void FliesToAParkingOrbit_EveryPhysicsStep()
        {
            FlightResult r = FlyCase("Duna 1g every step");
            Report(r);
            CheckBar(r);
        }

        // Every case in the theory, flown at once on all cores the first time any of them is asked for.
        private static readonly string[] AllCases =
            { "Duna 1g cut", "Duna 1g skew", "Jool 1g cut", "Jool 1g skew", "Duna Sun in way", "Duna 0.25g cut", "Duna coast Bebop", "Jool coast Bebop" };

        private static readonly Lazy<Dictionary<string, FlightResult>> FlownCases = new Lazy<Dictionary<string, FlightResult>>(() =>
        {
            var results = new ConcurrentDictionary<string, FlightResult>();
            Parallel.ForEach(AllCases, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                name => results[name] = FlyCase(name));
            return new Dictionary<string, FlightResult>(results);
        });

        internal static FlightResult Flown(string name) => FlownCases.Value[name];

        private static void CheckBar(FlightResult r)
        {
            Assert.True(r.Finished, "the guidance never reported Done");
            Assert.True(r.FinalEccentricity < 0.05, $"eccentricity {r.FinalEccentricity:F4}");
            Assert.True(Math.Abs(r.FinalPeriapsisAltitude - r.ParkingAltitude) <= 0.1 * r.ParkingAltitude,
                $"periapsis {r.FinalPeriapsisAltitude / 1000:F2} km vs {r.ParkingAltitude / 1000:F0} km");
            Assert.True(r.MaxAccelErrorOutsideFlip <= 0.02, $"acceleration off by {r.MaxAccelErrorOutsideFlip * 100:F2} %");
            Assert.True(r.ClosestToSun >= TripCalculator.DefaultSunSafetyDistance, $"came within {r.ClosestToSun / 1e9:F3} Gm of the Sun");
            Assert.True(r.MinAltitudeAtTarget > 0, $"went {-r.MinAltitudeAtTarget / 1000:F1} km below the surface");
            if (r.Coast)
            {
                // At least the 10 % reserve left at arrival.
                Assert.True(r.DeltaVUsed <= BebopBudget,
                    $"used {r.DeltaVUsed / 1000:F2} km/s, leaving {(1 - r.DeltaVUsed / BebopDeltaV) * 100:F1} % of {BebopDeltaV / 1000:F2} km/s (needs 10 %)");
                Assert.True(Math.Abs(r.TripTime / r.PlannedTripTime - 1) <= 0.01, $"arrived {r.TripTime:F0} s vs planned {r.PlannedTripTime:F0} s");
            }
        }

        [Fact]
        public void SunDetourEstimate_MatchesTheDetourFlown()
        {
            FlightResult r = Flown("Duna Sun in way");
            Report(r);
            // The planner's estimate (r.Planned*) now includes the detour; the guidance flies that detour.
            output.WriteLine($"estimate: {r.PlannedTripTime:F0} s, {r.PlannedDeltaV / 1000:F1} km/s; flown: {r.TripTime:F0} s, {r.DeltaVUsed / 1000:F1} km/s");
            Assert.Equal(2, r.Legs);
            Assert.True(Math.Abs(r.TripTime / r.PlannedTripTime - 1) < 0.02, "trip time within 2 %");
            Assert.True(Math.Abs(r.DeltaVUsed / r.PlannedDeltaV - 1) < 0.02, "delta-v within 2 %");
        }

        private void Report(FlightResult r)
        {
            double pePct = (r.FinalPeriapsisAltitude - r.ParkingAltitude) / r.ParkingAltitude * 100;
            output.WriteLine($"{r.Name}: finished={r.Finished} legs={r.Legs} steps={r.Steps:N0} wall={r.WallSeconds:F1}s");
            if (r.Coast)
                output.WriteLine($"COAST | {r.Name} | planned burn {r.PlannedBurnUp:F0} s, coast {r.PlannedCoast:F0} s, burn {r.PlannedBurnDown:F0} s, trip {r.PlannedTripTime:F0} s | " +
                                 $"budget {r.CoastBudget / 1000:F2} km/s ({r.CoastBudget / BebopDeltaV * 100:F1} % of {BebopDeltaV / 1000:F2}); used {r.DeltaVUsed / 1000:F2} km/s ({r.DeltaVUsed / BebopDeltaV * 100:F1} %), left {(1 - r.DeltaVUsed / BebopDeltaV) * 100:F1} %; planner said {r.PlannedDeltaVNeeded / 1000:F2} km/s needed");
            output.WriteLine($"ROW | {r.Name} | Pe {r.FinalPeriapsisAltitude / 1000:F2} km / Ap {r.FinalApoapsisAltitude / 1000:F2} km (target {r.ParkingAltitude / 1000:F0}; Pe {pePct:+0.0;-0.0} %) | " +
                             $"e {r.FinalEccentricity:F4} | accel err {r.MaxAccelErrorOutsideFlip * 100:F2} % | " +
                             $"dv {r.DeltaVUsed / 1000:F1} vs {r.PlannedDeltaV / 1000:F1} km/s ({(r.DeltaVUsed / r.PlannedDeltaV - 1) * 100:+0.0;-0.0} %) | " +
                             $"time {r.TripTime:F0} vs {r.PlannedTripTime:F0} s ({(r.TripTime / r.PlannedTripTime - 1) * 100:+0.0;-0.0} %) | " +
                             $"Sun {r.ClosestToSun / 1e9:F3} Gm | lowest {r.MinAltitudeAtTarget / 1000:F1} km | planner flagged Sun {r.PlannerFlaggedSun}");
            output.WriteLine("  solvers: " + r.SolverStats);
            foreach (string line in r.PhaseLog) output.WriteLine("    " + line);
            foreach (string line in r.Trace) output.WriteLine(line);
        }
    }
}
