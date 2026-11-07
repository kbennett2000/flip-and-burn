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
        /// <summary>
        /// Moon trips leaving the planet (ADR 0005): engine off, waiting in orbit until the ship has come round to the
        /// target's side of the planet, pointing where the burn will go.
        /// </summary>
        Waiting,
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

        /// <summary>
        /// Coast trips: the arrival time to fly to (UT), when it is already known: a resumed trip keeps the one it was
        /// flying to (ADR 0004). NaN, the default, plans the fastest trip the budget allows.
        /// </summary>
        public double CoastArrivalUT { get; set; } = double.NaN;

        /// <summary>Coast trips: a velocity error (m/s) above which the ship makes a course correction burn.</summary>
        public double CorrectionThreshold { get; set; } = 1.0;
        public double MinThrustFraction { get; set; } = 0.982;

        /// <summary>
        /// A trip among a planet's moons (ADR 0005): positions are relative to the planet, which is the central body
        /// ("Sun" in the names above). Its pull is strong near it, so gravity is allowed for along the path the ship will
        /// really fly, and the moon being left pulls too.
        /// </summary>
        public bool PlanetSystemTrip { get; set; }

        /// <summary>How far from the central body a detour's waypoint keeps both legs (m); 0 = the safety distance times the margin.</summary>
        public double DetourClearance { get; set; }

        /// <summary>
        /// Moon trips starting from orbit (around the planet or a moon): wait in orbit for the departure window before burning (ADR 0005).
        /// Off for a trip that is already under way (a resumed trip).
        /// </summary>
        public bool WaitForDepartureWindow { get; set; }

        /// <summary>Moon trips: the other moons to keep clear of.</summary>
        public System.Collections.Generic.List<Obstacle> Obstacles { get; set; } = new System.Collections.Generic.List<Obstacle>();
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
        /// <summary>Coast trips, while coasting: seconds until the braking burn is expected to start. Waiting to leave a
        /// planet: seconds until the burn starts.</summary>
        public double TimeToBurn;
        /// <summary>Seconds until the end of the current leg (the arrival, unless detouring around the Sun).</summary>
        public double TimeToArrival;
        /// <summary>1-based leg number and leg count; 2 legs when detouring around the Sun.</summary>
        public int Leg = 1, LegCount = 1;
        /// <summary>True while burning prograde to leave the departure planet.</summary>
        public bool Departing;
        /// <summary>True during the last burn into the circular parking orbit.</summary>
        public bool Circularizing;

        /// <summary>The planner found no way to the target from here this step (the autopilot stops if it lasts).</summary>
        public bool NoSolution;

        /// <summary>Coast trips: a course correction burn (turning for it, or burning).</summary>
        public bool Correcting;

        /// <summary>Coast trips, while coasting: how far the ship's velocity is from the one that arrives on time (m/s).</summary>
        public double CourseError;

        /// <summary>
        /// The trip couldn't arrive cleanly and is making the ship's orbit around the target safe instead (ADR 0003,
        /// "Arriving"). With <see cref="GuidancePhase.Done"/>: the ship is in that safe orbit.
        /// </summary>
        public bool SafeOrbit;
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
        private bool arrivalReAimed;          // trips between planets: the arrival point chosen again from the real approach
        private bool correcting;              // coast trips: in a course correction burn
        private double brakeAtUT = double.NaN; // coast trips: braking start, latched once it is near (it may only come earlier)
        private bool correctionBurning;       // ... and the engine was on last step
        private Vec3 correctionDirection;
        private bool circularizing;
        private double circularizeSign;       // +1 braking along the orbit, -1 speeding up
        private double lastUT = double.NaN;
        private Vec3 currentPosition;
        private bool initialized;
        private bool climbedOut;
        // Full burns: the last solution while speeding up, and when it was found (for the flip, below).
        private TwoArcSolver.Arcs lastArcs;
        private double lastArcsUT = double.NaN;

        /// <summary>
        /// Full burns: when the flip is this close (s) and the next solution jumps to a different one (or there is none),
        /// flip now on the last solution. Near the flip the solution can stop existing a step early, when the time to the
        /// flip would go below zero, and the search that follows finds another trip altogether (brake first, then speed up),
        /// which never flips. Seen leaving a planet's strong gravity on a moon trip (ADR 0005).
        /// </summary>
        public const double FlipSnapSeconds = 1.0;

        public FlipAndBurnGuidance(GuidanceConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            if (config.Target == null) throw new ArgumentException("no target", nameof(config));
            if (!(config.Acceleration > 0)) throw new ArgumentException("acceleration must be positive", nameof(config));
            targetGravity = new PathGravity(config.Target.Ephemeris, config.Target.GravParameter, config.SunGravParameter, true);
            // The braking share: the configured one, lower for an arrival in a deep well (CoastPlanner.BrakingFractionFor).
            brakingFraction = Math.Min(config.BrakingFraction, CoastPlanner.BrakingFractionFor(config.Target, config.ParkingAltitude, config.Acceleration));
            if (config.PlanetSystemTrip && config.Departure != null && !config.Departure.IsCentralBody)
                targetGravity.AddBody(config.Departure.Ephemeris, config.Departure.GravParameter);
        }

        /// <summary>
        /// Moon trips: the braking may go down to this share of the acceleration (90 %, against 98.2 % between planets). A moon
        /// trip lasts minutes to hours, not days, so being a little ahead or behind at the flip is a bigger share of the braking
        /// left, and the narrow band couldn't take it up: at Ike at 2 g the braking sat on the 98.2 % floor for its last minute
        /// and arrived 20 km low (ADR 0005).
        /// </summary>
        public const double PlanetSystemMinThrust = 0.90;

        private readonly double brakingFraction;

        /// <summary>The share of the acceleration the braking is planned at (lower than configured for a deep-well arrival).</summary>
        public double BrakingFraction => brakingFraction;

        // The share of the acceleration the braking will average, before it starts, for the delta-v the trip still needs. Moon
        // trips: in a deep well the arrival law uses the headroom at the end (CoastPlanner.BrakingFlownShare). Between planets
        // the braking flies at its planned share, for the time the coast works out (which allows for the target's well).
        private double BrakingFlown => config.PlanetSystemTrip ? CoastPlanner.BrakingFlownShare(brakingFraction) : brakingFraction;

        /// <summary>The lowest share of the acceleration the braking may use (the floor of the arrival law's band).</summary>
        public double BrakingFloor => MinThrust;

        // The band's floor, moved down with the braking share so the band keeps the room below it that it was given.
        private double MinThrust => Math.Min(config.PlanetSystemTrip ? Math.Min(config.MinThrustFraction, PlanetSystemMinThrust) : config.MinThrustFraction,
            brakingFraction - (config.BrakingFraction - config.MinThrustFraction));

        /// <summary>The braking band's floor (share of the acceleration) for a braking share, with the default settings: as <see cref="BrakingFloor"/>.</summary>
        public static double BrakingFloorShare(bool planetSystemTrip, double brakingShare)
        {
            var d = new GuidanceConfig();
            return Math.Min(planetSystemTrip ? Math.Min(d.MinThrustFraction, PlanetSystemMinThrust) : d.MinThrustFraction,
                brakingShare - (d.BrakingFraction - d.MinThrustFraction));
        }

        /// <summary>Moon trips: the flown-path gravity allowance is worked out again this often (game seconds).</summary>
        public const double AllowanceEvery = 0.5;
        private double lastAllowanceUT = double.NaN;

        public GuidancePhase Phase => phase;

        /// <summary>The Sun-detour waypoint, if this trip uses one.</summary>
        public Vec3? DetourWaypoint => legs.Count > 1 ? legs[0].Waypoint : (Vec3?)null;

        /// <summary>
        /// Seconds of burning left in the trip at the acceleration being flown, as the latest plan has it (the speed-up still
        /// to go and the braking; a coast trip's coast doesn't count). NaN when not known (making the orbit safe).
        /// </summary>
        public double BurnTimeLeft { get; private set; } = double.NaN;

        /// <summary>
        /// Delta-v the burning left will use (m/s), as the latest plan has it: the speed-up at the chosen acceleration, the
        /// braking at its own share (<see cref="BrakingFraction"/>). NaN when not known.
        /// </summary>
        public double BurnDeltaVLeft { get; private set; } = double.NaN;

        /// <summary>Coast trips: the arrival time being flown to (NaN for a full burn).</summary>
        public double CoastArrivalUT => coastArrivalUT;

        /// <summary>Diagnostics for tests: solver counters as text.</summary>
        /// <summary>Diagnostics for tests: the position given at the last update.</summary>
        public Vec3 LastPosition => currentPosition;

        public string LawStats => law == null ? "" : $"law asked {law.LastAsked:F3} tau {law.LastTau:F1}";

        public string SolverStats => $"two-arc warm {solver?.WarmSolves} robust {solver?.RobustSolves}; arrival nudged {law?.Nudged} carried {law?.Carried}";

        private double ParkingRadius => config.Target.Radius + config.ParkingAltitude;
        private bool IsCoastTrip => !double.IsNaN(coastArrivalUT);

        /// <summary>
        /// The ship is still inside the departure planet's sphere of influence. The burning left (<see cref="BurnDeltaVLeft"/>)
        /// doesn't count that planet's pull, so until the ship is out the reserve shown comes from the plan.
        /// </summary>
        public bool LeavingDeparture { get; private set; }

        public GuidanceCommand Update(GuidanceInput input)
        {
            TargetPlanet dep = config.Departure;
            LeavingDeparture = dep != null && !dep.IsCentralBody && dep.SphereOfInfluence > 0 &&
                               (input.Position - dep.Ephemeris.StateAt(input.UT).Position).Length < dep.SphereOfInfluence;
            if (safeOrbit)
            {
                BurnTimeLeft = BurnDeltaVLeft = double.NaN;
                return SafeOrbitStep(input);
            }

            if (circularizing)
            {
                BurnTimeLeft = BurnDeltaVLeft = 0;
                return Circularize(input);
            }

            if (!windowChecked) CheckWindow(input);
            if (input.UT < departureUT) return Waiting(input);
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
                if (!IsCoastTrip && !double.IsNaN(lastArcsUT))
                {
                    double expectedT1 = lastArcs.T1 - (input.UT - lastArcsUT);
                    if (expectedT1 <= FlipSnapSeconds && (!arcs.Valid || !solver.LastWasWarm || Math.Abs(arcs.T1 - expectedT1) > FlipSnapSeconds))
                    {
                        // The flip is due: flip on the last solution (see FlipSnapSeconds).
                        double arrival = lastArcsUT + lastArcs.Total;
                        lastArcsUT = double.NaN;
                        law = MakeArrivalLaw(leg, arrival);
                        law.Start(input.UT, Math.Max(1, arrival - input.UT));
                        phase = GuidancePhase.Flip;
                        return Update(input);
                    }
                }

                if (!arcs.Valid)
                {
                    // No solution this step (should not happen in practice): hold course, keep thrusting.
                    cmd.Acceleration = config.Acceleration;
                    cmd.NoSolution = true;
                    return cmd;
                }

                // A full burn plans the flip as a coast lasting as long as the turn takes (from the last solution; a
                // half-turn to begin with), so turning doesn't leave the ship short of speed.
                if (!IsCoastTrip) flipTime = Vec3.Angle(arcs.A1, arcs.A2) / config.TurnRate;
                if (!leg.IsWaypoint) UpdateGravityAllowance(input, arcs);

                BurnTimeLeft = arcs.T1 + arcs.T2;
                double brakingLeft = config.Acceleration * BrakingFlown * arcs.T2;
                if (IsCoastTrip && !config.PlanetSystemTrip && !leg.IsWaypoint)
                    brakingLeft = Math.Max(brakingLeft, CoastPlanner.BrakingFloor(config.Target, config.ParkingAltitude, config.Acceleration,
                        CoastPlanner.ArrivalSpeed(config.Target, config.SunGravParameter, input.UT, input.Position, input.Velocity, arcs)));
                BurnDeltaVLeft = config.Acceleration * arcs.T1 + brakingLeft;
                if (!IsCoastTrip)
                {
                    lastArcs = arcs;
                    lastArcsUT = input.UT;
                }

                if (arcs.T1 > Math.Max(step, 1e-3))
                {
                    cmd.Direction = arcs.A1.Normalized();
                    cmd.Acceleration = config.Acceleration;
                    cmd.TimeToFlip = arcs.T1;
                    cmd.TimeToArrival = arcs.Total;
                    Vec3 climb = cmd.Direction;
                    if (legIndex == 0 && NeedsToClimbOut(input, ref climb, out bool fire))
                    {
                        cmd.Direction = climb;
                        cmd.Departing = true;
                        if (!fire) cmd.Acceleration = 0;
                    }

                    return cmd;
                }

                if (IsCoastTrip && !leg.IsWaypoint)
                {
                    // Fix the arrival point now, so it doesn't slide around during the coast.
                    coastEnd = ParkingArrival.Relative(config.Target, coastArrivalUT, currentPosition, ParkingRadius, config.EquatorNormal);
                    plannedBrakingTime = arcs.T2;
                    brakeAtUT = double.NaN;
                    phase = GuidancePhase.Coast;
                }
                else
                {
                    law = MakeArrivalLaw(leg, input.UT + arcs.Total);
                    law.Start(input.UT, arcs.Tc + arcs.T2);
                    phase = GuidancePhase.Flip;
                }

                lastArcsUT = double.NaN;
            }

            if (phase == GuidancePhase.Coast)
            {
                if (Coast(input, step, cmd)) return cmd;

                // Time to brake: the arrival law takes over, with the same arrival point and time.
                double a = config.Acceleration;
                law = new ArrivalLaw(config.Target.Ephemeris, targetGravity, coastEnd.Position, coastEnd.Velocity,
                    a * brakingFraction, a * MinThrust, a);
                law.Start(input.UT, coastArrivalUT - input.UT);
                phase = GuidancePhase.Decelerate;
                // Moon trips: braking starts now; set the deadline to fly the planned braking from here (ADR 0005).
                if (config.PlanetSystemTrip) law.Retune(input.UT, input.Position, input.Velocity, law.Nominal);
            }

            Vec3 thrust = law.Solve(input.UT, input.Position, input.Velocity, out double timeToGo);
            Vec3 want = thrust.Normalized();
            cmd.TimeToArrival = timeToGo;
            BurnTimeLeft = timeToGo;
            BurnDeltaVLeft = law.ForecastDeltaV();

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
                if (config.PlanetSystemTrip && !leg.IsWaypoint)
                {
                    // Moon trips: braking starts now; set the deadline to fly the planned braking from here (ADR 0005).
                    if (law.Retune(input.UT, input.Position, input.Velocity, law.Nominal))
                    {
                        thrust = law.Solve(input.UT, input.Position, input.Velocity, out timeToGo);
                        want = thrust.Normalized();
                        cmd.TimeToArrival = timeToGo;
                        BurnTimeLeft = timeToGo;
                        BurnDeltaVLeft = law.ForecastDeltaV();
                    }
                }
            }

            if (timeToGo <= config.ArrivalCutoff)
            {
                if (leg.IsWaypoint && legIndex + 1 < legs.Count)
                {
                    // Reached the detour waypoint: start the next leg from here.
                    legIndex++;
                    lastArcsUT = double.NaN;
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

            if (!leg.IsWaypoint && ReadyToSettle(input))
            {
                // Already at the parking orbit's height, moving along it a little fast: the last burn takes it from here.
                phase = GuidancePhase.FinalApproach;
                circularizing = true;
                circularizeSign = 0;
                return Circularize(input);
            }

            // Final approach: inside the target's sphere of influence; flying to the planet a moon trip is centred on (whose
            // sphere holds the whole trip), within ten parking-orbit radii of it.
            double near = config.Target.IsCentralBody ? 10 * ParkingRadius : config.Target.SphereOfInfluence;
            bool nearTarget = !leg.IsWaypoint && near > 0 &&
                              (input.Position - config.Target.Ephemeris.StateAt(input.UT).Position).Length < near;
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
            double nominal = config.Acceleration * brakingFraction;
            double tb = Math.Min(plannedBrakingTime, tau);
            // Trips between planets: the arrival point was chosen at the coast's start for a ship coming in along the straight
            // line from where it was. A long coast curves around the Sun and comes in from elsewhere (a slow coast to Dres:
            // the braking swung the ship round at the end, dipped below the parking height and used 20 % more delta-v). Once
            // inside the target's sphere, well before braking, choose it again from the real approach, the ship's velocity
            // relative to the target; a course correction then moves to it.
            if (!config.PlanetSystemTrip && !arrivalReAimed && r.Length < config.Target.SphereOfInfluence && tau - tb > 2 * CorrectionCutoff)
            {
                arrivalReAimed = true;
                Vec3 side = ParkingArrival.Side(v, config.EquatorNormal);
                coastEnd = new StateVector(side * ParkingRadius, ParkingArrival.OrbitVelocity(side, config.EquatorNormal, config.Target.GravParameter, ParkingRadius));
                brakeAtUT = double.NaN;
            }

            // Trips between planets: braking can't take less than braking all at once at the parking height would, for the speed
            // the ship arrives with (falling into the target's pull adds to it), plus what finite thrust loses deep in a strong
            // pull (CoastPlanner.OneShotBraking, DeepWellLoss). Planned shorter, it started too late: into Jool at 1 g on 2 % of
            // a full burn's delta-v the arrival watch had to step in. The coast then aims for that longer, gentler braking.
            double floorDeltaV = 0, floorTime = 0;
            if (!config.PlanetSystemTrip)
            {
                double mu = config.Target.GravParameter;
                // The speed it arrives with: inside the target's sphere, from its energy there; outside, where its coast around the
                // Sun takes it by the time braking is due (the Sun's pull changes it over a long coast).
                double arriving;
                if (r.Length < config.Target.SphereOfInfluence || !(config.SunGravParameter > 0))
                    arriving = Math.Sqrt(Math.Max(0, v.LengthSquared - 2 * mu / r.Length));
                else
                {
                    double until = Math.Max(0, tau - tb);
                    StateVector ship = TwoBody.Propagate(input.Position, input.Velocity, config.SunGravParameter, until);
                    arriving = (ship.Velocity - config.Target.Ephemeris.StateAt(input.UT + until).Velocity).Length;
                }
                floorDeltaV = CoastPlanner.BrakingFloor(config.Target, config.ParkingAltitude, config.Acceleration, arriving);
                floorTime = floorDeltaV / (config.Acceleration * CoastPlanner.BrakingFlownShare(brakingFraction));
            }

            Vec3 want = v, brake = Vec3.Zero;
            bool tooLate = false;
            // Inside the target's sphere of influence the smooth path guessed for a trip between planets misses how the target
            // bends the approach (a slow coast into Dres drifted 34 m/s off course in its last ten minutes, with corrections
            // already over): there, as on a moon trip, gravity is added up along the path really flown.
            bool flownPath = config.PlanetSystemTrip || r.Length < config.Target.SphereOfInfluence;
            for (int i = 0; i < (flownPath ? 3 : 2); i++)
            {
                Vec3 g1, g2;
                if (flownPath)
                    targetGravity.IntegratePlanned(input.UT, r, want, Vec3.Zero, 0, tau - tb, brake, tb, out g1, out g2);   // ADR 0005
                else
                    targetGravity.Integrate(input.UT,
                        targetGravity.CoastPath(input.UT, r, want, 0, Vec3.Zero, tau - tb, tb, coastEnd.Position, coastEnd.Velocity),
                        tau, out g1, out g2, PathGravity.CoastSamples);

                // brake * (τ - tb/2) = W, a fixed vector, and |brake| = nominal tb: a quadratic in tb.
                Vec3 p = coastEnd.Position - r - g2, q = coastEnd.Velocity - g1;
                Vec3 w = q * tau - p;
                double disc = tau * tau - 2 * w.Length / nominal;
                tooLate = disc <= 0;
                tb = tooLate ? tau : tau - Math.Sqrt(disc);
                if (floorTime > tb) tb = Math.Min(tau, floorTime);
                brake = w / (tau - 0.5 * tb);
                want = q - brake;
            }

            plannedBrakingTime = tb;
            BurnTimeLeft = tb;
            // As the braking will be flown: the halfway share where the coast adds gravity up along the path really flown (a moon
            // trip, or inside the target's sphere), the planned share otherwise (CoastPlanner.BrakingFlownShare); never under
            // the floor.
            BurnDeltaVLeft = Math.Max(floorDeltaV,
                config.Acceleration * (flownPath ? CoastPlanner.BrakingFlownShare(brakingFraction) : BrakingFlown) * tb);
            double coastLeft = tau - tb;
            // Once braking is near, latch its start: it may come earlier, never later. Close to it the coast plan is
            // ill-conditioned at high speed (the time left is the square root of a small difference of large numbers: at
            // 320 km/s a 2 m/s error moves it by two minutes), and left free the time left recedes step after step while the
            // course error balloons, so braking never starts (ADR 0003, "Arriving"). The braking law corrects what is left.
            if (!double.IsNaN(brakeAtUT) || coastLeft < CorrectionCutoff)
            {
                brakeAtUT = double.IsNaN(brakeAtUT) ? input.UT + coastLeft : Math.Min(brakeAtUT, input.UT + coastLeft);
                coastLeft = brakeAtUT - input.UT;
            }

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

        private bool trimming;
        private double trimStartUT = double.NaN;

        /// <summary>Moon trips: the trim burn stops when the velocity is this close to circular (m/s), or after <see cref="TrimSeconds"/>.</summary>
        public const double TrimTolerance = 0.2;
        public const double TrimSeconds = 600;

        /// <summary>Moon trips: the trim also brings the ship to the parking height, climbing or sinking at up to this (m/s).</summary>
        public const double TrimClimbRate = 30;

        /// <summary>
        /// Moon trips (ADR 0005): a last trim once the speed along the orbit is right. At a small moon the last burn takes a
        /// second or two at the chosen acceleration, too short to settle the climb or fall left from the braking, and 1 % of
        /// orbital speed left over is a 5 % error in height. And arriving much too fast in a strong pull (Tylo, 1.4 km/s over
        /// orbital speed), the last burn can't hold the height at all: the ship balloons up and falls back, and the burn ends
        /// wherever the speed comes right. So steer the velocity to circular with a gentle climb or sink toward the parking
        /// height (up to <see cref="TrimClimbRate"/>), and finish only within 1 % of the parking altitude: turn with the
        /// engine off if need be, then burn gently.
        /// </summary>
        private bool Trim(GuidanceInput input, Vec3 r, Vec3 v, Vec3 up, double vc, GuidanceCommand cmd)
        {
            Vec3 vt = v - up * Vec3.Dot(v, up);
            Vec3 along = vt.Length > 1e-6 ? vt.Normalized() : Vec3.Cross(Vec3.Cross(up, config.EquatorNormal), up).Normalized();
            double rl = r.Length, heightError = ParkingRadius - rl;
            double climb = Math.Max(-TrimClimbRate, Math.Min(TrimClimbRate, 0.02 * heightError));
            Vec3 dv = along * vc + up * climb - v;
            if (!trimming) trimStartUT = input.UT;
            trimming = true;
            bool atHeight = Math.Abs(heightError) <= Math.Max(100, 0.01 * config.ParkingAltitude);
            if ((atHeight && (along * vc - v).Length <= Math.Max(TrimTolerance, 0.0005 * vc)) || input.UT - trimStartUT > TrimSeconds)
            {
                trimming = false;
                return false;
            }

            cmd.Direction = dv.Normalized();
            bool pointed = Vec3.Angle(input.Facing, cmd.Direction) * 180 / Math.PI <= config.RelightToleranceDeg;
            cmd.Acceleration = pointed ? Math.Min(config.Acceleration, dv.Length / 2) : 0;
            cmd.TimeToArrival = dv.Length / config.Acceleration;
            return true;
        }

        private Vec3 circularizeDirection;
        private double circularizeDirectionUT = double.NaN;

        /// <summary>
        /// Once the last burn is under way, its direction changes no faster than the ship can turn. To hold the height in a
        /// strong pull the burn keeps tilting up and down; a ship that can't follow points more than 5 degrees off, and the
        /// autopilot holds the engine at zero until it catches up, so the height drifts. Seen at Tylo: a 5-degree-a-second
        /// ship arrived 7 km low watched and 9 km high in the background (ADR 0005 addendum, cycle 15).
        /// </summary>
        private Vec3 LimitTurn(Vec3 want, double ut)
        {
            if (double.IsNaN(circularizeDirectionUT))
            {
                circularizeDirection = want;
                circularizeDirectionUT = ut;
                return want;
            }

            double max = config.TurnRate * Math.Max(0, ut - circularizeDirectionUT);
            double angle = Vec3.Angle(circularizeDirection, want);
            if (angle > max && angle > 1e-9)
            {
                Vec3 axis = Vec3.Cross(circularizeDirection, want);
                if (axis.Length < 1e-12) axis = Vec3.Cross(circularizeDirection, new Vec3(0, 0, 1));
                axis = axis.Normalized();
                Vec3 perp = Vec3.Cross(axis, circularizeDirection);
                want = (circularizeDirection * Math.Cos(max) + perp * Math.Sin(max)).Normalized();
            }

            circularizeDirection = want;
            circularizeDirectionUT = ut;
            return want;
        }

        /// <summary>The last burn turns with the engine off when it is more than this far (degrees) from where it should point.</summary>
        public const double CircularizeTurnFirstDeg = 30;

        /// <summary>
        /// The braking can reach the parking orbit a little early, and then has to keep braking at 98-100 % until its
        /// deadline, dropping below orbital speed (at the Mun, 20 s early ended 200 m/s too slow; ADR 0005). What matters on
        /// arrival is the orbit's shape, not the point on it: so once the ship is at the parking height (within 2 % of the
        /// altitude), hardly climbing or falling, and at most 3 seconds' braking above orbital speed, hand over to the last
        /// burn, which holds the height while it brakes to exactly orbital speed.
        /// </summary>
        private bool ReadyToSettle(GuidanceInput input)
        {
            StateVector planet = config.Target.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double rl = r.Length;
            if (Math.Abs(rl - ParkingRadius) > Math.Max(200, 0.02 * config.ParkingAltitude)) return false;
            double vr = Vec3.Dot(v, r / rl);
            double vt = (v - r / rl * vr).Length;
            double vc = Math.Sqrt(config.Target.GravParameter / rl);
            double excess = vt - vc;
            return Math.Abs(vr) <= Math.Max(2, 0.01 * vc) && excess >= 0 && excess <= 3 * config.Acceleration;
        }

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

            if (circularizeSign == 0)
            {
                circularizeSign = Math.Sign(excess);
                circularizeDirectionUT = double.NaN;
            }
            // Done once the speed reaches circular (it has crossed over), or there is nothing left to do.
            if (trimming || circularizeSign * excess <= 0 || Math.Abs(excess) < 0.05)
            {
                // Every trip (moon trips since ADR 0005, trips between planets since cycle 15): a slow arrival at a small planet
                // can end at orbital speed along the orbit but still sinking a few m/s (Duna to Dres on a small budget: 18 x 22 km
                // at 20 km), which the check above doesn't see. The trim settles the height and the whole velocity.
                if (Trim(input, r, v, up, vc, cmd)) return cmd;
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

            cmd.Direction = LimitTurn((up * radial + along * tangential).Normalized(), input.UT);
            // A big turn first, then burn: thrust while turning half-way round pushes the ship the wrong way. Arriving a little
            // fast this burn brakes along the braking direction and needs at most a small turn; arriving a little slow it
            // speeds up, half a turn away.
            bool pointed = Vec3.Angle(input.Facing, cmd.Direction) * 180 / Math.PI <= CircularizeTurnFirstDeg;
            cmd.Acceleration = pointed ? a : 0;
            cmd.TimeToArrival = Math.Abs(excess) / a;
            return cmd;
        }

        // ------------------------------------------------------------------ a safe orbit instead

        private bool safeOrbit;
        private double safeOrbitAcceleration;

        /// <summary>Why the trip is making a safe orbit instead of the parking orbit (null if it isn't).</summary>
        public string SafeOrbitReason { get; private set; }

        /// <summary>
        /// The trip can't arrive cleanly (falling too fast to stop above the atmosphere, flying past, or the plan has failed
        /// near the target): from now on, put the ship into a circular orbit around the target instead, wherever it can
        /// stop, and no lower than the parking orbit, burning at <paramref name="acceleration"/> (the engines' full thrust).
        /// </summary>
        public void EnterSafeOrbit(GuidanceInput input, string reason, double acceleration)
        {
            if (safeOrbit) return;
            safeOrbitAcceleration = acceleration > 0 ? acceleration : config.Acceleration;
            SafeOrbitReason = reason;
            safeOrbit = true;
            circularizing = false;
            phase = GuidancePhase.FinalApproach;
        }

        /// <summary>The acceleration the safe orbit is being made at (m/s^2), or NaN.</summary>
        public double SafeOrbitAcceleration => safeOrbit ? safeOrbitAcceleration : double.NaN;

        /// <summary>The heat guard stepped down: make the safe orbit at this lower acceleration from now on.</summary>
        public void SetSafeOrbitAcceleration(double acceleration)
        {
            if (safeOrbit && acceleration > 0) safeOrbitAcceleration = acceleration;
        }

        // Steer the velocity straight to circular at the current radius (the cheapest way to a safe orbit from here), climbing
        // gently if below the parking orbit. Done once the orbit is circular, at or above the parking orbit.
        private GuidanceCommand SafeOrbitStep(GuidanceInput input)
        {
            var cmd = new GuidanceCommand { Phase = GuidancePhase.FinalApproach, Leg = legIndex + 1, LegCount = Math.Max(1, legs.Count), SafeOrbit = true };
            if (phase == GuidancePhase.Done)
            {
                cmd.Phase = GuidancePhase.Done;
                cmd.Direction = input.Facing;
                return cmd;
            }

            double a = safeOrbitAcceleration, mu = config.Target.GravParameter;
            StateVector planet = config.Target.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double rl = r.Length;
            Vec3 up = r / rl;
            double vr = Vec3.Dot(v, up);
            Vec3 vtVec = v - up * vr;
            double vt = vtVec.Length;
            Vec3 along = vt > 1e-3 ? vtVec / vt : Vec3.Cross(Vec3.Cross(up, config.EquatorNormal), up).Normalized();
            if (along.LengthSquared < 0.5) along = Vec3.Cross(up, new Vec3(1, 0, 0)).Normalized();

            // Below the parking orbit: climb at up to 50 m/s, slowing as it gets there.
            double climb = rl < ParkingRadius ? Math.Min(50, 0.02 * (ParkingRadius - rl) + 1) : 0;
            Vec3 wanted = along * Math.Sqrt(mu / rl) + up * climb;
            Vec3 error = wanted - v;
            double off = error.Length;
            if (off < 0.5 && climb == 0)
            {
                phase = GuidancePhase.Done;
                cmd.Phase = GuidancePhase.Done;
                cmd.Direction = input.Facing;
                return cmd;
            }

            cmd.Direction = error / off;
            cmd.Acceleration = Math.Min(a, off / 2);   // full thrust, easing off over the last few m/s
            cmd.TimeToArrival = off / a;
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
                if (config.CoastArrivalUT > input.UT)
                {
                    coastArrivalUT = config.CoastArrivalUT;
                    if (config.PlanetSystemTrip)
                    {
                        // Start from the allowance a plan to that arrival settles on (ADR 0005).
                        CoastPlan kept = CoastPlanner.PlanArrivingAt(Request(input), Math.Max(config.DeltaVBudget, 1), coastArrivalUT);
                        wellVelocity = kept.WellVelocity;
                        wellPosition = kept.WellPosition;
                    }
                }
                else
                {
                    CoastPlan coast = CoastPlanner.Plan(Request(input), config.DeltaVBudget);
                    if (coast.Feasible && coast.CoastTime > 1)
                    {
                        coastArrivalUT = coast.ArrivalUT;
                        // Moon trips: start from the plan's gravity allowance; deep in a planet's pull the fixed-arrival
                        // solve has no answer without it (ADR 0005).
                        if (config.PlanetSystemTrip)
                        {
                            wellVelocity = coast.WellVelocity;
                            wellPosition = coast.WellPosition;
                        }
                    }
                }
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
            PlanetSystemTrip = config.PlanetSystemTrip,
            DetourClearance = config.DetourClearance,
            Obstacles = config.Obstacles,
        };

        private TwoArcSolver MakeSolver(Leg leg)
        {
            flipTime = Math.PI / config.TurnRate;
            double braking = config.Acceleration * brakingFraction;
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
            if (config.PlanetSystemTrip)
            {
                // Along the path the planned burns really fly (ADR 0005), worked out twice a game second.
                if (!double.IsNaN(lastAllowanceUT) && input.UT - lastAllowanceUT < AllowanceEvery) return;
                lastAllowanceUT = input.UT;
                targetGravity.IntegratePlanned(input.UT, r, v, arcs.A1, arcs.T1, arcs.Tc, arcs.A2, arcs.T2, out g1, out g2);
                targetGravity.FrameCurve(input.UT, total, out Vec3 cp, out Vec3 cv);
                wellVelocity = g1 + cv;
                wellPosition = g2 + cp;
                return;
            }

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
            double a = config.Acceleration, nominal = a * brakingFraction, min = a * MinThrust;
            if (leg.IsWaypoint)
            {
                var point = new FixedEphemeris(leg.Waypoint);
                return new ArrivalLaw(point, new PathGravity(point, 0, config.SunGravParameter, false), Vec3.Zero, Vec3.Zero, nominal, min, a);
            }

            // Fix the arrival point now, so the target doesn't slide around as the ship closes in.
            StateVector rel = ParkingArrival.Relative(config.Target, arrivalUT, currentPosition, ParkingRadius, config.EquatorNormal);
            return new ArrivalLaw(config.Target.Ephemeris, targetGravity, rel.Position, rel.Velocity, nominal, min, a);
        }

        // ------------------------------------------------------------------ waiting to leave the planet (moon trips)

        private bool windowChecked;
        private double departureUT = double.NaN;
        private Vec3 departureDirection;
        private double plannedBurnTime, plannedBurnDeltaV;

        /// <summary>Moon trips leaving the planet: when the burn starts (UT), or NaN when it started (or starts) straight away.</summary>
        public double DepartureUT => departureUT;

        // Leaving the planet at the centre with the target behind it: wait until the ship has come round (DepartureWindow).
        private void CheckWindow(GuidanceInput input)
        {
            windowChecked = true;
            if (!config.PlanetSystemTrip || !config.WaitForDepartureWindow || config.Departure == null || initialized) return;
            TripRequest request = Request(input);
            double wait = DepartureWindow.Find(request, out StateVector s);
            if (!(wait > 1)) return;
            departureUT = input.UT + wait;
            request.ShipPosition = s.Position;
            request.ShipVelocity = s.Velocity;
            request.StartTime = departureUT;
            request.PlanSunDetour = false;
            request.CheckTargetGravity = false;
            TripPlan plan = TripCalculator.Plan(request);
            departureDirection = plan.Feasible && plan.TripTime > 0 ? (plan.Profile.C + plan.Profile.E * plan.Profile.Ab).Normalized() : s.Velocity.Normalized();
            plannedBurnTime = plan.Feasible ? plan.TripTime : double.NaN;
            plannedBurnDeltaV = plan.Feasible ? plan.TotalDeltaV - plan.ParkingOrbitDeltaV : double.NaN;
        }

        private GuidanceCommand Waiting(GuidanceInput input)
        {
            BurnTimeLeft = plannedBurnTime;
            BurnDeltaVLeft = plannedBurnDeltaV;
            return new GuidanceCommand
            {
                Phase = GuidancePhase.Waiting,
                Direction = departureDirection,
                Acceleration = 0,
                TimeToBurn = departureUT - input.UT,
                TimeToFlip = departureUT - input.UT + plannedBurnTime / 2,
                TimeToArrival = departureUT - input.UT + plannedBurnTime,
                Leg = 1,
                LegCount = 1,
            };
        }

        // ------------------------------------------------------------------ leaving the departure planet

        /// <summary>
        /// Near the departure planet, thrusting straight at the target can point into the planet (from its far side) or
        /// fail to climb out. Burn prograde until the ship is escaping, then until the line toward the target clears the planet
        /// (<see cref="ClimbOut"/>).
        /// </summary>
        private bool NeedsToClimbOut(GuidanceInput input, ref Vec3 direction, out bool fire)
        {
            fire = true;
            TargetPlanet dep = config.Departure;
            // Moon trips keep checking until the ship leaves the moon's sphere of influence: once out of orbit, the plan
            // can still swing to aim through the moon at the target behind it (ADR 0005).
            if (dep == null || (climbedOut && !config.PlanetSystemTrip)) return false;
            StateVector planet = dep.Ephemeris.StateAt(input.UT);
            Vec3 r = input.Position - planet.Position, v = input.Velocity - planet.Velocity;
            double soi = dep.SphereOfInfluence > 0 ? dep.SphereOfInfluence : 100 * dep.Radius;
            if (r.Length > soi)
            {
                climbedOut = true;
                return false;
            }

            double safe = dep.SafeRadius > 0 ? dep.SafeRadius : dep.Radius;
            if (!ClimbOut.Steer(r, v, dep.GravParameter, safe, direction, config.Acceleration, config.PlanetSystemTrip, out direction, out fire))
            {
                climbedOut = true; // once out, stay out: no switching back and forth
                return false;
            }

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
