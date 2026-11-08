using System;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>What the planner says for some common trips: trip time and delta-v (a report, Category=PlannerReport).</summary>
    public class PlannerChangeReport
    {
        private readonly ITestOutputHelper output;
        public PlannerChangeReport(ITestOutputHelper output) => this.output = output;

        private void Planet(string name, PlanetRecord target, double g, double parking, TripType trip = TripType.FullBurn, double budget = 0)
        {
            FlightResult r = Flights.Fly(name, target, g, FlipManeuver.CutFlipRelight, 0, parking, trip: trip, deltaVBudget: budget, stopAfterSeconds: 1);
            Row(r);
        }

        private void Moon(string route, double g, TripType trip, double angle)
        {
            foreach (var c in MoonTripSweepTests.AllCasesForReport())
            {
                if (c.Item1.Name != route) continue;
                SimSystem sys = SimSystem.Around(c.Item1.Planet, c.Item1.From, SimSystem.DefaultParking(c.Item1.From == c.Item1.Planet ? c.Item1.Planet.AtCentre() : c.Item1.From), angle);
                PlanetRecord target = sys.TargetRecord(c.Item1.To);
                double parking = SimSystem.DefaultParking(target);
                double budget = 0;
                if (trip == TripType.CoastInTheMiddle)
                    budget = 0.6 * Flights.Fly("probe", target, g, FlipManeuver.CutFlipRelight, 0, parking, system: sys, stopAfterSeconds: 1).PlannedDeltaV;
                Row(Flights.Fly($"{route}, {(trip == TripType.FullBurn ? "full burn" : "coast")}, {g:0.#} g", target, g, FlipManeuver.CutFlipRelight, 0, parking,
                    system: sys, trip: trip, deltaVBudget: budget, stopAfterSeconds: 1));
                return;
            }
        }

        private void Row(FlightResult r) =>
            output.WriteLine($"PLAN | {r.Name} | trip {r.PlannedTripTime:F0} s | delta-v {r.PlannedDeltaV:F0} m/s");

        [Fact]
        [Trait("Category", "LongRunning")]
        [Trait("Category", "PlannerReport")]
        public void CommonTrips()
        {
            Planet("Kerbin to Duna, full burn, 1 g", KerbolSystem.Duna, 1.0, 60_000);
            Planet("Kerbin to Duna, full burn, 0.25 g", KerbolSystem.Duna, 0.25, 60_000);
            Planet("Kerbin to Duna, coast (Bebop), 1 g", KerbolSystem.Duna, 1.0, 60_000, TripType.CoastInTheMiddle, GuidanceSimulationTests.BebopBudget);
            Planet("Kerbin to Jool, full burn, 1 g", KerbolSystem.Jool, 1.0, 220_000);
            Planet("Kerbin to Jool, full burn, 2 g", KerbolSystem.Jool, 2.0, 220_000);
            Planet("Kerbin to Jool, coast (Bebop), 1 g", KerbolSystem.Jool, 1.0, 220_000, TripType.CoastInTheMiddle, GuidanceSimulationTests.BebopBudget);
            Moon("Kerbin to the Mun", 1.0, TripType.FullBurn, 1.0);
            Moon("Kerbin to the Mun", 1.0, TripType.CoastInTheMiddle, 1.0);
            Moon("Kerbin to Minmus", 1.0, TripType.FullBurn, 1.0);
            Moon("the Mun to Kerbin", 1.0, TripType.FullBurn, 1.0);
            Moon("the Mun to Kerbin", 2.0, TripType.CoastInTheMiddle, 1.0);
            Moon("Jool to Laythe", 1.0, TripType.FullBurn, 1.0);
            Moon("Jool to Tylo", 1.0, TripType.FullBurn, 1.0);
            Moon("Jool to Tylo", 1.0, TripType.CoastInTheMiddle, 1.0);
        }
    }
}
