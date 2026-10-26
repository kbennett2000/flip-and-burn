using System;
using FlipAndBurn.Core;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    public class ShipPerformanceTests
    {
        private const double G0 = ShipPerformance.StandardGravity;

        [Fact]
        public void StockEngine_IsTheRocketEquation()
        {
            var ship = new ShipPerformance(fullThrust: 200_000, vacuumIsp: 350, ispThrottleExponent: 0, startMass: 20_000, endMass: 10_000);
            double rocket = 350 * G0 * Math.Log(2);
            Assert.Equal(rocket, ship.FullThrustDeltaV, 9);
            Assert.Equal(rocket, ship.DeltaVAtAcceleration(9.81), 9);   // throttling doesn't change Isp
            Assert.Equal(10.0, ship.MaxAcceleration, 12);
        }

        [Fact]
        public void ThrottleExponent_MatchesNumericalIntegration()
        {
            // Holding a constant acceleration below full thrust, Isp = Isp0 * throttle^0.5. Integrate step by step.
            var ship = new ShipPerformance(4_746_093.75, 281_250, 0.5, 152_640, 141_420);
            double a = 9.81, m = ship.StartMass, dv = 0, dt = 0.5;
            while (m > ship.EndMass)
            {
                double throttle = a * m / ship.FullThrust;
                double isp = ship.VacuumIsp * Math.Pow(throttle, 0.5);
                double mdot = a * m / (isp * G0);
                double step = Math.Min(dt, (m - ship.EndMass) / mdot);
                dv += a * step;
                m -= mdot * step;
            }

            Assert.True(Math.Abs(ship.DeltaVAtAcceleration(a) / dv - 1) < 1e-4, $"closed form {ship.DeltaVAtAcceleration(a)} vs integrated {dv}");
            Assert.True(ship.DeltaVAtAcceleration(a) < 0.6 * ship.FullThrustDeltaV, "throttling down costs this drive a lot of Isp");
        }

        [Fact]
        public void AtFullThrust_TheTwoAgreeForSmallFuelFractions()
        {
            var ship = new ShipPerformance(1e6, 1e5, 0.5, 100_000, 99_000);
            double full = ship.FullThrustDeltaV, held = ship.DeltaVAtAcceleration(ship.MaxAcceleration);
            Assert.True(Math.Abs(held / full - 1) < 0.01, $"{held} vs {full}");
        }

        [Fact]
        public void AboveMaxAcceleration_GivesZero()
        {
            var ship = new ShipPerformance(1e5, 300, 0, 10_000, 5_000);
            Assert.Equal(0, ship.DeltaVAtAcceleration(10.01));
            Assert.True(ship.DeltaVAtAcceleration(9.99) > 0);
        }

        [Fact]
        public void NoEngineOrNoFuel_GivesZero()
        {
            Assert.Equal(0, new ShipPerformance(0, 0, 0, 1000, 1000).FullThrustDeltaV);
            Assert.Equal(0, new ShipPerformance(1e5, 300, 0, 1000, 1000).DeltaVAtAcceleration(1));
        }

        [Theory]
        // radius, atmosphere depth, max terrain, SOI, expected (m)
        [InlineData(600_000, 70_000, 6_764, 84_159_286, 80_000)]      // atmosphere 70 km -> 77 -> 80 km
        [InlineData(320_000, 50_000, 8_264, 47_921_949, 60_000)]      // 50 km -> 55 -> 60 km
        [InlineData(700_000, 90_000, 7_526, 85_109_364, 100_000)]     // 90 km -> 99 -> 100 km
        [InlineData(6_000_000, 200_000, 0, 2_455_985_185, 220_000)]   // 200 km -> 220 km
        [InlineData(250_000, 0, 6_817, 9_646_663, 20_000)]            // airless: 6.8 km + 10 km -> 20 km
        [InlineData(138_000, 0, 0, 32_832_839, 20_000)]               // unknown terrain: 10 % of radius -> 13.8 -> 20 km
        [InlineData(100_000, 0, 60_000, 150_000, 25_000)]             // 70 km wanted, kept inside the SOI: half of (SOI - radius)
        public void DefaultParkingAltitude(double radius, double atm, double terrain, double soi, double expected)
        {
            Assert.Equal(expected, ParkingOrbit.DefaultAltitude(radius, atm, terrain, soi));
        }

        [Fact]
        public void Readiness_ReportsBothShortfalls()
        {
            TripPlan plan = TripCalculator.Plan(new TripRequest
            {
                ShipPosition = KerbolSystem.Kerbin.Orbit.StateAt(0).Position,
                ShipVelocity = KerbolSystem.Kerbin.Orbit.StateAt(0).Velocity,
                Target = KerbolSystem.Duna.AsTarget(),
                Acceleration = 9.81,
                ParkingAltitude = 60_000,
            });

            var weak = new ShipPerformance(50_000, 350, 0, 10_000, 5_000);       // 5 m/s^2 max
            TripReadiness r1 = TripReadiness.Check(plan, weak, 9.81);
            Assert.False(r1.CanHoldAcceleration);
            Assert.False(r1.Ready);

            var thirsty = new ShipPerformance(4_746_093.75, 281_250, 0.5, 152_640, 141_420);
            TripReadiness r2 = TripReadiness.Check(plan, thirsty, 9.81);
            Assert.True(r2.CanHoldAcceleration);
            Assert.False(r2.HasEnoughDeltaV);
            Assert.True(r2.DeltaVShortfall > 900_000);

            var torch = new ShipPerformance(1e8, 1e7, 0, 100_000, 50_000);
            Assert.True(TripReadiness.Check(plan, torch, 9.81).Ready);
        }
    }
}
