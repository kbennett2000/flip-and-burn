using System;
using System.Collections.Generic;

namespace FlipAndBurn.Core.Guidance
{
    public enum GuidancePhase
    {
        Accelerate,
        // A coast phase (burn up, coast, burn down) would slot in here: TwoArcSolver already takes a coast time
        // between its arcs, so adding it means choosing that time and adding a state, not new math.
        Flip,
        Decelerate,
        FinalApproach,
        Done,
    }

    public enum FlipManeuver
    {
        /// <summary>Thrust to zero, turn, relight once pointing within a few degrees. The default (ADR 0002).</summary>
        CutFlipRelight,
        /// <summary>Keep thrusting while turning; the steering after the flip corrects the sideways push.</summary>
        SkewFlip,
    }

    /// <summary>Everything that stays fixed for one trip.</summary>
    public sealed class GuidanceConfig
    {
        public TargetPlanet Target { get; set; }
        /// <summary>The planet the ship is leaving, if it starts inside one's sphere of influence. Optional.</summary>
        public TargetPlanet Departure { get; set; }
        public double Acceleration { get; set; } = TripCalculator.OneG;
        public FlipManeuver Flip { get; set; } = FlipManeuver.CutFlipRelight;
        public double ParkingAltitude { get; set; }
        /// <summary>How fast the ship can turn, rad/s. In the game this will come from MechJeb.</summary>
        public double TurnRate { get; set; } = 5 * Math.PI / 180;
        public double SunGravParameter { get; set; }
        public double SunSafetyDistance { get; set; } = TripCalculator.DefaultSunSafetyDistance;
        /// <summary>The target's rotation axis (its north). The parking orbit is equatorial and prograde about it.</summary>
        public Vec3 EquatorNormal { get; set; } = new Vec3(0, 0, 1);
        /// <summary>Cut, flip, relight: relight once the ship points within this many degrees of the new direction.</summary>
        public double RelightToleranceDeg { get; set; } = 5;
        /// <summary>Stop steering when this little time is left (s); the remaining error is below what one step fixes.</summary>
        public double ArrivalCutoff { get; set; } = 1.0;
        /// <summary>A detour waypoint keeps both legs at least this factor times the Sun safety distance from the Sun.</summary>
        public double DetourMargin { get; set; } = 1.2;
        /// <summary>
        /// The braking half is planned at this fraction of the chosen acceleration, and may use anything from
        /// <see cref="MinThrustFraction"/> to 100 %. That band is the only way to correct being a little ahead or
        /// behind while braking (ADR 0003).
        /// </summary>
        public double BrakingFraction { get; set; } = 0.99;
        public double MinThrustFraction { get; set; } = 0.982;
    }

    /// <summary>The ship as it is right now. Positions and velocities are relative to the Sun.</summary>
    public struct GuidanceInput
    {
        public double UT;
        public Vec3 Position;
        public Vec3 Velocity;
        /// <summary>Unit vector the ship (its engines) points along.</summary>
        public Vec3 Facing;
    }

    public sealed class GuidanceCommand
    {
        /// <summary>Unit vector to point the ship along.</summary>
        public Vec3 Direction;
        /// <summary>Thrust acceleration to hold, m/s^2 (0 up to the chosen value).</summary>
        public double Acceleration;
        public GuidancePhase Phase;
        /// <summary>Seconds until the flip starts (0 once it has).</summary>
        public double TimeToFlip;
        /// <summary>Seconds until the end of the current leg (the arrival, unless detouring around the Sun).</summary>
        public double TimeToArrival;
        /// <summary>1-based leg number and leg count; 2 legs when detouring around the Sun.</summary>
        public int Leg = 1, LegCount = 1;
        /// <summary>True while burning prograde to leave the departure planet.</summary>
        public bool Departing;
        /// <summary>True during the last burn into the circular parking orbit.</summary>
        public bool Circularizing;
    }

    /// <summary>
    /// Flip and Burn steering, called every physics step (ADR 0003). Re-plans from the ship's actual state each time.
    /// </summary>
    public sealed class FlipAndBurnGuidance
    {
        private sealed class Leg
        {
            public bool IsWaypoint;
            public Vec3 Waypoint;
        }

