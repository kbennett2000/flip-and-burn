using System.Diagnostics;
using FlipAndBurn.Core;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The coast trip planner: Kerbin to Duna on the Bebop Refueller's numbers.</summary>
    public class CoastPlannerTests
    {
        private readonly ITestOutputHelper output;

        public CoastPlannerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static TripRequest DunaFromLowKerbinOrbit()
        {
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            StateVector k = kerbin.Orbit.StateAt(0);
            double r = kerbin.Radius + 80_000, v = System.Math.Sqrt(kerbin.GravParameter / r);
            TargetPlanet departure = kerbin.AsTarget();
            departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            return new TripRequest
            {
                ShipPosition = k.Position + new Vec3(r, 0, 0),
                ShipVelocity = k.Velocity + new Vec3(0, v, 0),
                StartTime = 0,
                Target = KerbolSystem.Duna.AsTarget(),
                Departure = departure,
                Acceleration = TripCalculator.OneG,
                ParkingAltitude = 60_000,
                SunGravParameter = KerbolSystem.SunGravParameter,
            };
        }

        [Fact]
        public void FitsTheBudgetWithTheGuidanceMargin()
        {
            double budget = GuidanceSimulationTests.BebopBudget;
            var sw = Stopwatch.StartNew();
            CoastPlan plan = CoastPlanner.Plan(DunaFromLowKerbinOrbit(), budget);
            output.WriteLine($"planned in {sw.Elapsed.TotalMilliseconds:F0} ms: burn {plan.BurnUpTime:F0} s, coast {plan.CoastTime:F0} s, " +
                             $"burn {plan.BurnDownTime:F0} s, trip {plan.TripTime:F0} s, dv {plan.DeltaV:F0} m/s, needed {plan.DeltaVNeeded:F0} m/s");

            Assert.True(plan.Feasible, plan.Problem);
            Assert.True(plan.CoastTime > 0);
            Assert.Equal(plan.BurnUpTime + plan.CoastTime + plan.BurnDownTime, plan.TripTime, 6);
            // The burns use the budget less the margin, to within the search's precision.
            Assert.True(plan.DeltaVNeeded <= budget * 1.000001, $"needs {plan.DeltaVNeeded} of {budget}");
            Assert.True(plan.DeltaVNeeded >= budget * 0.999, $"needs {plan.DeltaVNeeded} of {budget}: should use it all to be fastest");
            Assert.True(sw.Elapsed.TotalSeconds < 2, "the window re-plans once a second; this must stay quick");
        }

        [Fact]
        public void MoreDeltaVMeansAShorterTrip()
        {
            CoastPlan small = CoastPlanner.Plan(DunaFromLowKerbinOrbit(), 60_000);
            CoastPlan large = CoastPlanner.Plan(DunaFromLowKerbinOrbit(), 120_000);
            Assert.True(small.Feasible && large.Feasible);
            Assert.True(large.TripTime < small.TripTime);
            Assert.True(large.CoastTime < small.CoastTime);
        }

        [Fact]
        public void RefusesWhenEvenACoastCantBeDone()
        {
            // 1 km/s can't stop at Duna from a trip of any length within the planner's horizon.
            CoastPlan plan = CoastPlanner.Plan(DunaFromLowKerbinOrbit(), 1_000);
            Assert.False(plan.Feasible);
            Assert.False(string.IsNullOrEmpty(plan.Problem));
        }
    }
}
