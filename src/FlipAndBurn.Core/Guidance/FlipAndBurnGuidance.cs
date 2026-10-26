using System;
using System.Collections.Generic;

namespace FlipAndBurn.Core.Guidance
{
    public enum GuidancePhase
    {
        Accelerate,
        /// <summary>Coast trips only: engine off between the burns. The ship turns to its braking direction here.</summary>
        Coast,
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

    public enum TripType
    {
        /// <summary>Burn the whole way: accelerate, flip, decelerate. The default (ADR 0002).</summary>
        FullBurn,
        /// <summary>Burn, coast weightless, burn to stop: the fastest trip a delta-v budget allows (ADR 0002 addendum).</summary>
        CoastInTheMiddle,
    }

    /// <summary>Everything that stays fixed for one trip.</summary>
    public sealed class GuidanceConfig
    {
        public TargetPlanet Target { get; set; }
        /// <summary>The planet the ship is leaving, if it starts inside one's sphere of influence. Optional.</summary>
        public TargetPlanet Departure { get; set; }
        public double Acceleration { get; set; } = TripCalculator.OneG;
        public TripType Trip { get; set; } = TripType.FullBurn;
        /// <summary>Coast trips: delta-v the burns may use (m/s), i.e. the ship's delta-v minus the reserve.</summary>
        public double DeltaVBudget { get; set; }
        /// <summary>Full-burn trips only; on a coast trip the flip happens with the engine off.</summary>
        public FlipManeuver Flip { get; set; } = FlipManeuver.CutFlipRelight;
        public double ParkingAltitude { get; set; }
        /// <summary>How fast the ship can turn, rad/s. In the game this will come from MechJeb.</summary>
        public double TurnRate { get; set; } = 5 * Math.PI / 180;
        public double SunGravParameter { get; set; }
        public double SunSafetyDistance { get; set; } = TripCalculator.DefaultSunSafetyDistance;
        /// <summary>The target's rotation axis (its north). The parking orbit is equatorial and prograde about it.</summary>
        public Vec3 EquatorNormal { get; set; } = ParkingArrival.DefaultNorth;
        /// <summary>Cut, flip, relight: relight once the ship points within this many degrees of the new direction.</summary>
        public double RelightToleranceDeg { get; set; } = 5;
        /// <summary>Stop steering when this little time is left (s); the final circularising burn takes over.</summary>
        public double ArrivalCutoff { get; set; } = 1.0;
        /// <summary>A detour waypoint keeps both legs at least this factor times the Sun safety distance from the Sun.</summary>
        public double DetourMargin { get; set; } = SunDetour.DefaultMargin;
        /// <summary>
        /// The braking half is planned at this fraction of the chosen acceleration, and may use anything from
        /// <see cref="MinThrustFraction"/> to 100 %. That band is the only way to correct being a little ahead or
        /// behind while braking (ADR 0003).
        /// </summary>
        public double BrakingFraction { get; set; } = CoastPlanner.BrakingFraction;

        /// <summary>Coast trips: a velocity error (m/s) above which the ship makes a course correction burn.</summary>
        public double CorrectionThreshold { get; set; } = 1.0;
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
        /// <summary>Seconds until the flip starts (0 once it has). On a coast trip, until the coast starts.</summary>
        public double TimeToFlip;
        /// <summary>Coast trips, while coasting: seconds until the braking burn is expected to start.</summary>
        public double TimeToBurn;
        /// <summary>Seconds until the end of the current leg (the arrival, unless detouring around the Sun).</summary>
        public double TimeToArrival;
        /// <summary>1-based leg number and leg count; 2 legs when detouring around the Sun.</summary>
        public int Leg = 1, LegCount = 1;
        /// <summary>True while burning prograde to leave the departure planet.</summary>
        public bool Departing;
        /// <summary>True during the last burn into the circular parking orbit.</summary>
        public bool Circularizing;

        /// <summary>Coast trips: a course correction burn (turning for it, or burning).</summary>
        public bool Correcting;

        /// <summary>Coast trips, while coasting: how far the ship's velocity is from the one that arrives on time (m/s).</summary>
        public double CourseError;
    }

    /// <summary>
    /// Flip and Burn steering, called every physics step (ADR 0003). Re-plans from the ship's actual state each time.
    /// </summary>
    public sealed class FlipAndBurnGuidance
    {
        /// <summary>Coast trips: no course corrections in the last this-many seconds before braking (the braking takes care of it).</summary>
        public const double CorrectionCutoff = 600;

