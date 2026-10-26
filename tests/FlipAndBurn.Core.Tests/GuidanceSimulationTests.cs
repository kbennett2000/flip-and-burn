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
                case "Duna 1g warp x100": return Flights.Fly(name, KerbolSystem.Duna, 1.0, FlipManeuver.CutFlipRelight, 0, 60_000, cruiseWarp: 100);
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
        [InlineData("Duna 1g warp x100")]
        public void FliesToAParkingOrbit(string name)
        {
            var sw = Stopwatch.StartNew();
            FlightResult r = FlyCase(name);
            Report(r, sw);

            Assert.True(r.Finished, "the guidance never reported Done");
            Assert.True(r.FinalEccentricity < 0.05, $"eccentricity {r.FinalEccentricity:F4}");
            Assert.True(Math.Abs(r.FinalPeriapsisAltitude - r.ParkingAltitude) <= 0.1 * r.ParkingAltitude,
                $"periapsis {r.FinalPeriapsisAltitude / 1000:F2} km vs {r.ParkingAltitude / 1000:F0} km");
            Assert.True(r.MaxAccelErrorOutsideFlip <= 0.02, $"acceleration off by {r.MaxAccelErrorOutsideFlip * 100:F2} %");
            Assert.True(r.ClosestToSun >= TripCalculator.DefaultSunSafetyDistance, $"came within {r.ClosestToSun / 1e9:F3} Gm of the Sun");
            Assert.True(r.MinAltitudeAtTarget > 0, $"went {-r.MinAltitudeAtTarget / 1000:F1} km below the surface");
        }

        private void Report(FlightResult r, Stopwatch sw)
        {
            double pePct = (r.FinalPeriapsisAltitude - r.ParkingAltitude) / r.ParkingAltitude * 100;
            output.WriteLine($"{r.Name}: finished={r.Finished} legs={r.Legs} steps={r.Steps:N0} wall={sw.Elapsed.TotalSeconds:F1}s");
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
