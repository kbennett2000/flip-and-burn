using System;
using System.Linq;
using FlipAndBurn.Core;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    public class TripCalculatorTests
    {
        private const double OneG = TripCalculator.OneG;
        private const double ParkingAltitude = 100_000;  // m; used throughout unless a test says otherwise

        private readonly ITestOutputHelper output;

        public TripCalculatorTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static TripRequest FromKerbin(TargetPlanet target, double ut, double accel = OneG, bool shipStill = false)
        {
            StateVector kerbin = KerbolSystem.Kerbin.Orbit.StateAt(ut);
            return new TripRequest
            {
                ShipPosition = kerbin.Position,
                ShipVelocity = shipStill ? Vec3.Zero : kerbin.Velocity,
                StartTime = ut,
                Target = target,
                Acceleration = accel,
                ParkingAltitude = ParkingAltitude,
                SunGravParameter = KerbolSystem.SunGravParameter,
            };
        }

        private void Report(string label, TripPlan p)
        {
            output.WriteLine($"{label}: feasible={p.Feasible} {p.Problem} {p.ProblemDetail}");
            if (!p.Feasible) return;
            output.WriteLine($"  trip time      {p.TripTime:F1} s = {p.TripTime / 3600:F2} h = {p.TripTime / 21600:F2} Kerbin days");
            output.WriteLine($"  time to flip   {p.TimeToFlip:F1} s = {p.TimeToFlip / 3600:F2} h");
            output.WriteLine($"  peak speed     {p.PeakSpeed / 1000:F3} km/s (relative to the ship's start velocity)");
            output.WriteLine($"  burn delta-v   {p.BurnDeltaV / 1000:F3} km/s");
            output.WriteLine($"  parking dv     {p.ParkingOrbitDeltaV:F1} m/s");
            output.WriteLine($"  total delta-v  {p.TotalDeltaV / 1000:F3} km/s");
            output.WriteLine($"  accel before/after flip {p.AccelerationBeforeFlip:F4} / {p.AccelerationAfterFlip:F4} m/s^2");
            output.WriteLine($"  closest to Sun {p.ClosestSunDistance / 1e9:F3} Gm at {p.ClosestSunTime / 3600:F2} h; near Sun={p.PassesNearSun}; gravity matters={p.GravityNotNegligible}");
            output.WriteLine($"  arrival iterations {p.ArrivalIterations}, converged={p.ArrivalConverged}: {string.Join(", ", p.ArrivalTimeHistory.Select(t => t.ToString("F3")))}");
        }

        private static void AssertRel(double expected, double actual, double tol, string what)
        {
            double rel = Math.Abs(actual - expected) / Math.Max(Math.Abs(expected), 1e-300);
            Assert.True(rel <= tol, $"{what}: expected {expected:R}, got {actual:R} (relative error {rel:E2} > {tol:E0})");
        }

        // ---------------------------------------------------------------- planets held still: hand formulas

        [Theory]
        [InlineData("Duna")]
        [InlineData("Jool")]
        public void StillPlanets_MatchHandFormulas(string targetName)
        {
            PlanetRecord target = targetName == "Duna" ? KerbolSystem.Duna : KerbolSystem.Jool;
            // Both planets frozen where they are at UT 0; ship at rest at Kerbin's position.
            TripRequest req = FromKerbin(target.AsStillTarget(0), 0, OneG, shipStill: true);
            TripPlan plan = TripCalculator.Plan(req);
            Report($"Kerbin -> {targetName}, planets still, 1 g", plan);

            Assert.True(plan.Feasible);
            double d = (plan.AimPoint - req.ShipPosition).Length;
            double tHand = 2 * Math.Sqrt(d / OneG);
            double dvHand = 2 * Math.Sqrt(d * OneG);
            output.WriteLine($"  straight-line distance to the aim point d = {d / 1e9:F6} Gm; hand t = {tHand:F3} s, hand dv = {dvHand:F3} m/s");

            AssertRel(tHand, plan.TripTime, 1e-9, "trip time vs 2*sqrt(d/a)");
            AssertRel(dvHand, plan.BurnDeltaV, 1e-9, "burn delta-v vs 2*sqrt(d*a)");
            AssertRel(tHand / 2, plan.TimeToFlip, 1e-9, "time to flip");
            AssertRel(OneG * tHand / 2, plan.PeakSpeed, 1e-9, "peak speed vs a*t/2");
            AssertRel(OneG, plan.AccelerationBeforeFlip, 1e-9, "acceleration before flip");
            AssertRel(OneG, plan.AccelerationAfterFlip, 1e-9, "acceleration after flip");
            AssertRel(Math.Sqrt(target.GravParameter / (target.Radius + ParkingAltitude)), plan.ParkingOrbitDeltaV, 1e-12, "parking orbit delta-v");
            AssertRel(plan.BurnDeltaV + plan.ParkingOrbitDeltaV, plan.TotalDeltaV, 1e-15, "total delta-v");
            Assert.True(plan.ArrivalConverged);
            Assert.True(plan.ArrivalIterations <= 3, "a still target needs no real iteration");
        }

        [Fact]
        public void StillPlanets_SimpleNumbers_DistanceOf1e10m()
        {
            // d = 1e10 m at 1 g: t = 2*sqrt(1e10/9.81) = 63,855.4 s, dv = 2*sqrt(1e10*9.81) = 626,418.6 m/s.
            var target = new TargetPlanet("Point", new FixedEphemeris(new Vec3(1e10 + 1, 0, 0)), 1.0, 1.0);
            var req = new TripRequest { ShipPosition = Vec3.Zero, ShipVelocity = Vec3.Zero, Target = target, Acceleration = OneG, ParkingAltitude = 0, SunSafetyDistance = 0 };
            TripPlan plan = TripCalculator.Plan(req);
            AssertRel(2 * Math.Sqrt(1e10 / OneG), plan.TripTime, 1e-12, "trip time");
            AssertRel(2 * Math.Sqrt(1e10 * OneG), plan.BurnDeltaV, 1e-12, "delta-v");
        }

        // ---------------------------------------------------------------- planets moving: arrival-time iteration

        [Theory]
        [InlineData("Duna")]
        [InlineData("Jool")]
        public void MovingPlanets_ArrivalIterationConverges(string targetName)
        {
            PlanetRecord target = targetName == "Duna" ? KerbolSystem.Duna : KerbolSystem.Jool;
            TripRequest req = FromKerbin(target.AsTarget(), 0);
            TripPlan plan = TripCalculator.Plan(req);
            Report($"Kerbin -> {targetName}, planets moving, 1 g", plan);

            Assert.True(plan.Feasible);
            Assert.True(plan.ArrivalConverged);
            Assert.True(plan.ArrivalIterations <= 10, $"took {plan.ArrivalIterations} iterations");

            // Each correction is smaller than the one before: the iteration is contracting.
            double[] h = plan.ArrivalTimeHistory.ToArray();
            for (int i = 2; i < h.Length; i++)
                Assert.True(Math.Abs(h[i] - h[i - 1]) <= Math.Abs(h[i - 1] - h[i - 2]) + 1e-6, $"step {i} grew");

            // The path really ends at the moving planet: at the arrival time, the ship is at the parking radius from
            // where the planet actually is then, with the planet's actual velocity.
            StateVector planetThen = target.Orbit.StateAt(plan.ArrivalTime);
            StateVector shipThen = plan.ShipStateAt(plan.TripTime);
            double parkingRadius = target.Radius + ParkingAltitude;
            Assert.True(Math.Abs((shipThen.Position - planetThen.Position).Length - parkingRadius) < 1.0, "ship not at parking radius from the planet");
            Assert.True((shipThen.Velocity - planetThen.Velocity).Length < 1e-6, "ship velocity does not match the planet's");

            // Starting state is the ship's.
            StateVector start = plan.ShipStateAt(0);
            Assert.True((start.Position - req.ShipPosition).Length < 1e-6);
            Assert.True((start.Velocity - req.ShipVelocity).Length < 1e-9);

            // The stronger half of the burn is exactly the chosen acceleration.
            AssertRel(OneG, Math.Max(plan.AccelerationBeforeFlip, plan.AccelerationAfterFlip), 1e-9, "peak acceleration");

            // Matching the planet's velocity costs at least the velocity difference.
            Assert.True(plan.BurnDeltaV >= (planetThen.Velocity - req.ShipVelocity).Length);
            Assert.False(plan.PassesNearSun);
            Assert.False(plan.GravityNotNegligible);

            TripPlan still = TripCalculator.Plan(FromKerbin(target.AsStillTarget(0), 0, OneG, shipStill: true));
            output.WriteLine($"  vs planets still: trip time {plan.TripTime - still.TripTime:+0.0;-0.0} s, total dv {(plan.TotalDeltaV - still.TotalDeltaV) / 1000:+0.000;-0.000} km/s");
        }

        [Fact]
        public void MovingPlanets_OpmPlanetFitsTheSameWay()
        {
            // OPM's planets are ordinary Sun-orbiting bodies; nothing special is needed.
            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Plock.AsTarget(), 0));
            Report("Kerbin -> Plock (OPM), planets moving, 1 g", plan);
            Assert.True(plan.Feasible);
            Assert.True(plan.ArrivalConverged);
        }

        // ---------------------------------------------------------------- the Sun in the way

        [Fact]
        public void SunInTheWay_StraightThroughTheSun_IsFlagged()
        {
            // Ship at rest on +X at Kerbin's distance, Duna held still on -X at its distance: the line crosses the Sun.
            var duna = new TargetPlanet("Duna", new FixedEphemeris(new Vec3(-KerbolSystem.Duna.Orbit.SemiMajorAxis, 0, 0)),
                KerbolSystem.Duna.GravParameter, KerbolSystem.Duna.Radius);
            var req = new TripRequest
            {
                ShipPosition = new Vec3(KerbolSystem.Kerbin.Orbit.SemiMajorAxis, 0, 0),
                ShipVelocity = Vec3.Zero,
                Target = duna,
                Acceleration = OneG,
                ParkingAltitude = ParkingAltitude,
                SunGravParameter = KerbolSystem.SunGravParameter,
            };
            TripPlan plan = TripCalculator.Plan(req);
            Report("Through the Sun, still", plan);

            Assert.True(plan.Feasible);
            Assert.True(plan.PassesNearSun);
            Assert.True(plan.ClosestSunDistance < 1e3, $"closest approach {plan.ClosestSunDistance} m");
            Assert.True(plan.ClosestSunDistance < KerbolSystem.SunRadius, "path goes through the Sun itself");
        }

        [Fact]
        public void SunInTheWay_DunaOnTheFarSide_IsFlagged()
        {
            // Find when Duna is most nearly opposite Kerbin during the first three Kerbin years.
            double bestUt = 0, bestAngle = 0;
            for (double ut = 0; ut < 3 * KerbolSystem.Kerbin.Orbit.Period; ut += 3600)
            {
                Vec3 k = KerbolSystem.Kerbin.Orbit.StateAt(ut).Position, d = KerbolSystem.Duna.Orbit.StateAt(ut).Position;
                double angle = Math.Acos(Math.Max(-1, Math.Min(1, Vec3.Dot(k, d) / (k.Length * d.Length))));
                if (angle > bestAngle)
                {
                    bestAngle = angle;
                    bestUt = ut;
                }
            }

            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Duna.AsTarget(), bestUt));
            output.WriteLine($"Duna most opposite Kerbin at UT {bestUt:F0} s ({bestAngle * 180 / Math.PI:F2} degrees apart)");
            Report("Kerbin -> Duna, Duna behind the Sun, moving", plan);

            Assert.True(plan.Feasible);
            Assert.True(plan.PassesNearSun);
            Assert.True(plan.ClosestSunDistance < TripCalculator.DefaultSunSafetyDistance);
        }

        [Fact]
        public void SunNotInTheWay_SameSide_IsNotFlagged()
        {
            var duna = new TargetPlanet("Duna", new FixedEphemeris(new Vec3(KerbolSystem.Duna.Orbit.SemiMajorAxis, 0, 0)),
                KerbolSystem.Duna.GravParameter, KerbolSystem.Duna.Radius);
            var req = new TripRequest
            {
                ShipPosition = new Vec3(KerbolSystem.Kerbin.Orbit.SemiMajorAxis, 0, 0),
                Target = duna,
                ParkingAltitude = ParkingAltitude,
                SunGravParameter = KerbolSystem.SunGravParameter,
            };
            TripPlan plan = TripCalculator.Plan(req);
            Assert.False(plan.PassesNearSun);
            AssertRel(KerbolSystem.Kerbin.Orbit.SemiMajorAxis, plan.ClosestSunDistance, 1e-9, "closest approach is the start");
        }

        [Fact]
        public void SunSafetyDistance_IsJustInsideMohoPerihelion()
        {
            double mohoPerihelion = KerbolSystem.Moho.Orbit.SemiMajorAxis * (1 - KerbolSystem.Moho.Orbit.Eccentricity);
            output.WriteLine($"Moho perihelion {mohoPerihelion:E4} m; safety distance {TripCalculator.DefaultSunSafetyDistance:E4} m = {TripCalculator.DefaultSunSafetyDistance / KerbolSystem.SunRadius:F1} solar radii");
            Assert.InRange(TripCalculator.DefaultSunSafetyDistance, 0.9 * mohoPerihelion, mohoPerihelion);

            // Every Sun-orbiting planet's perihelion, stock and OPM, is outside it, so no trip has to end inside it.
            foreach (PlanetRecord p in KerbolSystem.StockSunPlanets.Concat(KerbolSystem.OpmSunPlanets).Distinct())
                Assert.True(p.Orbit.SemiMajorAxis * (1 - p.Orbit.Eccentricity) > TripCalculator.DefaultSunSafetyDistance, p.Name);
        }

        // ---------------------------------------------------------------- extremes: no crashes, sensible answers

        [Fact]
        public void LowAcceleration_StillPlans_AndWarnsThatGravityMatters()
        {
            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Jool.AsTarget(), 0, accel: 1e-4));
            Report("Kerbin -> Jool at 0.0001 m/s^2", plan);
            Assert.True(plan.Feasible);
            Assert.True(plan.ArrivalConverged);
            Assert.True(plan.GravityNotNegligible, "the Sun's pull (~0.006 m/s^2 at Kerbin) dwarfs 1e-4 m/s^2");
            Assert.InRange(plan.TripTime, 1e6, TripCalculator.DefaultMaxTripTime);
            Assert.True(double.IsFinite(plan.TotalDeltaV) && plan.TotalDeltaV > 0);
        }

        [Theory]
        [InlineData(1e-12)]
        [InlineData(1e-15)]
        public void AccelerationTooLowToMatter_ReportsNoSolution(double accel)
        {
            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Jool.AsTarget(), 0, accel));
            Report($"Kerbin -> Jool at {accel} m/s^2", plan);
            Assert.False(plan.Feasible);
            Assert.Equal(TripProblem.NoSolutionWithinMaxTripTime, plan.Problem);
        }

        [Fact]
        public void DistanceTooLargeToMatter_ReportsNoSolution()
        {
            // At 1 g, 1e22 m takes 2*sqrt(1e22/9.81) = 6.4e10 s, past the 1e10 s limit.
            var farAway = new TargetPlanet("Far", new FixedEphemeris(new Vec3(1e22, 0, 0)), 1e12, 1e5);
            TripPlan plan = TripCalculator.Plan(new TripRequest { ShipPosition = new Vec3(1e11, 0, 0), Target = farAway, Acceleration = OneG });
            Report("1e22 m away at 1 g", plan);
            Assert.False(plan.Feasible);
            Assert.Equal(TripProblem.NoSolutionWithinMaxTripTime, plan.Problem);
        }

        [Fact]
        public void HugeAcceleration_GivesShortTrip()
        {
            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Duna.AsTarget(), 0, accel: 1e6));
            Report("Kerbin -> Duna at 1e6 m/s^2", plan);
            Assert.True(plan.Feasible);
            Assert.InRange(plan.TripTime, 1, 1000);
        }

        [Fact]
        public void AlreadyThere_IsAZeroLengthTrip()
        {
            var planet = new TargetPlanet("Here", new FixedEphemeris(new Vec3(1e10, 0, 0)), 1e12, 1e5);
            var req = new TripRequest { ShipPosition = new Vec3(1e10 - 1e5, 0, 0), ShipVelocity = Vec3.Zero, Target = planet, ParkingAltitude = 0 };
            TripPlan plan = TripCalculator.Plan(req);
            Assert.True(plan.Feasible);
            Assert.Equal(0, plan.TripTime);
            Assert.Equal(0, plan.BurnDeltaV);
            AssertRel(Math.Sqrt(1e12 / 1e5), plan.TotalDeltaV, 1e-12, "only the parking orbit burn");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-9.81)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void BadAcceleration_IsInvalidInput(double accel)
        {
            TripPlan plan = TripCalculator.Plan(FromKerbin(KerbolSystem.Duna.AsTarget(), 0, accel));
            Assert.Equal(TripProblem.InvalidInput, plan.Problem);
            Assert.False(string.IsNullOrEmpty(plan.ProblemDetail));
        }

        [Fact]
        public void OtherBadInputs_AreInvalidInput()
        {
            TripRequest Good() => FromKerbin(KerbolSystem.Duna.AsTarget(), 0);

            Assert.Equal(TripProblem.InvalidInput, TripCalculator.Plan(null).Problem);
            var r1 = Good(); r1.Target = null; Assert.Equal(TripProblem.InvalidInput, TripCalculator.Plan(r1).Problem);
            var r2 = Good(); r2.ParkingAltitude = -1; Assert.Equal(TripProblem.InvalidInput, TripCalculator.Plan(r2).Problem);
            var r3 = Good(); r3.ShipPosition = new Vec3(double.NaN, 0, 0); Assert.Equal(TripProblem.InvalidInput, TripCalculator.Plan(r3).Problem);
            var r4 = Good(); r4.MaxTripTime = 0; Assert.Equal(TripProblem.InvalidInput, TripCalculator.Plan(r4).Problem);
        }
    }
}
