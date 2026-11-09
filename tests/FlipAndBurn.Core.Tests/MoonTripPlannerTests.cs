using System;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The planning side of moon trips (ADR 0005): the departure window, the gravity rules, the saved trip.</summary>
    public class MoonTripPlannerTests
    {
        // A ship in an 80 km orbit around Kerbin (at the centre), at an angle along that orbit, flying to the Mun.
        private static TripRequest FromLowKerbinOrbit(double angle, double accelG, PlanetRecord to = null)
        {
            SimSystem sys = SimSystem.Around(KerbolSystem.Kerbin, KerbolSystem.Kerbin, 80_000, angle);
            var sim = new ShipSimulator(sys.Departure, sys.TargetRecord(to ?? KerbolSystem.Mun), 0.1, sys.CentralGravParameter, sys.Others);
            sim.StartInOrbit(sys.Departure, sys.StartAltitude, 0, angle);
            PlanetRecord target = sys.TargetRecord(to ?? KerbolSystem.Mun);
            TargetPlanet tp = target.AsTarget();
            tp.SphereOfInfluence = target.SphereOfInfluence;
            var request = new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = 0, Target = tp, Departure = sys.DepartureFor(),
                Acceleration = accelG * TripCalculator.OneG, ParkingAltitude = SimSystem.DefaultParking(target), SunGravParameter = sys.CentralGravParameter,
            };
            sys.Configure(request);
            return request;
        }

        [Fact]
        public void DepartureWindow_WaitsOnlyWhenTheMunIsBehindKerbin()
        {
            // The Mun starts at 97 degrees (mean anomaly 1.7 rad on a circular orbit): overhead from a ship at 90 degrees,
            // behind Kerbin from one at 270.
            double now = DepartureWindow.Find(FromLowKerbinOrbit(97 * Math.PI / 180, 1), out _);
            double behind = DepartureWindow.Find(FromLowKerbinOrbit(277 * Math.PI / 180, 1), out StateVector at);
            Assert.Equal(0, now);
            Assert.InRange(behind, 600, 1900);   // well into the 1,850 s orbit, never more than one
            TripRequest r = FromLowKerbinOrbit(277 * Math.PI / 180, 1);
            Assert.True(DepartureWindow.Clear(r, at, behind, r.Departure, DepartureWindow.SafeRadiusOf(r)));
        }

        [Fact]
        public void LeavingLowKerbinOrbit_TooLittleThrust_IsRefusedWithTheMinimum()
        {
            TripPlan plan = TripCalculator.Plan(FromLowKerbinOrbit(0, 0.5));
            Assert.Equal(TripProblem.DepartureGravityTooStrong, plan.Problem);
            double g = KerbolSystem.Kerbin.GravParameter / Math.Pow(680_000, 2);
            Assert.Equal(1.1 * g, plan.MinimumDepartureAcceleration, 6);   // 8.4 m/s^2, 0.86 g
            Assert.Equal(TripProblem.None, TripCalculator.Plan(FromLowKerbinOrbit(0, 1)).Problem);
        }

        [Fact]
        public void TheEstimate_AllowsForGravity_AndIncludesTheWait()
        {
            TripPlan plan = TripCalculator.Plan(FromLowKerbinOrbit(277 * Math.PI / 180, 1));
            Assert.True(plan.Feasible, plan.ProblemDetail);
            Assert.True(plan.GravityIncluded);
            Assert.True(plan.DepartureWait > 600);
            Assert.True(plan.TripTime > plan.DepartureWait + 1800 && plan.TimeToFlip > plan.DepartureWait);
            Assert.True(plan.ClosestSunDistance >= 679_000, $"passes {plan.ClosestSunDistance / 1000:F0} km from Kerbin's centre");
        }

        [Fact]
        public void CoastDownToThePlanet_NeedsOneAndAHalfTimesItsPull()
        {
            SimSystem sys = SimSystem.Around(KerbolSystem.Kerbin, KerbolSystem.Mun, 20_000, 1.0);
            var sim = new ShipSimulator(sys.Departure, sys.Planet, 0.1, sys.CentralGravParameter, sys.Others);
            sim.StartInOrbit(sys.Departure, 20_000, 0, 1.0);
            TargetPlanet kerbin = sys.Planet.AsTarget();
            kerbin.IsCentralBody = true;
            var request = new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = 0, Target = kerbin, Departure = sys.DepartureFor(),
                Acceleration = TripCalculator.OneG, ParkingAltitude = 80_000,
            };
            sys.Configure(request);
            CoastPlan at1g = CoastPlanner.Plan(request, 15_000);
            Assert.False(at1g.Feasible);
            Assert.Equal(1.5 * KerbolSystem.Kerbin.GravParameter / Math.Pow(680_000, 2), at1g.MinimumAcceleration, 6);   // 1.17 g
            request.Acceleration = 2 * TripCalculator.OneG;
            Assert.True(CoastPlanner.Plan(request, 20_000).Feasible);
        }

        [Fact]
        public void SavedTrip_MoonTripIsFormat2_BetweenPlanetsStaysFormat1()
        {
            var moon = new TripRecord { Target = "Mun", Acceleration = 9.81, ChosenAcceleration = 9.81, Centre = "Kerbin", Departed = false };
            var duna = new TripRecord { Target = "Duna", Acceleration = 9.81, ChosenAcceleration = 9.81 };
            Assert.Contains(moon.ToValues(), kv => kv.Key == "version" && kv.Value == "2");
            Assert.Contains(duna.ToValues(), kv => kv.Key == "version" && kv.Value == "1");
            Assert.DoesNotContain(duna.ToValues(), kv => kv.Key == "centre");
            TripRecord back = TripRecord.FromValues(moon.ToValues(), out string problem);
            Assert.Null(problem);
            Assert.Equal("Kerbin", back.Centre);
            Assert.False(back.Departed);
            TripRecord dunaBack = TripRecord.FromValues(duna.ToValues(), out problem);
            Assert.Equal("", dunaBack.Centre);
            Assert.True(dunaBack.Departed);
        }

        [Fact]
        public void Waiting_WarpsOnRails_AndTheArrivalWatchWaitsForTheBraking()
        {
            var waiting = new GuidanceCommand { Phase = GuidancePhase.Waiting, TimeToBurn = 900 };
            WarpDecision d = WarpPolicy.Decide(waiting, pointed: true);
            Assert.Equal(WarpMode.Rails, d.Mode);
            Assert.Equal(900 / WarpPolicy.RailsLeadSeconds, d.MaxRate, 6);
            Assert.Equal(WarpMode.None, WarpPolicy.Decide(waiting, pointed: false).Mode);
            Assert.False(ArrivalSafety.Watching(true, GuidancePhase.Accelerate));
            Assert.False(ArrivalSafety.Watching(true, GuidancePhase.Waiting));
            Assert.True(ArrivalSafety.Watching(true, GuidancePhase.Decelerate));
            Assert.True(ArrivalSafety.Watching(false, GuidancePhase.Accelerate));
        }
    }
}
