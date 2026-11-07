using System;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core
{
    /// <summary>A burn-coast-burn trip.</summary>
    public sealed class CoastPlan
    {
        public bool Feasible { get; internal set; }
        public string Problem { get; internal set; } = "";
        public double BurnUpTime { get; internal set; }      // s, first burn
        public double CoastTime { get; internal set; }       // s, engine off (the flip happens here)
        public double BurnDownTime { get; internal set; }    // s, braking burn
        public double TripTime { get; internal set; }        // s
        public double ArrivalUT { get; internal set; }
        /// <summary>
        /// Delta-v of the two planned burns, gravity included (m/s), the braking at the share the guidance flies
        /// (<see cref="CoastPlanner.BrakingFlownShare"/>); counted in the ship's delta-v at the chosen acceleration, so braking
        /// below it on an engine that loses Isp at part throttle counts what it really burns (<see cref="CoastPlanner.BrakingCost"/>).
        /// </summary>
        public double DeltaV { get; internal set; }
        public double Budget { get; internal set; }
        /// <summary>Moon trips leaving the planet: the wait in orbit before the first burn (s), included in <see cref="TripTime"/>.</summary>
        public double DepartureWait { get; internal set; }

        /// <summary>
        /// The gravity allowance the plan settled on (what gravity adds over the trip, in velocity and position). The guidance
        /// starts from it: deep in a planet's pull the fixed-arrival solve has no answer without it (ADR 0005).
        /// </summary>
        internal Vec3 WellVelocity, WellPosition;

        /// <summary>
        /// Set when the acceleration is too low for this coast trip's braking (m/s^2): the least that works. Moon trips falling
        /// to the planet at the centre (ADR 0005).
        /// </summary>
        public double MinimumAcceleration { get; internal set; }
        /// <summary>Delta-v to allow for flying the trip: the burns plus the guidance margin (m/s).</summary>
        public double DeltaVNeeded => DeltaV * (1 + CoastPlanner.GuidanceMargin);
    }

    /// <summary>
    /// Plans the fastest burn-coast-burn trip whose burns fit a delta-v budget (ADR 0002 addendum). Uses the same
    /// two-arc solver, arrival point and gravity estimate as the guidance, so the guidance flies what this promises.
    /// </summary>
    public static class CoastPlanner
    {
        public const double BrakingFraction = 0.99;   // same as GuidanceConfig.BrakingFraction

        /// <summary>Deep wells (cycle 15): at or below this share of the acceleration, the target's pull at the parking orbit leaves the braking at <see cref="BrakingFraction"/>.</summary>
        public const double DeepWellStart = 0.25;
        /// <summary>Deep wells: from this share on, the braking is planned at <see cref="DeepWellFraction"/>.</summary>
        public const double DeepWellFull = 0.4;
        public const double DeepWellFraction = 0.85;

        /// <summary>
        /// The share of the chosen acceleration the braking is planned at, for an arrival where the target pulls
        /// <paramref name="gravityAtParking"/> (m/s^2) at the parking orbit. Braking steers by the arrival law, whose thrust
        /// changes along the braking; diving into a deep well (Jool, Tylo, Laythe, Kerbin from its moons) the end of that
        /// braking has to hold the ship up against the target's pull as well as slow it, and asked more than the
        /// acceleration: the law ran at full thrust, fell behind, and reached the parking orbit up to 1.25 km/s fast, which the
        /// last burn then took off (ADR 0005 addendum). Planned with this much headroom, the law can rise to full thrust at the
        /// end and arrives on the plan. 99 % in a shallow well; down to 90 % where the pull is half the acceleration or more.
        /// </summary>
        public static double BrakingFractionFor(double gravityAtParking, double acceleration)
        {
            double share = acceleration > 0 ? gravityAtParking / acceleration : 0;
            if (!(share > DeepWellStart)) return BrakingFraction;
            double x = Math.Min(1, (share - DeepWellStart) / (DeepWellFull - DeepWellStart));
            return BrakingFraction - (BrakingFraction - DeepWellFraction) * x;
        }

        /// <summary>
        /// The share of the acceleration a coast plan counts the braking's delta-v at, for its planned share: the same where it
        /// is the usual 99 %; in a deep well, halfway to full thrust. Moon trips: the arrival law starts at the planned share
        /// and uses the headroom at the end, finishing at full thrust (measured in the simulator: Tylo 0.92 for 0.85 planned,
        /// Laythe 0.88, the Mun to Kerbin at 2 g 0.95). Between planets the braking flies at its planned share but longer than
        /// the plan's timing, whose smooth gravity path misses the end of a deep well (Jool: 5,287 s against 4,774): the same
        /// extra cost. Counted so, planned and flown delta-v agree within about 1 % between planets and 2.6 to 5.1 % (flown
        /// under) for moon coast trips.
        /// </summary>
        public static double BrakingFlownShare(double plannedShare) =>
            plannedShare < BrakingFraction - 1e-9 ? (plannedShare + 1) / 2 : plannedShare;

        /// <summary>
        /// What the braking costs per second of it, as a share of the chosen acceleration's delta-v: its delta-v share
        /// (<see cref="BrakingFlownShare"/>) times what each m/s costs on an engine that loses Isp at part throttle
        /// (<see cref="ShipPerformance.ThrottleCost"/>), at the throttle it is really flown at: on a moon trip the arrival law
        /// rises from the planned share to full thrust, halfway on average; between planets it holds the planned share, for
        /// longer than planned (measured: Tylo 0.92 for 0.85 planned; Jool at 1 g 0.850, Duna at 0.4 g 0.853). Without it the
        /// Kerbstein drive's braking at 85 % burned 8.5 % more than counted, and the window showed 2.8 points more reserve
        /// than the 0.4 g trip to Duna arrived with (in-game run 9).
        /// </summary>
        public static double BrakingCost(double plannedShare, double ispThrottleExponent, bool planetSystemTrip)
        {
            double flown = BrakingFlownShare(plannedShare);
            return flown * ShipPerformance.ThrottleCost(planetSystemTrip ? flown : plannedShare, ispThrottleExponent);
        }

        /// <summary>The braking share for a trip to <paramref name="target"/>'s parking orbit at <paramref name="acceleration"/>.</summary>
        public static double BrakingFractionFor(TargetPlanet target, double parkingAltitude, double acceleration)
        {
            if (target == null) return BrakingFraction;
            double r = target.Radius + parkingAltitude;
            return r > 0 ? BrakingFractionFor(target.GravParameter / (r * r), acceleration) : BrakingFraction;
        }

        /// <summary>
        /// Flying a coast trip costs more delta-v than its two planned burns: steering corrections, the braking law
        /// holding 98-100 % of the acceleration, the last circularizing burn, and gravity the plan models only roughly
        /// (the target's well near arrival). The simulator measured 1.3 % extra to Duna and 4.2 % to Jool, so the burns
        /// are planned to fit the budget less 6 %, and the planner reports the delta-v needed with this margin included.
        /// That keeps the 10 % reserve really there at arrival.
        /// </summary>
        public const double GuidanceMargin = 0.06;

        /// <summary>
        /// A coast trip down to the planet a moon trip is centred on needs at least this many times the planet's pull at the
        /// parking orbit (ADR 0005). The coast speeds up all the way down the planet's well and leaves the braking to the end,
        /// where the pull is strongest: at 1 g from the Mun to Kerbin's 80 km orbit (1.29 times the pull there) the braking
        /// couldn't keep up and every simulated trip arrived 5 km low after a long last burn; at 2 g it arrived within 0.3 %.
        /// Other trips need 1.1 times (<see cref="TripCalculator.TargetGravityMargin"/>).
        /// </summary>
        public const double CentralTargetGravityMargin = 1.5;

        public static CoastPlan Plan(TripRequest request, double deltaVBudget)
        {
            // A moon trip leaving the planet: plan from the departure window (ADR 0005), the wait added to the trip.
            if (request != null && request.PlanetSystemTrip && request.WaitForDepartureWindow && request.Departure != null && request.Target != null)
            {
                double wait = DepartureWindow.Find(request, out StateVector s);
                if (wait > 1)
                {
                    TripRequest later = Copy(request);
                    later.ShipPosition = s.Position;
                    later.ShipVelocity = s.Velocity;
                    later.StartTime = request.StartTime + wait;
                    later.WaitForDepartureWindow = false;
                    CoastPlan fromWindow = Plan(later, deltaVBudget);
                    if (!fromWindow.Feasible) return fromWindow;
                    fromWindow.DepartureWait = wait;
                    fromWindow.TripTime += wait;
                    return fromWindow;
                }
            }

            var result = new CoastPlan { Budget = deltaVBudget };
            if (request?.Target == null || !(request.Acceleration > 0) || !(deltaVBudget > 0))
            {
                result.Problem = "invalid input";
                return result;
            }

            if (request.PlanetSystemTrip && request.Target.IsCentralBody && request.CheckTargetGravity)
            {
                double rp = request.Target.Radius + request.ParkingAltitude;
                double minimum = CentralTargetGravityMargin * request.Target.GravParameter / (rp * rp);
                if (request.Acceleration < minimum)
                {
                    result.MinimumAcceleration = minimum;
                    result.Problem = "the braking can't beat the planet's pull near the end";
                    return result;
                }
            }

            double a1 = request.Acceleration, a2 = request.Acceleration * BrakingFractionFor(request.Target, request.ParkingAltitude, request.Acceleration);
            // Plan the burns a margin under the budget: flying them costs a little more (see GuidanceMargin).
            double burnBudget = deltaVBudget / (1 + GuidanceMargin);
            double parkingRadius = request.Target.Radius + request.ParkingAltitude;
            PathGravity gravity = request.PlanetSystemTrip ? SystemTrip.GravityFor(request)
                : new PathGravity(request.Target.Ephemeris, request.Target.GravParameter, request.SunGravParameter, true);
            Vec3 wellV = Vec3.Zero, wellP = Vec3.Zero;

            StateVector EndState(double arrivalUT)
            {
                StateVector planet = request.Target.Ephemeris.StateAt(arrivalUT);
                StateVector rel = ParkingArrival.Relative(request.Target, arrivalUT, request.ShipPosition, parkingRadius, request.North);
                return new StateVector(planet.Position + rel.Position - wellP, planet.Velocity + rel.Velocity - wellV);
            }

            var solver = new TwoArcSolver(EndState, a1, a2, request.MaxTripTime);
            // The braking's delta-v at the share the guidance flies, at what it costs there (BrakingCost); its timing at the planned share.
            double a2Cost = request.Acceleration * BrakingCost(a2 / request.Acceleration, request.IspThrottleExponent, request.PlanetSystemTrip);
            double DeltaVFor(TwoArcSolver.Arcs arcs) => a1 * arcs.T1 + a2Cost * arcs.T2;

            TwoArcSolver.Arcs best = default;
            for (int pass = 0; pass < 6; pass++)
            {
                // Fastest trip that fits: the shortest coast whose burns use no more than the budget.
                TwoArcSolver.Arcs full = solver.Solve(request.StartTime, request.ShipPosition, request.ShipVelocity, 0);
                if (!full.Valid)
                {
                    result.Problem = "no trip found";
                    return result;
                }

                if (DeltaVFor(full) <= burnBudget)
                {
                    best = full;
                }
                else
                {
                    double lo = 0, hi = Math.Max(1000, full.Total);
                    TwoArcSolver.Arcs atHi = solver.Solve(request.StartTime, request.ShipPosition, request.ShipVelocity, hi);
                    while (!(atHi.Valid && DeltaVFor(atHi) <= burnBudget))
                    {
                        if (hi > request.MaxTripTime)
                        {
                            result.Problem = "even a long coast needs more delta-v than the budget";
                            return result;
                        }

                        lo = hi;
                        hi *= 2;
                        atHi = solver.Solve(request.StartTime, request.ShipPosition, request.ShipVelocity, hi);
                    }

                    best = atHi;
                    for (int i = 0; i < 60 && hi - lo > 1e-6 * hi; i++)
                    {
                        double mid = 0.5 * (lo + hi);
                        TwoArcSolver.Arcs m = solver.Solve(request.StartTime, request.ShipPosition, request.ShipVelocity, mid);
                        if (m.Valid && DeltaVFor(m) <= burnBudget)
                        {
                            hi = mid;
                            best = m;
                        }
                        else lo = mid;
                    }
                }

                // Gravity over the trip just planned; aim off by it and plan again.
                Allowance(request, gravity, parkingRadius, best, out wellV, out wellP);
            }

            result.Feasible = true;
            result.WellVelocity = wellV;
            result.WellPosition = wellP;
            result.BurnUpTime = best.T1;
            result.CoastTime = best.Tc;
            result.BurnDownTime = best.T2;
            result.TripTime = best.Total;
            result.ArrivalUT = request.StartTime + best.Total;
            result.DeltaV = DeltaVFor(best);
            return result;
        }

        private static TripRequest Copy(TripRequest r) => new TripRequest
        {
            ShipPosition = r.ShipPosition, ShipVelocity = r.ShipVelocity, StartTime = r.StartTime, Target = r.Target, Departure = r.Departure,
            Acceleration = r.Acceleration, ParkingAltitude = r.ParkingAltitude, SunGravParameter = r.SunGravParameter,
            SunSafetyDistance = r.SunSafetyDistance, MaxTripTime = r.MaxTripTime, North = r.North, CheckTargetGravity = r.CheckTargetGravity,
            PlanSunDetour = r.PlanSunDetour, PlanetSystemTrip = r.PlanetSystemTrip, DetourClearance = r.DetourClearance,
            Obstacles = r.Obstacles, WaitForDepartureWindow = r.WaitForDepartureWindow, IspThrottleExponent = r.IspThrottleExponent,
        };

        /// <summary>
        /// The fastest coast trip that still keeps the reserve when propellant is lost while the engines aren't firing
        /// (boil-off, ADR 0002): the loss over the whole trip time costs delta-v, so the burns get less of the budget. A
        /// slower trip loses more, so the trip time and the loss are settled together.
        /// </summary>
        /// <param name="ship">The ship's engines and propellant, to turn the loss into delta-v.</param>
        /// <param name="lossKgPerSecond">The measured background loss.</param>
        /// <param name="lossDeltaV">The delta-v the loss costs over the trip planned (m/s).</param>
        public static CoastPlan PlanAllowingForLoss(TripRequest request, double deltaVBudget, ShipPerformance ship, double lossKgPerSecond,
            out double lossDeltaV)
        {
            lossDeltaV = 0;
            CoastPlan plan = Plan(request, deltaVBudget);
            if (ship == null || !(lossKgPerSecond > 0) || !plan.Feasible) return plan;
            for (int i = 0; i < 6; i++)
            {
                double cost = BackgroundLoss.DeltaVCost(ship, request.Acceleration, lossKgPerSecond * plan.TripTime);
                CoastPlan next = Plan(request, deltaVBudget - cost);
                lossDeltaV = cost;
                if (!next.Feasible) return next;
                bool settled = Math.Abs(next.TripTime - plan.TripTime) < 1e-3 * plan.TripTime;
                plan = next;
                if (settled) break;
            }

            plan.Budget = deltaVBudget;
            return plan;
        }

        /// <summary>
        /// The burn-coast-burn trip that arrives at <paramref name="arrivalUT"/>, and whether its burns fit the budget the
        /// way <see cref="Plan"/>'s do (the guidance margin under it). A resumed trip keeps flying to the arrival it was
        /// already flying to when this fits (ADR 0004): a fresh "fastest" plan from part-way through the trip can land up
        /// to about 1 % either side of the one being flown, which would move the arrival with no time away at all.
        /// </summary>
        public static CoastPlan PlanArrivingAt(TripRequest request, double deltaVBudget, double arrivalUT)
        {
            var result = new CoastPlan { Budget = deltaVBudget };
            double total = request == null ? 0 : arrivalUT - request.StartTime;
            if (request?.Target == null || !(request.Acceleration > 0) || !(deltaVBudget > 0) || !(total > 0))
            {
                result.Problem = "invalid input";
                return result;
            }

            double a1 = request.Acceleration, a2 = request.Acceleration * BrakingFractionFor(request.Target, request.ParkingAltitude, request.Acceleration);
            double burnBudget = deltaVBudget / (1 + GuidanceMargin);
            double parkingRadius = request.Target.Radius + request.ParkingAltitude;
            PathGravity gravity = request.PlanetSystemTrip ? SystemTrip.GravityFor(request)
                : new PathGravity(request.Target.Ephemeris, request.Target.GravParameter, request.SunGravParameter, true);
            Vec3 wellV = Vec3.Zero, wellP = Vec3.Zero;

            StateVector EndState(double ut)
            {
                StateVector planet = request.Target.Ephemeris.StateAt(ut);
                StateVector rel = ParkingArrival.Relative(request.Target, ut, request.ShipPosition, parkingRadius, request.North);
                return new StateVector(planet.Position + rel.Position - wellP, planet.Velocity + rel.Velocity - wellV);
            }

            var solver = new TwoArcSolver(EndState, a1, a2, request.MaxTripTime);
            TwoArcSolver.Arcs best = default;
            for (int pass = 0; pass < 6; pass++)
            {
                best = solver.SolveFixedTotal(request.StartTime, request.ShipPosition, request.ShipVelocity, total);
                if (!best.Valid)
                {
                    result.Problem = "no trip arrives then";
                    return result;
                }

                Allowance(request, gravity, parkingRadius, best, out wellV, out wellP);
            }

            result.BurnUpTime = best.T1;
            result.CoastTime = best.Tc;
            result.BurnDownTime = best.T2;
            result.TripTime = best.Total;
            result.ArrivalUT = request.StartTime + best.Total;
            result.DeltaV = a1 * best.T1 + request.Acceleration * BrakingCost(a2 / request.Acceleration, request.IspThrottleExponent, request.PlanetSystemTrip) * best.T2;
            result.WellVelocity = wellV;
            result.WellPosition = wellP;
            result.Feasible = result.DeltaV <= burnBudget && best.Tc >= 0;
            if (!result.Feasible) result.Problem = "arriving then needs more delta-v than the budget";
            return result;
        }

        // Gravity over a planned trip, along its burn-coast-burn path (the coast on its orbit around the Sun): the target's
        // well, the Sun's pull relative to it, the target's own curve around the Sun, and the departure planet's pull while
        // leaving it. The two-arc solve aims off by it.
        private static void Allowance(TripRequest request, PathGravity gravity, double parkingRadius, TwoArcSolver.Arcs best, out Vec3 wellV, out Vec3 wellP)
        {
            StateVector planetNow = request.Target.Ephemeris.StateAt(request.StartTime);
            if (request.PlanetSystemTrip)
            {
                // A moon trip (ADR 0005): along the path the burns and the coast really fly, the moon being left included.
                gravity.IntegratePlanned(request.StartTime, request.ShipPosition - planetNow.Position, request.ShipVelocity - planetNow.Velocity,
                    best.A1, best.T1, best.Tc, best.A2, best.T2, out Vec3 sg1, out Vec3 sg2);
                gravity.FrameCurve(request.StartTime, best.Total, out Vec3 sp, out Vec3 sv);
                wellV = sg1 + sv;
                wellP = sg2 + sp;
                return;
            }

            StateVector end = ParkingArrival.Relative(request.Target, request.StartTime + best.Total, request.ShipPosition, parkingRadius, request.North);
            gravity.Integrate(request.StartTime,
                gravity.CoastPath(request.StartTime, request.ShipPosition - planetNow.Position, request.ShipVelocity - planetNow.Velocity,
                    best.T1, best.A1, best.Tc, best.T2, end.Position, end.Velocity),
                best.Total, out Vec3 g1, out Vec3 g2, PathGravity.CoastSamples);
            gravity.FrameCurve(request.StartTime, best.Total, out Vec3 curveP, out Vec3 curveV);

            DepartureGravity.Effect(request.Departure, request.StartTime, request.ShipPosition, request.ShipVelocity,
                best.A1, best.T1, best.Total, out Vec3 depV, out Vec3 depP);
            wellV = g1 + curveV + depV;
            wellP = g2 + curveP + depP;
        }
    }
}