        private readonly GuidanceConfig config;
        private readonly List<Leg> legs = new List<Leg>();
        private int legIndex;
        private GuidancePhase phase = GuidancePhase.Accelerate;
        private TwoArcSolver solver;
        private ArrivalLaw law;
        private double flipTime;      // seconds the last plan expects the flip to take
        // What the target's gravity will add over the braking half (velocity, position), from the last plan. The
        // gravity-free two-arc solve aims short of the arrival state by this much, so the braking half is planned
        // against the real gravity well rather than discovering it on the final approach.
        private Vec3 wellVelocity, wellPosition;
        private bool circularizing;
        private double circularizeSign;   // +1 braking along the orbit, -1 speeding up
        private double lastUT = double.NaN;
        private Vec3 currentPosition;
        private bool initialized;
        private bool climbedOut;

        public FlipAndBurnGuidance(GuidanceConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            if (config.Target == null) throw new ArgumentException("no target", nameof(config));
            if (!(config.Acceleration > 0)) throw new ArgumentException("acceleration must be positive", nameof(config));
        }

        public GuidancePhase Phase => phase;

        /// <summary>Diagnostics for tests: solver counters as text.</summary>
        public string SolverStats => $"two-arc warm {solver?.WarmSolves} robust {solver?.RobustSolves}; arrival nudged {law?.Nudged} carried {law?.Carried}";

        /// <summary>The Sun-detour waypoint, if this trip uses one.</summary>
        public Vec3? DetourWaypoint => legs.Count > 1 ? legs[0].Waypoint : (Vec3?)null;

        private double ParkingRadius => config.Target.Radius + config.ParkingAltitude;

        public GuidanceCommand Update(GuidanceInput input)
        {
            if (circularizing) return Circularize(input);

            if (!initialized) Initialize(input);
            currentPosition = input.Position;
            double step = double.IsNaN(lastUT) ? 0 : input.UT - lastUT;
            lastUT = input.UT;

            var cmd = new GuidanceCommand { Phase = phase, Leg = legIndex + 1, LegCount = legs.Count, Direction = input.Facing };
            if (phase == GuidancePhase.Done) return cmd;

            Leg leg = legs[legIndex];

            if (phase == GuidancePhase.Accelerate)
            {
                // The flip is planned as a coast lasting as long as the turn takes (from the last solution; a full
                // half-turn to begin with), so turning doesn't leave the ship short of speed.
                TwoArcSolver.Arcs arcs = solver.Solve(input.UT, input.Position, input.Velocity, flipTime);
                if (!arcs.Valid)
                {
                    // No solution this step (should not happen in practice): hold course, keep thrusting.
                    cmd.Acceleration = config.Acceleration;
                    return cmd;
                }

                flipTime = Vec3.Angle(arcs.A1, arcs.A2) / config.TurnRate;
                if (!leg.IsWaypoint) UpdateGravityWell(input, arcs);
                if (arcs.T1 > Math.Max(step, 1e-3))
                {
                    cmd.Direction = arcs.A1.Normalized();
                    cmd.Acceleration = config.Acceleration;
                    cmd.TimeToFlip = arcs.T1;
                    cmd.TimeToArrival = arcs.Total;
                    if (legIndex == 0 && NeedsToClimbOut(input, cmd.Direction, out Vec3 prograde))
                    {
                        cmd.Direction = prograde;
                        cmd.Departing = true;
                    }

                    return cmd;
                }

                law = MakeArrivalLaw(leg, input.UT + arcs.Total);
                law.Start(input.UT, arcs.Tc + arcs.T2);
                phase = GuidancePhase.Flip;
            }

            Vec3 thrust = law.Solve(input.UT, input.Position, input.Velocity, out double timeToGo);
            Vec3 want = thrust.Normalized();
            cmd.TimeToArrival = timeToGo;

            if (phase == GuidancePhase.Flip)
            {
                double off = Vec3.Angle(input.Facing, want) * 180 / Math.PI;
                if (off > config.RelightToleranceDeg)
                {
                    cmd.Phase = GuidancePhase.Flip;
                    cmd.Direction = want;
                    cmd.Acceleration = config.Flip == FlipManeuver.SkewFlip ? config.Acceleration : 0;
                    return cmd;
                }

                phase = GuidancePhase.Decelerate;
            }

            if (timeToGo <= config.ArrivalCutoff)
            {
                if (leg.IsWaypoint && legIndex + 1 < legs.Count)
                {
                    // Reached the detour waypoint: start the next leg from here.
                    legIndex++;
                    phase = GuidancePhase.Accelerate;
                    solver = MakeSolver(legs[legIndex]);
                    return Update(input);
                }

                // Arrived at the parking orbit's radius, moving along it. Finish with a short burn to exactly circular speed.
                phase = GuidancePhase.FinalApproach;
                circularizing = true;
                circularizeSign = 0;
                return Circularize(input);
            }

            bool nearTarget = !leg.IsWaypoint && config.Target.SphereOfInfluence > 0 &&
                              (input.Position - config.Target.Ephemeris.StateAt(input.UT).Position).Length < config.Target.SphereOfInfluence;
            phase = nearTarget ? GuidancePhase.FinalApproach : GuidancePhase.Decelerate;
            cmd.Phase = phase;
            cmd.Direction = want;
            cmd.Acceleration = thrust.Length;
            return cmd;
        }

