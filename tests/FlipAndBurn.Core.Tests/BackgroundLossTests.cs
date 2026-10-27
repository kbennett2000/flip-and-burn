using System;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>Propellant lost while the engines aren't firing (boil-off), measured and allowed for (ADR 0002).</summary>
    public class BackgroundLossTests
    {
        private readonly ITestOutputHelper output;
        public BackgroundLossTests(ITestOutputHelper output) { this.output = output; }

        [Fact]
        public void Rate_FromStretchesWithoutFiring()
        {
            var loss = new BackgroundLoss();
            loss.Sample(0, 11_220, false);
            loss.Sample(1800, 11_219, false);         // 1 kg in half an hour
            Assert.False(loss.Measured);              // not an hour yet
            loss.Sample(3600, 11_218, false);
            Assert.True(loss.Measured);
            Assert.Equal(2.0 / 3600, loss.RateKgPerSecond, 9);
            Assert.Equal(48, loss.KgPerDay(86_400), 6);
        }

        [Fact]
        public void FiringAndRefuelling_NotCounted()
        {
            var loss = new BackgroundLoss();
            loss.Sample(0, 11_220, false);
            loss.Sample(3600, 10_000, true);          // the engine fired: not background loss
            loss.Sample(7200, 10_001, false);         // refuelled: not counted either
            loss.Sample(10_800, 10_000, false);       // 1 kg in an hour
            Assert.Equal(1.0 / 3600, loss.RateKgPerSecond, 9);
            Assert.Equal(3600, loss.ObservedSeconds, 9);
        }

        [Fact]
        public void DeltaVCost_GrowsWithTheLoss()
        {
            ShipPerformance bebop = GuidanceSimulationTests.Bebop;
            double a = 0.4 * TripCalculator.OneG;
            double small = BackgroundLoss.DeltaVCost(bebop, a, 100), big = BackgroundLoss.DeltaVCost(bebop, a, 900);
            double all = BackgroundLoss.DeltaVCost(bebop, a, bebop.StartMass - bebop.EndMass);
            output.WriteLine($"Bebop at 0.4 g: {bebop.DeltaVAtAcceleration(a):F0} m/s; losing 100 kg costs {small:F0} m/s, 900 kg {big:F0} m/s, all of it {all:F0} m/s");
            Assert.Equal(0, BackgroundLoss.DeltaVCost(bebop, a, 0));
            Assert.True(small > 0 && big > small);
            Assert.Equal(bebop.DeltaVAtAcceleration(a), all, 6);
        }

        [Fact]
        public void CoastPlan_AllowsForTheLoss_AndStillKeepsTheReserve()
        {
            // The Bebop coast trip at 1 g from Kerbin orbit (the simulator's tested case), with tanks losing 2 kg an hour.
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            var sim = new ShipSimulator(kerbin, KerbolSystem.Duna, 0.1);
            sim.StartInOrbit(kerbin, 100_000, 0);
            TargetPlanet target = KerbolSystem.Duna.AsTarget();
            target.SphereOfInfluence = KerbolSystem.Duna.SphereOfInfluence;
            TargetPlanet departure = kerbin.AsTarget();
            departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            var request = new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = 0, Target = target, Departure = departure,
                Acceleration = TripCalculator.OneG, ParkingAltitude = 60_000, SunGravParameter = KerbolSystem.SunGravParameter,
            };
            // A Bebop with more deuterium (18 t instead of 11.2 t): from Kerbin at UT 0 the real Bebop's budget is only just
            // enough for any coast trip to Duna, which leaves no room to show the trade.
            ShipPerformance bebop = new ShipPerformance(4_746_093.26171875, 281_249.96875, 0.5, 141_422.38 + 18_000, 141_422.38);
            double start = bebop.DeltaVAtAcceleration(TripCalculator.OneG), budget = 0.9 * start;
            double rate = 2.0 / 3600;

            CoastPlan plain = CoastPlanner.Plan(request, budget);
            CoastPlan allowed = CoastPlanner.PlanAllowingForLoss(request, budget, bebop, rate, out double lossDeltaV);
            double lostKg = rate * allowed.TripTime;
            double reserve = start - allowed.DeltaVNeeded - lossDeltaV;
            output.WriteLine($"without the loss: {plain.TripTime:F0} s, needs {plain.DeltaVNeeded:F0} m/s; allowing for 2 kg/h: {allowed.TripTime:F0} s, " +
                             $"needs {allowed.DeltaVNeeded:F0} m/s, loses {lostKg:F0} kg = {lossDeltaV:F0} m/s; reserve {reserve:F0} m/s = {reserve / start * 100:F2} %");

            Assert.True(allowed.Feasible);
            Assert.True(allowed.TripTime > plain.TripTime, "a slower trip, to make room for the loss");
            Assert.True(allowed.DeltaVNeeded + lossDeltaV <= budget * 1.000001, "the burns and the loss fit the budget");
            Assert.True(reserve >= 0.1 * start * 0.999999, "the 10 % reserve is still there at arrival");

            // No loss: the plain plan.
            CoastPlan none = CoastPlanner.PlanAllowingForLoss(request, budget, bebop, 0, out double zero);
            Assert.Equal(plain.TripTime, none.TripTime, 6);
            Assert.Equal(0, zero);
        }

        [Fact]
        public void TooMuchLoss_NoCoastTrip()
        {
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            var sim = new ShipSimulator(kerbin, KerbolSystem.Duna, 0.1);
            sim.StartInOrbit(kerbin, 100_000, 0);
            TargetPlanet target = KerbolSystem.Duna.AsTarget();
            target.SphereOfInfluence = KerbolSystem.Duna.SphereOfInfluence;
            TargetPlanet departure = kerbin.AsTarget();
            departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            var request = new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = 0, Target = target, Departure = departure,
                Acceleration = TripCalculator.OneG, ParkingAltitude = 60_000, SunGravParameter = KerbolSystem.SunGravParameter,
            };
            // Losing 100 kg an hour, the Bebop's 11.2 t of deuterium can't last a coast to Duna.
            CoastPlan plan = CoastPlanner.PlanAllowingForLoss(request, GuidanceSimulationTests.BebopBudget, GuidanceSimulationTests.Bebop, 100.0 / 3600, out _);
            Assert.False(plan.Feasible);
        }
    }
}
