using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// How fast every watched trip reaches the last burn: speed along the orbit above circular, climb rate and height when
    /// the circularizing burn begins, and how long that burn takes. A report (Category=LastBurn), not a pass/fail check.
    /// </summary>
    public class LastBurnReport
    {
        private readonly ITestOutputHelper output;
        public LastBurnReport(ITestOutputHelper output) => this.output = output;

        private void Row(FlightResult f) =>
            output.WriteLine($"{f.Name} | last burn from {f.LastBurnAltitude / 1000:F1} km (target {f.ParkingAltitude / 1000:F0}), excess {f.LastBurnExcess:F0} m/s, " +
                             $"climb {f.LastBurnClimb:F0} m/s, lasted {f.LastBurnSeconds:F0} s | orbit {f.FinalPeriapsisAltitude / 1000:F1} x {f.FinalApoapsisAltitude / 1000:F1} km");

        [Fact]
        [Trait("Category", "LongRunning")]
        [Trait("Category", "LastBurn")]
        public void EveryWatchedTrip()
        {
            foreach (string name in new[] { "Duna 1g cut", "Duna 1g skew", "Jool 1g cut", "Jool 1g skew", "Duna Sun in way", "Duna 0.25g cut", "Duna coast Bebop", "Jool coast Bebop" })
                foreach (double t in new[] { 5.0, 10.0 })
                {
                    FlightResult f = GuidanceSimulationTests.FlyCase(name, t);
                    f.Name = $"{name}, turning {t:0} deg/s";
                    Row(f);
                }

            foreach (var c in MoonTripSweepTests.AllCasesForReport())
                foreach (double t in new[] { 5.0, 10.0 })
                    Row(MoonTripSweepTests.FlyRoute(c.Item1, c.Item2, c.Item3, c.Item4, c.Item5, t));
        }

        [Fact]
        [Trait("Category", "LongRunning")]
        [Trait("Category", "LastBurnTrace")]
        public void TyloCoastTrace()
        {
            MoonTripSweepTests.Route tylo = MoonTripSweepTests.AllCasesForReport().First(c => c.Item1.Name == "Jool to Tylo").Item1;
            SimSystem sys = SimSystem.Around(tylo.Planet, tylo.From, SimSystem.DefaultParking(tylo.Planet.AtCentre()), 1.0);
            PlanetRecord target = sys.TargetRecord(tylo.To);
            double parking = SimSystem.DefaultParking(target);
            FlightResult probe = Flights.Fly("probe", target, 1.0, FlipManeuver.CutFlipRelight, 0, parking, system: sys, stopAfterSeconds: 1, turnRateDegPerSec: 5);
            FlightResult f = Flights.Fly("tylo coast trace", target, 1.0, FlipManeuver.CutFlipRelight, 0, parking, system: sys, trip: TripType.CoastInTheMiddle,
                deltaVBudget: 0.6 * probe.PlannedDeltaV, wallSeconds: 150, maxTimeFactor: 3, turnRateDegPerSec: 5, traceFrom: 0, traceEvery: 20);
            foreach (string l in f.PhaseLog) output.WriteLine("phase " + l);
            foreach (string l in f.Trace) output.WriteLine(l);
            Row(f);
        }
    }
}