        // ------------------------------------------------------------------ circularizing

        /// <summary>
        /// The last burn: along the orbit (against the motion if too fast, with it if too slow) at the chosen
        /// acceleration, with a radial part that holds the parking radius, until the speed is circular. Needed because a
        /// strong gravity well (Jool at 220 km pulls 7.3 m/s^2) pulls sideways on the final approach, and fighting that
        /// at a fixed acceleration leaves some speed to lose at the end.
        /// </summary>
        private GuidanceCommand Circularize(GuidanceInput input)
        {
            var cmd = new GuidanceCommand { Phase = GuidancePhase.FinalApproach, Leg = legIndex + 1, LegCount = legs.Count, Circularizing = true };
            double a = config.Acceleration;
            double mu = config.Target.GravParameter;
            StateVector planet = config.Target.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double rl = r.Length;
            Vec3 up = r / rl;
            double vr = Vec3.Dot(v, up);
            Vec3 vtVec = v - up * vr;
            double vt = vtVec.Length;
            Vec3 along = vtVec / vt;
            double vc = Math.Sqrt(mu / rl);
            double excess = vt - vc;

            if (circularizeSign == 0) circularizeSign = Math.Sign(excess);
            // Done once the speed reaches circular (it has crossed over), or there is nothing left to do.
            if (circularizeSign * excess <= 0 || Math.Abs(excess) < 0.05)
            {
                circularizing = false;
                phase = GuidancePhase.Done;
                return new GuidanceCommand { Phase = phase, Leg = legIndex + 1, LegCount = legs.Count, Direction = input.Facing };
            }

            // Radial: hold the parking radius (critically damped, about 30 s), cancelling gravity minus the centrifugal
            // effect of the current speed.
            const double omega = 0.1;
            double radial = -omega * omega * (rl - ParkingRadius) - 2 * omega * vr + mu / (rl * rl) - vt * vt / rl;
            radial = Math.Max(-0.8 * a, Math.Min(0.8 * a, radial));
            double tangential = -circularizeSign * Math.Sqrt(a * a - radial * radial);

            cmd.Direction = (up * radial + along * tangential).Normalized();
            cmd.Acceleration = a;
            cmd.TimeToArrival = Math.Abs(excess) / a;
            return cmd;
        }

        // ------------------------------------------------------------------ set-up

        private void Initialize(GuidanceInput input)
        {
            initialized = true;
            legs.Clear();
            Vec3? waypoint = PlanDetour(input);
            if (waypoint.HasValue) legs.Add(new Leg { IsWaypoint = true, Waypoint = waypoint.Value });
            legs.Add(new Leg());
            legIndex = 0;
            solver = MakeSolver(legs[0]);
        }

        private TwoArcSolver MakeSolver(Leg leg)
        {
            flipTime = Math.PI / config.TurnRate;
            double braking = config.Acceleration * config.BrakingFraction;
            if (leg.IsWaypoint)
                return new TwoArcSolver(_ => new StateVector(leg.Waypoint, Vec3.Zero), config.Acceleration, braking);
            return new TwoArcSolver(TargetEndState, config.Acceleration, braking);
        }