        private sealed class Leg
        {
            public bool IsWaypoint;
            public Vec3 Waypoint;
        }

        private readonly GuidanceConfig config;
        private readonly PathGravity targetGravity;
        private readonly List<Leg> legs = new List<Leg>();
        private int legIndex;
        private GuidancePhase phase = GuidancePhase.Accelerate;
        private TwoArcSolver solver;
        private ArrivalLaw law;
        private double flipTime;              // seconds the last full-burn plan expects the flip to take
        private double coastArrivalUT = double.NaN;  // coast trips: the fixed arrival time
        private double plannedBrakingTime;    // coast trips: the braking burn's length, for TimeToBurn
        // What gravity (the target's well and the Sun) will add over the rest of the trip, from the last plan. The
        // gravity-free two-arc solve aims off by this much, so the plan already allows for it.
        private Vec3 wellVelocity, wellPosition;
        private StateVector coastEnd;         // coast trips: the arrival point (relative to the target), fixed at the coast's start
        private bool correcting;              // coast trips: in a course correction burn
        private bool correctionBurning;       // ... and the engine was on last step
        private Vec3 correctionDirection;
        private bool circularizing;
        private double circularizeSign;       // +1 braking along the orbit, -1 speeding up
        private double lastUT = double.NaN;
        private Vec3 currentPosition;
        private bool initialized;
        private bool climbedOut;

        public FlipAndBurnGuidance(GuidanceConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            if (config.Target == null) throw new ArgumentException("no target", nameof(config));
            if (!(config.Acceleration > 0)) throw new ArgumentException("acceleration must be positive", nameof(config));
            targetGravity = new PathGravity(config.Target.Ephemeris, config.Target.GravParameter, config.SunGravParameter, true);
        }

        public GuidancePhase Phase => phase;

        /// <summary>The Sun-detour waypoint, if this trip uses one.</summary>
        public Vec3? DetourWaypoint => legs.Count > 1 ? legs[0].Waypoint : (Vec3?)null;

        /// <summary>Coast trips: the arrival time being flown to (NaN for a full burn).</summary>
        public double CoastArrivalUT => coastArrivalUT;

        /// <summary>Diagnostics for tests: solver counters as text.</summary>
        public string SolverStats => $"two-arc warm {solver?.WarmSolves} robust {solver?.RobustSolves}; arrival nudged {law?.Nudged} carried {law?.Carried}";

        private double ParkingRadius => config.Target.Radius + config.ParkingAltitude;
        private bool IsCoastTrip => !double.IsNaN(coastArrivalUT);

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
                TwoArcSolver.Arcs arcs = IsCoastTrip
                    ? solver.SolveFixedTotal(input.UT, input.Position, input.Velocity, coastArrivalUT - input.UT)
                    : solver.Solve(input.UT, input.Position, input.Velocity, flipTime);
                if (!arcs.Valid)
                {
                    // No solution this step (should not happen in practice): hold course, keep thrusting.
                    cmd.Acceleration = config.Acceleration;
                    return cmd;
                }

                // A full burn plans the flip as a coast lasting as long as the turn takes (from the last solution; a
                // half-turn to begin with), so turning doesn't leave the ship short of speed.
                if (!IsCoastTrip) flipTime = Vec3.Angle(arcs.A1, arcs.A2) / config.TurnRate;
                if (!leg.IsWaypoint) UpdateGravityAllowance(input, arcs);

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

                if (IsCoastTrip && !leg.IsWaypoint)
                {
                    // Fix the arrival point now, so it doesn't slide around during the coast.
                    coastEnd = ParkingArrival.Relative(config.Target, coastArrivalUT, currentPosition, ParkingRadius, config.EquatorNormal);
                    plannedBrakingTime = arcs.T2;
                    phase = GuidancePhase.Coast;
                }
                else
                {
                    law = MakeArrivalLaw(leg, input.UT + arcs.Total);
                    law.Start(input.UT, arcs.Tc + arcs.T2);
                    phase = GuidancePhase.Flip;
                }
            }

