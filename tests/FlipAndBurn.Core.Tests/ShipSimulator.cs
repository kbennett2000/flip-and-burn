using System;
using System.Collections.Generic;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Flies a ship under the guidance, outside KSP. Gravity: the Sun, the target planet (on its real orbit) and the
    /// departure planet while the ship is inside its sphere of influence. KSP's physics step is 0.02 s; physics warp
    /// multiplies it. The ship turns no faster than its turn rate and thrusts along where it actually points.
    /// </summary>
    public sealed class ShipSimulator
    {
        public const double PhysicsStep = 0.02;

        private readonly PlanetRecord target, departure;
        private readonly double sunMu = KerbolSystem.SunGravParameter;
        private readonly double turnRate;

        public double UT;
        public Vec3 Position, Velocity, Facing;

        public ShipSimulator(PlanetRecord departure, PlanetRecord target, double turnRate)
        {
            this.departure = departure;
            this.target = target;
            this.turnRate = turnRate;
        }

        /// <summary>Put the ship in a circular, equatorial, prograde orbit around <paramref name="body"/>.</summary>
        public void StartInOrbit(PlanetRecord body, double altitude, double ut, double angleRad = 0)
        {
            UT = ut;
            StateVector planet = body.Orbit.StateAt(ut);
            double r = body.Radius + altitude, v = Math.Sqrt(body.GravParameter / r);
            var radial = new Vec3(Math.Cos(angleRad), Math.Sin(angleRad), 0);
            var along = new Vec3(-Math.Sin(angleRad), Math.Cos(angleRad), 0);
            Position = planet.Position + radial * r;
            Velocity = planet.Velocity + along * v;
            Facing = along;
        }

        public void Step(double dt, Vec3 direction, double acceleration)
        {
            Turn(direction, dt);
            Vec3 thrust = Facing * acceleration;

            // RK4 with the thrust held for the whole step (KSP applies forces once per physics step).
            Vec3 r = Position, v = Velocity;
            double t = UT;
            Vec3 k1v = Gravity(t, r) + thrust, k1r = v;
            Vec3 k2v = Gravity(t + dt / 2, r + k1r * (dt / 2)) + thrust, k2r = v + k1v * (dt / 2);
            Vec3 k3v = Gravity(t + dt / 2, r + k2r * (dt / 2)) + thrust, k3r = v + k2v * (dt / 2);
            Vec3 k4v = Gravity(t + dt, r + k3r * dt) + thrust, k4r = v + k3v * dt;
            Position = r + (k1r + k2r * 2 + k3r * 2 + k4r) * (dt / 6);
            Velocity = v + (k1v + k2v * 2 + k3v * 2 + k4v) * (dt / 6);
            UT = t + dt;
        }

        private void Turn(Vec3 want, double dt)
        {
            want = want.Normalized();
            if (want.LengthSquared == 0) return;
            double angle = Vec3.Angle(Facing, want);
            double max = turnRate * dt;
            if (angle <= max)
            {
                Facing = want;
                return;
            }

            // Rotate toward the wanted direction on the great circle (any perpendicular axis if exactly opposite).
            Vec3 axis = Vec3.Cross(Facing, want);
            if (axis.Length < 1e-9)
            {
                axis = Vec3.Cross(Facing, new Vec3(0, 0, 1));
                if (axis.Length < 1e-9) axis = Vec3.Cross(Facing, new Vec3(1, 0, 0));
            }

            axis = axis.Normalized();
            Vec3 perp = Vec3.Cross(axis, Facing);
            Facing = (Facing * Math.Cos(max) + perp * Math.Sin(max)).Normalized();
        }

        public Vec3 Gravity(double t, Vec3 r)
        {
            double rs = r.Length;
            Vec3 g = r * (-sunMu / (rs * rs * rs));
            g += PlanetPull(target, t, r, always: true);
            if (departure != null && departure != target) g += PlanetPull(departure, t, r, always: false);
            return g;
        }

        private static Vec3 PlanetPull(PlanetRecord p, double t, Vec3 r, bool always)
        {
            Vec3 d = r - p.Orbit.StateAt(t).Position;
            double dl = d.Length;
            if (!always && dl > p.SphereOfInfluence) return Vec3.Zero;
            return d * (-p.GravParameter / (dl * dl * dl));
        }

        public OrbitShape OrbitAround(PlanetRecord body)
        {
            StateVector p = body.Orbit.StateAt(UT);
            return TwoBody.Shape(Position - p.Position, Velocity - p.Velocity, body.GravParameter);
        }
    }

    /// <summary>What happened on one simulated trip.</summary>
    public sealed class FlightResult
    {
        public string Name;
        public bool Finished;
        public double TripTime, DeltaVUsed, MaxAccelErrorOutsideFlip, ClosestToSun, MinAltitudeAtTarget = double.MaxValue;
        public double FinalPeriapsisAltitude, FinalApoapsisAltitude, FinalEccentricity;
        public double PlannedTripTime, PlannedDeltaV;
        public bool PlannerFlaggedSun;
        public int Steps, Legs;
        public double ParkingAltitude, Acceleration;
        public readonly List<string> PhaseLog = new List<string>();
        public string SolverStats = "";
        public readonly List<string> Trace = new List<string>();
        public double TraceFrom = double.MaxValue, TraceTo = double.MaxValue, TraceEvery = 50;
    }

    public static class Flights
    {
        /// <summary>
        /// Fly from a low Kerbin orbit to <paramref name="target"/> and report. Physics warp <paramref name="cruiseWarp"/>
        /// is used only in the cruise: not while leaving Kerbin, within 10 minutes of the flip, or inside the target's
        /// sphere of influence (ADR 0002 decision 2).
        /// </summary>
        public static FlightResult Fly(string name, PlanetRecord target, double accelG, FlipManeuver flip, double startUT,
            double parkingAltitude, double cruiseWarp = 1, double turnRateDegPerSec = 5, double maxTimeFactor = 4,
            double stopAfterSeconds = double.MaxValue, double traceFrom = double.MaxValue, double traceTo = double.MaxValue, double traceEvery = 50)
        {
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            double a = accelG * TripCalculator.OneG;
            double turnRate = turnRateDegPerSec * Math.PI / 180;
            var sim = new ShipSimulator(kerbin, target, turnRate);
            sim.StartInOrbit(kerbin, 100_000, startUT);

            TargetPlanet targetPlanet = target.AsTarget();
            targetPlanet.SphereOfInfluence = target.SphereOfInfluence;
            TargetPlanet departure = kerbin.AsTarget();
            departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            departure.SafeRadius = kerbin.Radius + 80_000;

            var config = new GuidanceConfig
            {
                Target = targetPlanet,
                Departure = departure,
                Acceleration = a,
                Flip = flip,
                ParkingAltitude = parkingAltitude,
                TurnRate = turnRate,
                SunGravParameter = KerbolSystem.SunGravParameter,
            };
            var guidance = new FlipAndBurnGuidance(config);

            TripPlan plan = TripCalculator.Plan(new TripRequest
            {
                ShipPosition = sim.Position, ShipVelocity = sim.Velocity, StartTime = startUT, Target = targetPlanet,
                Acceleration = a, ParkingAltitude = parkingAltitude, SunGravParameter = KerbolSystem.SunGravParameter,
            });

            var result = new FlightResult
            {
                Name = name, PlannedTripTime = plan.TripTime, PlannedDeltaV = plan.TotalDeltaV, PlannerFlaggedSun = plan.PassesNearSun,
                ClosestToSun = double.MaxValue, ParkingAltitude = parkingAltitude, Acceleration = a,
            };

            double nextTrace = traceFrom;
            double limit = Math.Min(startUT + maxTimeFactor * Math.Max(plan.TripTime, 3600), startUT + stopAfterSeconds);
            GuidancePhase lastPhase = (GuidancePhase)(-1);
            int lastLeg = 0;
            bool lastDeparting = false, lastCirc = false;
            while (sim.UT < limit)
            {
                GuidanceCommand cmd = guidance.Update(new GuidanceInput
                {
                    UT = sim.UT, Position = sim.Position, Velocity = sim.Velocity, Facing = sim.Facing,
                });

                if (cmd.Phase != lastPhase || cmd.Leg != lastLeg || cmd.Departing != lastDeparting || cmd.Circularizing != lastCirc)
                {
                    lastDeparting = cmd.Departing;
                    lastCirc = cmd.Circularizing;
                    result.PhaseLog.Add($"{sim.UT - startUT,12:F1} s  leg {cmd.Leg}/{cmd.LegCount}  {cmd.Phase}" + (cmd.Departing ? " (climbing out)" : "") + (cmd.Circularizing ? " (circularizing)" : ""));
                    lastPhase = cmd.Phase;
                    lastLeg = cmd.Leg;
                }

                result.Legs = cmd.LegCount;
                double since = sim.UT - startUT;
                if (since >= nextTrace && since <= traceTo)
                {
                    nextTrace += traceEvery;
                    StateVector tp = target.Orbit.StateAt(sim.UT);
                    Vec3 rr = sim.Position - tp.Position, vr = sim.Velocity - tp.Velocity;
                    double radialSpeed = Vec3.Dot(vr, rr.Normalized());
                    result.Trace.Add($"{since,10:F0} s {cmd.Phase,-13} alt {(rr.Length - target.Radius) / 1000,12:F1} km  v {vr.Length,10:F1} m/s  vrad {radialSpeed,10:F1}  tgo {cmd.TimeToArrival,9:F1}  acc {cmd.Acceleration:F2}  ang(-v) {Vec3.Angle(cmd.Direction, -vr) * 180 / Math.PI,6:F1}  ang(up) {Vec3.Angle(cmd.Direction, rr) * 180 / Math.PI,6:F1}");
                }

                if (cmd.Phase == GuidancePhase.Done)
                {
                    result.Finished = true;
                    break;
                }

                if (cmd.Phase != GuidancePhase.Flip)
                    result.MaxAccelErrorOutsideFlip = Math.Max(result.MaxAccelErrorOutsideFlip, Math.Abs(cmd.Acceleration - a) / a);

                bool cruise = !cmd.Departing &&
                              ((cmd.Phase == GuidancePhase.Accelerate && cmd.TimeToFlip > 600) ||
                               (cmd.Phase == GuidancePhase.Decelerate && cmd.TimeToArrival > 600));
                double dt = ShipSimulator.PhysicsStep * (cruise ? cruiseWarp : 1);

                result.DeltaVUsed += cmd.Acceleration * dt;
                sim.Step(dt, cmd.Direction, cmd.Acceleration);
                result.Steps++;
                result.ClosestToSun = Math.Min(result.ClosestToSun, sim.Position.Length);
                double alt = (sim.Position - target.Orbit.StateAt(sim.UT).Position).Length - target.Radius;
                result.MinAltitudeAtTarget = Math.Min(result.MinAltitudeAtTarget, alt);
            }

            result.SolverStats = guidance.SolverStats;
            result.TripTime = sim.UT - startUT;
            OrbitShape orbit = sim.OrbitAround(target);
            result.FinalEccentricity = orbit.Eccentricity;
            result.FinalPeriapsisAltitude = orbit.PeriapsisRadius - target.Radius;
            result.FinalApoapsisAltitude = orbit.ApoapsisRadius - target.Radius;
            return result;
        }
    }
}