        // The parking orbit point the trip ends at, for a given arrival time (ADR 0003): on the planet's equator, at the
        // point where the prograde orbit runs in the ship's direction of travel. The braking then ends with the ship
        // already moving along the orbit at orbital speed, instead of falling in and having to turn sideways at the end.
        private StateVector TargetEndState(double arrivalUT)
        {
            StateVector planet = config.Target.Ephemeris.StateAt(arrivalUT);
            Vec3 side = ArrivalSide(planet.Position - currentPosition);
            return new StateVector(planet.Position + side * ParkingRadius - wellPosition, planet.Velocity + OrbitVelocity(side) - wellVelocity);
        }

        // Estimate the target's gravity over the braking half of the current plan: from the state after the flip to the
        // arrival state, along the expected path, relative to the planet.
        private void UpdateGravityWell(GuidanceInput input, TwoArcSolver.Arcs arcs)
        {
            double tFlipEnd = arcs.T1 + arcs.Tc;
            Vec3 rFlip = input.Position + input.Velocity * tFlipEnd + arcs.A1 * (0.5 * arcs.T1 * arcs.T1 + arcs.T1 * arcs.Tc);
            Vec3 vFlip = input.Velocity + arcs.A1 * arcs.T1;
            StateVector planetAtFlip = config.Target.Ephemeris.StateAt(input.UT + tFlipEnd);
            StateVector planetAtArrival = config.Target.Ephemeris.StateAt(input.UT + arcs.Total);
            Vec3 side = ArrivalSide(planetAtArrival.Position - input.Position);
            ArrivalLaw.GravityIntegrals(config.Target.GravParameter,
                rFlip - planetAtFlip.Position, vFlip - planetAtFlip.Velocity,
                side * ParkingRadius, OrbitVelocity(side), arcs.T2, out wellVelocity, out wellPosition);
        }

        /// <summary>
        /// Arrival point direction (from the planet) for a ship travelling along <paramref name="towardPlanet"/>: the
        /// travel direction flattened onto the equator, crossed with north. There the prograde orbit's velocity,
        /// north x side, equals the travel direction.
        /// </summary>
        private Vec3 ArrivalSide(Vec3 towardPlanet)
        {
            Vec3 n = config.EquatorNormal.Normalized();
            Vec3 travel = towardPlanet - n * Vec3.Dot(towardPlanet, n);
            if (travel.Length < 1e-9 * Math.Max(1, towardPlanet.Length))
                travel = Vec3.Cross(n, new Vec3(1, 0, 0)).Length > 0.1 ? Vec3.Cross(n, new Vec3(1, 0, 0)) : Vec3.Cross(n, new Vec3(0, 1, 0));
            return Vec3.Cross(travel.Normalized(), n).Normalized();
        }

        private Vec3 OrbitVelocity(Vec3 side)
        {
            double speed = Math.Sqrt(config.Target.GravParameter / ParkingRadius);
            return Vec3.Cross(config.EquatorNormal.Normalized(), side).Normalized() * speed;
        }

        private ArrivalLaw MakeArrivalLaw(Leg leg, double arrivalUT)
        {
            double a = config.Acceleration, nominal = a * config.BrakingFraction, min = a * config.MinThrustFraction;
            if (leg.IsWaypoint)
                return new ArrivalLaw(new FixedEphemeris(leg.Waypoint), 0, Vec3.Zero, Vec3.Zero, nominal, min, a, config.SunGravParameter, frameFeelsSun: false);

            // Fix the arrival point now, so the target doesn't slide around as the ship closes in.
            Vec3 side = ArrivalSide(config.Target.Ephemeris.StateAt(arrivalUT).Position - currentPosition);
            return new ArrivalLaw(config.Target.Ephemeris, config.Target.GravParameter, side * ParkingRadius, OrbitVelocity(side), nominal, min, a,
                config.SunGravParameter);
        }

        // ------------------------------------------------------------------ leaving the departure planet