            if (phase == GuidancePhase.Coast)
            {
                if (Coast(input, step, cmd)) return cmd;

                // Time to brake: the arrival law takes over, with the same arrival point and time.
                double a = config.Acceleration;
                law = new ArrivalLaw(config.Target.Ephemeris, targetGravity, coastEnd.Position, coastEnd.Velocity,
                    a * config.BrakingFraction, a * config.MinThrustFraction, a);
                law.Start(input.UT, coastArrivalUT - input.UT);
                phase = GuidancePhase.Decelerate;
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

        // ------------------------------------------------------------------ coasting

        /// <summary>
        /// Coast trips, between the burns: engine off, pointing where the braking will go (the flip happens here, so
        /// its style doesn't matter). Returns false when it is time to start braking.
        /// </summary>
        /// <remarks>
        /// Each step works out the velocity the ship should have now so that coasting, then braking at the planned
        /// acceleration, ends at the arrival point on time. With gravity's contribution over the rest of the trip as
        /// G1 (velocity) and G2 (displacement), coasting for τ - tb then braking for tb at a constant A gives
        ///   r + v' τ + A tb^2 / 2 + G2 = rF  and  v' + A tb + G1 = vF,
        /// so A (τ - tb/2) = (vF - G1) τ - (rF - r - G2), a known vector W. With |A| the braking acceleration a2,
        /// tb = τ - sqrt(τ^2 - 2 |W| / a2), and v' = vF - G1 - A tb. (No real root: braking is already due.)
        /// Over weeks small differences grow (the Sun pulls a little differently on the ship than predicted), so when
        /// the ship's velocity is off by more than <see cref="GuidanceConfig.CorrectionThreshold"/> it turns and makes a short course
        /// correction burn at the chosen acceleration, then turns back.
        /// </remarks>
        private bool Coast(GuidanceInput input, double step, GuidanceCommand cmd)
        {
            double tau = coastArrivalUT - input.UT;
            StateVector planet = config.Target.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double nominal = config.Acceleration * config.BrakingFraction;
            double tb = Math.Min(plannedBrakingTime, tau);
            Vec3 want = v, brake = Vec3.Zero;
            bool tooLate = false;
            for (int i = 0; i < 2; i++)
            {
                targetGravity.Integrate(input.UT,
                    targetGravity.CoastPath(input.UT, r, want, 0, Vec3.Zero, tau - tb, tb, coastEnd.Position, coastEnd.Velocity),
                    tau, out Vec3 g1, out Vec3 g2, PathGravity.CoastSamples);

                // brake * (τ - tb/2) = W, a fixed vector, and |brake| = nominal tb: a quadratic in tb.
                Vec3 p = coastEnd.Position - r - g2, q = coastEnd.Velocity - g1;
                Vec3 w = q * tau - p;
                double disc = tau * tau - 2 * w.Length / nominal;
                tooLate = disc <= 0;
                tb = tooLate ? tau : tau - Math.Sqrt(disc);
                brake = w / (tau - 0.5 * tb);
                want = q - brake;
            }

            plannedBrakingTime = tb;
            double coastLeft = tau - tb;
            // Due within half the last step (a physics step at most when not warping): start braking now.
            if (tooLate || coastLeft <= Math.Max(0.5 * step, 1e-3)) return false;

            cmd.Phase = GuidancePhase.Coast;
            cmd.TimeToArrival = tau;
            Vec3 dv = want - v;
            cmd.CourseError = dv.Length;
            if (!correcting && dv.Length > config.CorrectionThreshold && coastLeft > CorrectionCutoff)
            {
                correcting = true;
                correctionDirection = dv.Normalized();
            }

            // Stop once what is left along the burn is less than half a burning step's worth (the next step would
            // overshoot), or when braking is close (the braking takes care of what is left).
            double left = Vec3.Dot(dv, correctionDirection);
            if (correcting && (left <= Math.Max(0.01, correctionBurning ? 0.5 * config.Acceleration * step : 0) ||
                               coastLeft <= CorrectionCutoff / 2))
                correcting = false;

            correctionBurning = false;
            if (correcting)
            {
                double off = Vec3.Angle(input.Facing, correctionDirection) * 180 / Math.PI;
                correctionBurning = off <= config.RelightToleranceDeg;
                cmd.Correcting = true;
                cmd.Direction = correctionDirection;
                cmd.Acceleration = correctionBurning ? config.Acceleration : 0;
                cmd.TimeToBurn = 0;
                return true;
            }

            cmd.Acceleration = 0;
            cmd.Direction = brake.Length > 0 ? brake.Normalized() : input.Facing;
            cmd.TimeToBurn = coastLeft;
            return true;
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

            // A coast trip flies to the arrival time the coast planner found. (Not with a Sun detour: the planner doesn't
            // offer that combination, ADR 0002 addendum.)
            if (config.Trip == TripType.CoastInTheMiddle && !waypoint.HasValue)
            {
                CoastPlan coast = CoastPlanner.Plan(Request(input), config.DeltaVBudget);
                if (coast.Feasible && coast.CoastTime > 1) coastArrivalUT = coast.ArrivalUT;
            }

            solver = MakeSolver(legs[0]);
        }

        private TripRequest Request(GuidanceInput input) => new TripRequest
        {
            ShipPosition = input.Position,
            ShipVelocity = input.Velocity,
            StartTime = input.UT,
            Target = config.Target,
            Departure = config.Departure,
            Acceleration = config.Acceleration,
            ParkingAltitude = config.ParkingAltitude,
            SunGravParameter = config.SunGravParameter,
            SunSafetyDistance = config.SunSafetyDistance,
            North = config.EquatorNormal,
        };

        private TwoArcSolver MakeSolver(Leg leg)
        {
            flipTime = Math.PI / config.TurnRate;
            double braking = config.Acceleration * config.BrakingFraction;
            if (leg.IsWaypoint)
                return new TwoArcSolver(_ => new StateVector(leg.Waypoint, Vec3.Zero), config.Acceleration, braking);
            return new TwoArcSolver(TargetEndState, config.Acceleration, braking);
        }

        // The parking orbit point the trip ends at (ParkingArrival), less the gravity allowance.
        private StateVector TargetEndState(double arrivalUT)
        {
            StateVector planet = config.Target.Ephemeris.StateAt(arrivalUT);
            StateVector rel = ParkingArrival.Relative(config.Target, arrivalUT, currentPosition, ParkingRadius, config.EquatorNormal);
            return new StateVector(planet.Position + rel.Position - wellPosition, planet.Velocity + rel.Velocity - wellVelocity);
        }

        // What gravity will add over the rest of the current plan, along the expected path: the target's well and the
        // Sun's pull relative to it, plus the target's own curve around the Sun (the plan is solved in the Sun's frame).
        // A full burn's path is close to a smooth curve; a coast trip's is not, so it follows the planned burns and
        // the coast's orbit around the Sun.
        private void UpdateGravityAllowance(GuidanceInput input, TwoArcSolver.Arcs arcs)
        {
            double total = arcs.Total;
            StateVector planet = config.Target.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            Vec3 g1, g2;
            StateVector end = ParkingArrival.Relative(config.Target, input.UT + total, input.Position, ParkingRadius, config.EquatorNormal);
            if (IsCoastTrip)
            {
                targetGravity.Integrate(input.UT,
                    targetGravity.CoastPath(input.UT, r, v, arcs.T1, arcs.A1, arcs.Tc, arcs.T2, end.Position, end.Velocity),
                    total, out g1, out g2, PathGravity.CoastSamples);
            }
            else
            {
                targetGravity.Integrate(input.UT, r, v, end.Position, end.Velocity, total, out g1, out g2);
            }

            targetGravity.FrameCurve(input.UT, total, out Vec3 curveP, out Vec3 curveV);
            wellVelocity = g1 + curveV;
            wellPosition = g2 + curveP;
        }

        private ArrivalLaw MakeArrivalLaw(Leg leg, double arrivalUT)
        {
            double a = config.Acceleration, nominal = a * config.BrakingFraction, min = a * config.MinThrustFraction;
            if (leg.IsWaypoint)
            {
                var point = new FixedEphemeris(leg.Waypoint);
                return new ArrivalLaw(point, new PathGravity(point, 0, config.SunGravParameter, false), Vec3.Zero, Vec3.Zero, nominal, min, a);
            }

            // Fix the arrival point now, so the target doesn't slide around as the ship closes in.
            StateVector rel = ParkingArrival.Relative(config.Target, arrivalUT, currentPosition, ParkingRadius, config.EquatorNormal);
            return new ArrivalLaw(config.Target.Ephemeris, targetGravity, rel.Position, rel.Velocity, nominal, min, a);
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

        /// <summary>If the straight trip passes within the Sun safety distance, the detour's waypoint (SunDetour).</summary>
        private Vec3? PlanDetour(GuidanceInput input)
        {
            if (!(config.SunSafetyDistance > 0)) return null;
            TripRequest request = Request(input);
            request.PlanSunDetour = false;
            request.CheckTargetGravity = false;
            TripPlan straight = TripCalculator.Plan(request);
            if (!straight.Feasible || !straight.PassesNearSun) return null;
            return SunDetour.Plan(request, straight.TripTime, config.DetourMargin, config.EquatorNormal, out _, out _);
        }
    }
}
