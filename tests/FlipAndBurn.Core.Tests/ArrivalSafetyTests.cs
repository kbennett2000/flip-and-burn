using System;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// A trip that can't arrive cleanly makes the ship's orbit safe instead of crashing into the target or flying past it
    /// (ADR 0003, "Arriving"). The watch must never fire on a normal arrival (checked in every simulated trip,
    /// GuidanceSimulationTests.CheckBar).
    /// </summary>
    public class ArrivalSafetyTests
    {
        private readonly ITestOutputHelper output;
        public ArrivalSafetyTests(ITestOutputHelper output) { this.output = output; }

        private static readonly PlanetRecord Duna = KerbolSystem.Duna;
        private const double ParkingAltitude = 60_000;
        private static double SafeRadius => Duna.Radius + Flights.TargetSafeAltitude(Duna);
        private static double ParkingRadius => Duna.Radius + ParkingAltitude;
        private const double UT = 1_000_000;

        private static (Vec3 r, Vec3 v) Rel(double altitude, double radialSpeed, double sidewaysSpeed)
        {
            var up = new Vec3(1, 0, 0);
            var side = new Vec3(0, 1, 0);
            return (up * (Duna.Radius + altitude), up * radialSpeed + side * sidewaysSpeed);
        }

        [Fact]
        public void Check_FallingTooFastToStop()
        {
            // 3,000 km up, falling at 7.4 km/s, a 0.4 g trip on a ship that can keep up 1 g: stopping at 1 g takes about
            // 2,800 km, more than 90 % of the 2,950 km left above the atmosphere: rescue now, while it still can.
            (Vec3 r, Vec3 v) = Rel(3_000_000, -7_400, 0);
            Assert.Equal(ArrivalIssue.CantStopInTime,
                ArrivalSafety.Check(r, v, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, 0.4 * TripCalculator.OneG, 1.0 * TripCalculator.OneG));
            // Falling at 4 km/s it stops with room to spare.
            (Vec3 r2, Vec3 v2) = Rel(3_000_000, -4_000, 0);
            Assert.Equal(ArrivalIssue.None,
                ArrivalSafety.Check(r2, v2, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, 0.4 * TripCalculator.OneG, 1.0 * TripCalculator.OneG));
        }

        [Fact]
        public void Check_NormalBrakingAtTheChosenAcceleration_NotAnEmergency()
        {
            // A flip-and-burn arrival brakes at 99-100 % of the chosen acceleration by design: 26.2 km/s with 35,000 km to go
            // at 1 g (from the simulator's Duna trip). Not an emergency, whatever the engines' headroom.
            (Vec3 r, Vec3 v) = Rel(35_074_300, -26_213.4, 0);
            Assert.Equal(ArrivalIssue.None,
                ArrivalSafety.Check(r, v, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, TripCalculator.OneG, 3.17 * TripCalculator.OneG));
            Assert.Equal(ArrivalIssue.None,
                ArrivalSafety.Check(r, v, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, TripCalculator.OneG, TripCalculator.OneG));
        }

        [Fact]
        public void Check_FlyingPast()
        {
            // 2,000 km up, outbound at 2 km/s: faster than Duna's escape speed there (about 1.0 km/s).
            (Vec3 r, Vec3 v) = Rel(2_000_000, 2_000, 300);
            Assert.Equal(ArrivalIssue.Escaping, ArrivalSafety.Check(r, v, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, 3.9, 3.9));
            // In the parking orbit: fine.
            (Vec3 r2, Vec3 v2) = Rel(ParkingAltitude, 0, Math.Sqrt(Duna.GravParameter / ParkingRadius));
            Assert.Equal(ArrivalIssue.None, ArrivalSafety.Check(r2, v2, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, 3.9, 3.9));
        }

        [Fact]
        public void Check_OutsideTheSphereOfInfluence_Ignored()
        {
            (Vec3 r, Vec3 v) = Rel(Duna.SphereOfInfluence * 1.5, -20_000, 0);
            Assert.Equal(ArrivalIssue.None, ArrivalSafety.Check(r, v, Duna.GravParameter, SafeRadius, ParkingRadius, Duna.SphereOfInfluence, 0.1, 0.2));
        }

        /// <summary>Fly the safe-orbit mode from a state near Duna; returns the orbit it ends in.</summary>
        private OrbitShape FlySafeOrbit(string name, double altitude, double radialSpeed, double sidewaysSpeed, double acceleration, double tripAcceleration)
        {
            var sim = new ShipSimulator(KerbolSystem.Kerbin, Duna, 10 * Math.PI / 180);
            StateVector planet = Duna.Orbit.StateAt(UT);
            (Vec3 r, Vec3 v) = Rel(altitude, radialSpeed, sidewaysSpeed);
            sim.UT = UT;
            sim.Position = planet.Position + r;
            sim.Velocity = planet.Velocity + v;
            sim.Facing = (-v).Normalized();
            TargetPlanet target = Duna.AsTarget();
            target.SphereOfInfluence = Duna.SphereOfInfluence;
            var guidance = new FlipAndBurnGuidance(new GuidanceConfig
            {
                Target = target, Acceleration = tripAcceleration, ParkingAltitude = ParkingAltitude, TurnRate = 10 * Math.PI / 180,
                SunGravParameter = KerbolSystem.SunGravParameter,
            });
            var start = new GuidanceInput { UT = sim.UT, Position = sim.Position, Velocity = sim.Velocity, Facing = sim.Facing };
            guidance.EnterSafeOrbit(start, name, acceleration);
            double lowest = double.MaxValue, used = 0;
            bool done = false;
            for (int i = 0; i < 2_000_000 && !done; i++)
            {
                GuidanceCommand cmd = guidance.Update(new GuidanceInput { UT = sim.UT, Position = sim.Position, Velocity = sim.Velocity, Facing = sim.Facing });
                if (cmd.Phase == GuidancePhase.Done)
                {
                    Assert.True(cmd.SafeOrbit);
                    done = true;
                    break;
                }

                sim.Step(0.1, cmd.Direction, cmd.Acceleration);
                used += cmd.Acceleration * 0.1;
                lowest = Math.Min(lowest, (sim.Position - Duna.Orbit.StateAt(sim.UT).Position).Length - Duna.Radius);
            }

            OrbitShape orbit = sim.OrbitAround(Duna);
            output.WriteLine($"{name}: done {done} after {sim.UT - UT:F0} s, {used:F0} m/s; lowest {lowest / 1000:F1} km; orbit {(orbit.PeriapsisRadius - Duna.Radius) / 1000:F1} x " +
                             $"{(orbit.ApoapsisRadius - Duna.Radius) / 1000:F1} km, e {orbit.Eccentricity:F4} (safe above {Flights.TargetSafeAltitude(Duna) / 1000:F0} km)");
            Assert.True(done, "the safe orbit was never reached");
            Assert.True(lowest > Flights.TargetSafeAltitude(Duna), "dipped below the safe altitude");
            Assert.True(used < 1.2 * (v - (new Vec3(0, 1, 0) * Math.Sqrt(Duna.GravParameter / r.Length))).Length + 200, "about the delta-v of going straight to circular");
            Assert.True(orbit.PeriapsisRadius > SafeRadius && orbit.Eccentricity < 0.01, "a safe, circular orbit");
            return orbit;
        }

        [Fact]
        public void SafeOrbit_FromFallingTooFast()
        {
            // The trip brakes at 0.4 g and can't stop; the engines' full 1 g can.
            FlySafeOrbit("falling at 6 km/s from 3,000 km", 3_000_000, -6_000, 200, TripCalculator.OneG, 0.4 * TripCalculator.OneG);
        }

        [Fact]
        public void SafeOrbit_FromFlyingPast()
        {
            OrbitShape o = FlySafeOrbit("outbound at 2 km/s from 2,000 km", 2_000_000, 2_000, 300, 0.4 * TripCalculator.OneG, 0.4 * TripCalculator.OneG);
            Assert.True(o.ApoapsisRadius < Duna.SphereOfInfluence, "captured, not flying past");
        }

        [Fact]
        public void SafeOrbit_FromJustAboveTheParkingOrbit()
        {
            // The plan failed close in (e.g. the engines lost thrust while braking): circular where it stops, at or above the
            // parking orbit.
            OrbitShape o = FlySafeOrbit("100 km up, falling at 300 m/s", 100_000, -300, 1_100, 0.4 * TripCalculator.OneG, 0.4 * TripCalculator.OneG);
            Assert.True(o.PeriapsisRadius >= ParkingRadius - 1_000, "at or above the parking orbit");
        }

        // ------------------------------------------------------------------ the rescue and heat

        // A simple heat model for the Bebop Refueller's drive: the hottest part heats while the thrust is above what the ship
        // sustains (0.45 g, measured in cycle 9) and cools below it. At 1 g it reaches 90 % of its limit in about 7 minutes
        // from 40 %, as the drive did in the cycle 9 overheating run.
        private const double SustainableG = 0.45, HeatRate = 0.001;

        private (bool done, double hottest, double lastAccelG, int stepDowns, OrbitShape orbit) RescueWithHeat(double startAccelG, bool guard)
        {
            var sim = new ShipSimulator(KerbolSystem.Kerbin, Duna, 10 * Math.PI / 180);
            StateVector planet = Duna.Orbit.StateAt(UT);
            (Vec3 r, Vec3 v) = Rel(4_000_000, 6_000, 300);   // flying past Duna fast: the watch fires "Escaping"; 6 km/s to take off
            sim.UT = UT;
            sim.Position = planet.Position + r;
            sim.Velocity = planet.Velocity + v;
            sim.Facing = (-v).Normalized();
            TargetPlanet target = Duna.AsTarget();
            target.SphereOfInfluence = Duna.SphereOfInfluence;
            var guidance = new FlipAndBurnGuidance(new GuidanceConfig
            {
                Target = target, Acceleration = TripCalculator.OneG, ParkingAltitude = ParkingAltitude, TurnRate = 10 * Math.PI / 180,
                SunGravParameter = KerbolSystem.SunGravParameter,
            });
            guidance.EnterSafeOrbit(new GuidanceInput { UT = sim.UT, Position = sim.Position, Velocity = sim.Velocity, Facing = sim.Facing },
                "flying past", startAccelG * TripCalculator.OneG);
            var heat = new HeatGuard();
            double hottest = 0.4, peak = hottest;
            int stepDowns = 0;
            const double dt = 0.1;
            for (int i = 0; i < 400_000; i++)
            {
                GuidanceCommand cmd = guidance.Update(new GuidanceInput { UT = sim.UT, Position = sim.Position, Velocity = sim.Velocity, Facing = sim.Facing });
                if (cmd.Phase == GuidancePhase.Done) return (true, peak, guidance.SafeOrbitAcceleration / TripCalculator.OneG, stepDowns, sim.OrbitAround(Duna));
                double a = cmd.Acceleration;
                if (guard && heat.Check(sim.UT, hottest, guidance.SafeOrbitAcceleration, a, a, false, a > 0, true, out double stepTo) != HeatStepDown.None)
                {
                    guidance.SetSafeOrbitAcceleration(stepTo);
                    stepDowns++;
                }

                sim.Step(dt, cmd.Direction, a);
                hottest = Math.Max(0.3, hottest + HeatRate * (a / (SustainableG * TripCalculator.OneG) - 1) * dt);
                peak = Math.Max(peak, hottest);
                if (hottest >= 1) return (false, peak, guidance.SafeOrbitAcceleration / TripCalculator.OneG, stepDowns, sim.OrbitAround(Duna));   // the drive is gone
            }

            return (false, peak, guidance.SafeOrbitAcceleration / TripCalculator.OneG, stepDowns, sim.OrbitAround(Duna));
        }

        /// <summary>
        /// The rescue uses what the engines can keep up, and keeps the heat guard running: at the trip's nominal 1 g the
        /// Bebop's drive would overheat before the orbit is safe; at what the trip had proven it can hold (0.48 g after the
        /// cycle 9 step-downs) it stays within its limit; and even started too high, the heat guard brings it down in time.
        /// </summary>
        [Fact]
        public void Rescue_StaysWithinTheHeatLimit()
        {
            var nominal = RescueWithHeat(1.0, guard: false);
            var ours = RescueWithHeat(0.48, guard: true);
            var tooHigh = RescueWithHeat(1.0, guard: true);
            output.WriteLine($"at the nominal 1 g, no heat guard: {(nominal.done ? "safe orbit" : "drive overheated")}, hottest part {nominal.hottest * 100:F0}% of its limit");
            output.WriteLine($"at the proven 0.48 g, heat guard on: {(ours.done ? "safe orbit" : "NOT DONE")}, hottest {ours.hottest * 100:F0}%, " +
                             $"{ours.stepDowns} step-downs, ended at {ours.lastAccelG:F2} g; orbit {(ours.orbit.PeriapsisRadius - Duna.Radius) / 1000:F0} x {(ours.orbit.ApoapsisRadius - Duna.Radius) / 1000:F0} km");
            output.WriteLine($"started too high at 1 g, heat guard on: {(tooHigh.done ? "safe orbit" : "NOT DONE")}, hottest {tooHigh.hottest * 100:F0}%, " +
                             $"{tooHigh.stepDowns} step-downs, ended at {tooHigh.lastAccelG:F2} g");

            Assert.False(nominal.done, "at the nominal thrust the drive should overheat in this model");
            Assert.True(nominal.hottest >= 1);
            Assert.True(ours.done && ours.hottest < 1, "the rescue at sustainable thrust stays within the limit");
            Assert.True(ours.orbit.PeriapsisRadius > SafeRadius && ours.orbit.ApoapsisRadius < Duna.SphereOfInfluence);
            Assert.True(tooHigh.done && tooHigh.hottest < 1, "the heat guard brings an overestimate down in time");
        }
    }
}