        /// <summary>
        /// Near the departure planet, thrusting straight at the target can point into the planet (from its far side) or
        /// fail to climb out. Burn prograde until the ship is escaping and the line toward the target clears the planet.
        /// </summary>
        private bool NeedsToClimbOut(GuidanceInput input, Vec3 direction, out Vec3 prograde)
        {
            prograde = default;
            TargetPlanet dep = config.Departure;
            if (dep == null || climbedOut) return false;
            StateVector planet = dep.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double soi = dep.SphereOfInfluence > 0 ? dep.SphereOfInfluence : 100 * dep.Radius;
            if (r.Length > soi)
            {
                climbedOut = true;
                return false;
            }

            bool escaping = v.LengthSquared / 2 - dep.GravParameter / r.Length >= 0;
            double safe = dep.SafeRadius > 0 ? dep.SafeRadius : dep.Radius;
            double along = -Vec3.Dot(r, direction);
            bool lineClear = along <= 0 || (r + direction * along).Length > safe;
            if (escaping && lineClear)
            {
                climbedOut = true; // once out, stay out: no switching back and forth
                return false;
            }

            prograde = v.Normalized();
            return true;
        }

        // ------------------------------------------------------------------ the Sun in the way

        /// <summary>
        /// If the straight trip passes within the Sun safety distance, pick a waypoint beside the Sun so that both legs
        /// (start to waypoint, waypoint to target) stay outside it with margin. The ship stops at the waypoint and then
        /// flies a normal trip from there.
        /// </summary>
        private Vec3? PlanDetour(GuidanceInput input)
        {
            if (!(config.SunSafetyDistance > 0)) return null;
            TripPlan straight = TripCalculator.Plan(new TripRequest
            {
                ShipPosition = input.Position,
                ShipVelocity = input.Velocity,
                StartTime = input.UT,
                Target = config.Target,
                Acceleration = config.Acceleration,
                ParkingAltitude = config.ParkingAltitude,
                SunGravParameter = config.SunGravParameter,
                SunSafetyDistance = config.SunSafetyDistance,
            });
            if (!straight.Feasible || !straight.PassesNearSun) return null;

            double clear = config.SunSafetyDistance * config.DetourMargin;
            Vec3 start = input.Position;
            double arrival = input.UT + straight.TripTime;
            Vec3 waypoint = default;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                Vec3 end = config.Target.Ephemeris.StateAt(arrival).Position;
                waypoint = Waypoint(start, end, clear);

                // Re-estimate the arrival time with the detour, then place the waypoint again.
                var dummy = new TargetPlanet("waypoint", new FixedEphemeris(waypoint), 1, 1);
                TripPlan leg1 = TripCalculator.Plan(new TripRequest
                {
                    ShipPosition = input.Position, ShipVelocity = input.Velocity, StartTime = input.UT, Target = dummy,
                    Acceleration = config.Acceleration, SunSafetyDistance = 0,
                });
                if (!leg1.Feasible) break;
                TripPlan leg2 = TripCalculator.Plan(new TripRequest
                {
                    ShipPosition = waypoint, ShipVelocity = Vec3.Zero, StartTime = input.UT + leg1.TripTime, Target = config.Target,
                    Acceleration = config.Acceleration, ParkingAltitude = config.ParkingAltitude, SunSafetyDistance = 0,
                });
                if (!leg2.Feasible) break;
                arrival = input.UT + leg1.TripTime + leg2.TripTime;
            }

            return waypoint;
        }

        /// <summary>A point off to the side of the Sun such that both straight legs stay at least <paramref name="clear"/> from it.</summary>
        private Vec3 Waypoint(Vec3 start, Vec3 end, double clear)
        {
            Vec3 line = (end - start).Normalized();
            // The side to pass on: away from the Sun, perpendicular to the line, in the plane of start, end and Sun.
            Vec3 closest = start + line * (-Vec3.Dot(start, line));
            Vec3 side = closest.Length > 1e-6 * start.Length
                ? closest.Normalized()
                : Vec3.Cross(config.EquatorNormal.Normalized(), line).Normalized();

            for (double distance = clear; ; distance *= 1.02)
            {
                Vec3 w = side * distance;
                if (SegmentDistance(start, w) >= clear && SegmentDistance(w, end) >= clear) return w;
                if (distance > 100 * clear) return w;
            }
        }

        private static double SegmentDistance(Vec3 a, Vec3 b)
        {
            Vec3 d = b - a;
            double t = Math.Max(0, Math.Min(1, -Vec3.Dot(a, d) / d.LengthSquared));
            return (a + d * t).Length;
        }
    }
}
