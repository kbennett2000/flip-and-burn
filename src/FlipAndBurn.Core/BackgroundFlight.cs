using System;
using System.Collections.Generic;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core
{
    /// <summary>A body that pulls on a ship flying in the background (ADR 0006).</summary>
    public sealed class GravitySource
    {
        public string Name;
        public IEphemeris Ephemeris;
        public double GravParameter;
        /// <summary>Its sphere of influence (m): it pulls only inside it, unless <see cref="Always"/>.</summary>
        public double SphereOfInfluence = double.PositiveInfinity;
        /// <summary>Pulls everywhere (the target).</summary>
        public bool Always;
        /// <summary>The lowest safe radius over it (m): the trip must stay above it.</summary>
        public double SafeRadius;
    }

    /// <summary>Why a background trip ended.</summary>
    public enum BackgroundEnd
    {
        /// <summary>Still flying.</summary>
        None,
        /// <summary>In the parking orbit, as planned.</summary>
        Arrived,
        /// <summary>The plan couldn't be followed; the ship made the safest orbit it could (<see cref="BackgroundFlight.Reason"/>).</summary>
        SafeOrbit,
        /// <summary>The propellant (or electric charge) ran out: the ship coasts on the orbit it was on.</summary>
        OutOfFuel,
    }

    /// <summary>
    /// A trip flown while the player is elsewhere (ADR 0006, Kris's decisions 1-3). The same guidance as a watched trip,
    /// flown in a small simulation advanced to the game clock: gravity of the centre (the Sun, or the planet for a moon
    /// trip), the target and the other bodies in their spheres of influence; the ship turning at its own rate and firing only
    /// when it points within 5 degrees (or in a skew flip), as the autopilot does; engine-off stretches on Kepler orbits.
    /// The acceleration is the one the trip had when the player left; it never steps up.
    /// </summary>
    public sealed class BackgroundFlight
    {
        /// <summary>Step while burning in the cruise, far from bodies and events (game s).</summary>
        public const double CruiseStep = 2.0;
        /// <summary>Within this many parking radii of the target, the final approach is flown in physics steps.</summary>
        public const double NearTargetRadii = 2;
        /// <summary>The last part of the final approach flown in physics steps (game s before arrival).</summary>
        public const double FinalStepSeconds = 120;
        /// <summary>Longest step while burning in the cruise, an hour or more from the next event (game s).</summary>
        public const double MaxCruiseStep = 60.0;
        /// <summary>Step while burning near a body or an event (game s).</summary>
        public const double NearStep = 0.1;
        /// <summary>Step for the final approach and the last burn into the orbit: a physics step, as a watched trip.</summary>
        public const double ArrivalStep = 0.02;
        /// <summary>The engine fires only when the ship points within this (degrees), as in a watched trip.</summary>
        public const double PointingTolerance = 5;
        /// <summary>Longest Kepler step while coasting or waiting (game s).</summary>
        public const double MaxRailsStep = 3600;

        private readonly FlipAndBurnGuidance guidance;
        private readonly GuidanceConfig config;
        private readonly List<GravitySource> bodies;
        private readonly double centralMu, exhaustVelocity, dryMass, targetSafeRadius;
        private readonly double chargePerNewton, chargeMade, chargeFloor;
        private readonly double fullThrust, ispThrottleExponent;
        private double noSolutionSince = double.NaN;

        public double UT { get; private set; }
        public Vec3 Position { get; private set; }
        public Vec3 Velocity { get; private set; }
        public Vec3 Facing { get; private set; }
        public double Mass { get; private set; }
        /// <summary>Propellant burned so far (kg), and delta-v (m/s).</summary>
        public double PropellantUsed { get; private set; }
        public double DeltaVUsed { get; private set; }
        /// <summary>Electric charge drawn from the batteries so far (beyond what the ship makes).</summary>
        public double ChargeUsed { get; private set; }
        public double ChargeLeft { get; private set; }
        public GuidanceCommand Last { get; private set; }
        public BackgroundEnd End { get; private set; }
        public string Reason { get; private set; }
        public int Steps { get; private set; }
        public FlipAndBurnGuidance Guidance => guidance;
        public double Acceleration => config.Acceleration;

        /// <param name="config">The trip, as a watched trip would be configured from here (positions relative to the centre).</param>
        /// <param name="bodies">Every body that pulls (the target with <see cref="GravitySource.Always"/>), not the centre.</param>
        /// <param name="centralGravParameter">The centre's, always (also when the centre is the target).</param>
        /// <param name="exhaustVelocity">m/s; infinity when no propellant is used (infinite propellant, or powered electric engines).</param>
        /// <param name="dryMass">kg: the mass when the propellant the engines reach has run out.</param>
        /// <param name="chargePerNewton">Electric engines: electric charge a second per newton of thrust (0 otherwise).</param>
        /// <param name="chargeMade">Electric charge the ship makes a second.</param>
        /// <param name="chargeStored">Electric charge in the batteries now, and the floor never to go below.</param>
        /// <param name="fullThrust">N at 100 %: with <paramref name="ispThrottleExponent"/>, the exhaust velocity at part
        /// throttle is exhaustVelocity * throttle^k (KSP Interstellar fusion drives, ADR 0002); 0 for a fixed exhaust velocity.</param>
        public BackgroundFlight(GuidanceConfig config, IEnumerable<GravitySource> bodies, double centralGravParameter, double ut,
            Vec3 position, Vec3 velocity, Vec3 facing, double mass, double exhaustVelocity, double dryMass, double targetSafeRadius,
            double chargePerNewton = 0, double chargeMade = 0, double chargeStored = 0, double chargeFloor = 0,
            double fullThrust = 0, double ispThrottleExponent = 0)
        {
            this.fullThrust = Math.Max(0, fullThrust);
            this.ispThrottleExponent = Math.Max(0, ispThrottleExponent);
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            guidance = new FlipAndBurnGuidance(config);
            this.bodies = new List<GravitySource>(bodies ?? Array.Empty<GravitySource>());
            centralMu = centralGravParameter;
            UT = ut;
            Position = position;
            Velocity = velocity;
            Facing = facing.LengthSquared > 0 ? facing.Normalized() : velocity.Normalized();
            Mass = mass;
            this.exhaustVelocity = exhaustVelocity > 0 ? exhaustVelocity : double.PositiveInfinity;
            this.dryMass = Math.Max(0, dryMass);
            this.targetSafeRadius = targetSafeRadius;
            this.chargePerNewton = Math.Max(0, chargePerNewton);
            this.chargeMade = Math.Max(0, chargeMade);
            ChargeLeft = chargeStored;
            this.chargeFloor = chargeFloor;
        }

        public bool Finished => End != BackgroundEnd.None;

        /// <summary>
        /// Fly on until <paramref name="ut"/> (or the trip ends), at most <paramref name="maxSteps"/> steps; returns false when
        /// it stopped short of <paramref name="ut"/> (to carry on next frame).
        /// </summary>
        public bool AdvanceTo(double ut, int maxSteps = int.MaxValue)
        {
            int steps = 0;
            while (!Finished && UT < ut - 1e-9)
            {
                if (steps++ >= maxSteps) return false;
                Step(ut);
            }

            return true;
        }

        private void Step(double until)
        {
            var input = new GuidanceInput { UT = UT, Position = Position, Velocity = Velocity, Facing = Facing };
            GuidanceCommand cmd = guidance.Update(input);
            Watch(input, ref cmd);
            Last = cmd;
            Steps++;
            if (cmd.Phase == GuidancePhase.Done)
            {
                End = cmd.SafeOrbit ? BackgroundEnd.SafeOrbit : BackgroundEnd.Arrived;
                if (cmd.SafeOrbit && Reason == null) Reason = guidance.SafeOrbitReason;
                return;
            }

            // Engine off and nothing due for a while: on rails, in big steps closing in on the next burn.
            bool rails = (cmd.Phase == GuidancePhase.Coast || cmd.Phase == GuidancePhase.Waiting) && cmd.Acceleration == 0 &&
                         !cmd.Correcting && cmd.TimeToBurn > 1 && Vec3.Angle(Facing, cmd.Direction) * 180 / Math.PI <= PointingTolerance;
            if (rails)
            {
                double dt = Math.Min(Math.Min(MaxRailsStep, cmd.TimeToBurn / 2), until - UT);
                Turn(cmd.Direction, dt);
                Coast(Math.Max(dt, 1e-3));
                return;
            }

            double step = NearStep;
            bool far = FarFromBodies() && !cmd.Circularizing && !cmd.SafeOrbit && !cmd.Departing &&
                       ((cmd.Phase == GuidancePhase.Accelerate && cmd.TimeToFlip > 600) ||
                        (cmd.Phase == GuidancePhase.Decelerate && cmd.TimeToArrival > 600) ||
                        (cmd.Phase == GuidancePhase.Coast && cmd.Acceleration == 0));
            if (far)
            {
                // In the cruise, bigger steps the further the next event is (up to a minute): the guidance re-aims every step,
                // and steady thrust in an almost uniform field integrates accurately. Keeps the fleet up with high time warp.
                double next = cmd.Phase == GuidancePhase.Accelerate ? cmd.TimeToFlip : cmd.Phase == GuidancePhase.Decelerate ? cmd.TimeToArrival : 0;
                step = Math.Max(CruiseStep, Math.Min(MaxCruiseStep, next / 50));
                if (Vec3.Angle(Facing, cmd.Direction) * 180 / Math.PI > PointingTolerance) step = CruiseStep;   // turning: small steps
            }
            else if (cmd.Circularizing || cmd.SafeOrbit) step = ArrivalStep;
            else if (cmd.Phase == GuidancePhase.FinalApproach)
            {
                // The final approach starts at the edge of the target's sphere, thousands of seconds out: step in proportion
                // to the time left (0.1 to 2 s); in physics steps for the last two minutes, and whenever the ship is within twice
                // the parking radius of the target (a slow approach to Dres dipped 0.8 km below the safe height with coarser steps).
                double distance = (Position - config.Target.Ephemeris.StateAt(UT).Position).Length;
                double parkingRadius = config.Target.Radius + config.ParkingAltitude;
                step = cmd.TimeToArrival > FinalStepSeconds && distance > NearTargetRadii * parkingRadius
                    ? Math.Max(NearStep, Math.Min(2.0, cmd.TimeToArrival / 500))
                    : ArrivalStep;
            }
            step = Math.Max(1e-3, Math.Min(step, until - UT));

            Turn(cmd.Direction, step);
            bool flipping = cmd.Phase == GuidancePhase.Flip;
            bool pointed = flipping || Vec3.Angle(Facing, cmd.Direction) * 180 / Math.PI <= PointingTolerance;
            double a = pointed ? cmd.Acceleration : 0;
            if (a > 0 && !Burn(a, step)) a = 0;
            Integrate(Facing * a, step);
        }

        // Take out what burning at acceleration a for dt uses; false (and the trip ends) when it has run out.
        private bool Burn(double a, double dt)
        {
            double thrust = Mass * a;
            if (chargePerNewton > 0)
            {
                double draw = Math.Max(0, chargePerNewton * thrust - chargeMade) * dt;
                if (draw > 0 && ChargeLeft - draw < chargeFloor)
                {
                    End = BackgroundEnd.OutOfFuel;
                    Reason = "the batteries reached the last 10%, and the ship doesn't make enough electric charge of its own to keep the engines going";
                    return false;
                }

                ChargeLeft -= draw;
                ChargeUsed += draw;
            }

            if (!double.IsPositiveInfinity(exhaustVelocity))
            {
                double ve = exhaustVelocity;
                if (fullThrust > 0 && ispThrottleExponent > 0)
                    ve *= Math.Pow(Math.Min(1, Math.Max(1e-6, thrust / fullThrust)), ispThrottleExponent);
                double dm = thrust / ve * dt;
                if (Mass - dm < dryMass)
                {
                    End = BackgroundEnd.OutOfFuel;
                    Reason = "the propellant ran out";
                    return false;
                }

                Mass -= dm;
                PropellantUsed += dm;
            }

            DeltaVUsed += a * dt;
            return true;
        }

        // The arrival watch, as the autopilot runs it; and a plan lost for 10 s makes the orbit safe.
        private void Watch(GuidanceInput input, ref GuidanceCommand cmd)
        {
            if (cmd.SafeOrbit || cmd.Phase == GuidancePhase.Done) return;
            if (cmd.NoSolution)
            {
                if (double.IsNaN(noSolutionSince)) noSolutionSince = UT;
                if (UT - noSolutionSince > 10) SafeOrbit(input, ref cmd, "the ship drifted off course and no way to the planned orbit was found");
                return;
            }

            noSolutionSince = double.NaN;
            if (cmd.Circularizing || !ArrivalSafety.Watching(config.Target.IsCentralBody, cmd.Phase)) return;
            StateVector t = config.Target.Ephemeris.StateAt(UT);
            Vec3 r = Position - t.Position, v = Velocity - t.Velocity;
            double soi = config.Target.IsCentralBody ? double.PositiveInfinity : config.Target.SphereOfInfluence;
            if (!(r.Length < soi)) return;
            ArrivalIssue issue = ArrivalSafety.Check(r, v, config.Target.GravParameter, targetSafeRadius,
                config.Target.Radius + config.ParkingAltitude, soi, config.Acceleration, config.Acceleration);
            if (issue == ArrivalIssue.Escaping) SafeOrbit(input, ref cmd, $"it would have flown past {config.Target.Name}");
        }

        private void SafeOrbit(GuidanceInput input, ref GuidanceCommand cmd, string why)
        {
            Reason = why;
            guidance.EnterSafeOrbit(input, why, config.Acceleration);
            cmd = guidance.Update(input);
        }

        private bool FarFromBodies()
        {
            foreach (GravitySource b in bodies)
            {
                double near = double.IsPositiveInfinity(b.SphereOfInfluence) ? 10 * Math.Max(b.SafeRadius, 1) : b.SphereOfInfluence;
                if ((Position - b.Ephemeris.StateAt(UT).Position).Length < near) return false;
            }

            return true;
        }

        private Vec3 Gravity(double t, Vec3 r)
        {
            double rs = r.Length;
            Vec3 g = centralMu > 0 && rs > 0 ? r * (-centralMu / (rs * rs * rs)) : Vec3.Zero;
            foreach (GravitySource b in bodies)
            {
                Vec3 d = r - b.Ephemeris.StateAt(t).Position;
                double dl = d.Length;
                if (dl > 0 && (b.Always || dl < b.SphereOfInfluence)) g += d * (-b.GravParameter / (dl * dl * dl));
            }

            return g;
        }

        private void Integrate(Vec3 thrust, double dt)
        {
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

        // Engine off: a Kepler step around the body whose sphere the ship is in (or the centre), with the others' pull as
        // half-step kicks either side.
        private void Coast(double dt)
        {
            GravitySource frame = null;
            foreach (GravitySource b in bodies)
                if (!double.IsPositiveInfinity(b.SphereOfInfluence) && (Position - b.Ephemeris.StateAt(UT).Position).Length < b.SphereOfInfluence)
                    frame = b;
            Kick(dt / 2, frame);
            if (frame == null)
            {
                if (centralMu > 0)
                {
                    StateVector next = TwoBody.Propagate(Position, Velocity, centralMu, dt);
                    Position = next.Position;
                    Velocity = next.Velocity;
                }
                else Position += Velocity * dt;
            }
            else
            {
                StateVector p0 = frame.Ephemeris.StateAt(UT), p1 = frame.Ephemeris.StateAt(UT + dt);
                StateVector rel = TwoBody.Propagate(Position - p0.Position, Velocity - p0.Velocity, frame.GravParameter, dt);
                Position = p1.Position + rel.Position;
                Velocity = p1.Velocity + rel.Velocity;
            }

            UT += dt;
            Kick(dt / 2, frame);
        }

        private void Kick(double dt, GravitySource frame)
        {
            Vec3 g = Gravity(UT, Position);
            if (frame == null)
            {
                double l = Position.Length;
                if (centralMu > 0 && l > 0) g -= Position * (-centralMu / (l * l * l));
            }
            else
            {
                StateVector p = frame.Ephemeris.StateAt(UT);
                Vec3 d = Position - p.Position;
                double dl = d.Length, pl = p.Position.Length;
                g -= d * (-frame.GravParameter / (dl * dl * dl));
                if (centralMu > 0 && pl > 0) g -= p.Position * (-centralMu / (pl * pl * pl));
            }

            Velocity += g * dt;
        }

        private void Turn(Vec3 want, double dt)
        {
            if (want.LengthSquared == 0) return;
            want = want.Normalized();
            double angle = Vec3.Angle(Facing, want);
            double max = config.TurnRate * dt;
            if (angle <= max)
            {
                Facing = want;
                return;
            }

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
    }
}
