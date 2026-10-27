using System;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core
{
    public enum ReplanProblem
    {
        None,
        /// <summary>No way to reach the target from here at this acceleration.</summary>
        NoWayToTarget,
        /// <summary>The acceleration can't beat the target's gravity at the parking orbit.</summary>
        TargetGravityTooStrong,
        /// <summary>The engines can't reach the acceleration.</summary>
        CantHoldAcceleration,
        /// <summary>Not enough delta-v left to finish (a full burn), or for a coast trip from here.</summary>
        NotEnoughDeltaV,
        /// <summary>Coast trips: the Sun is between the ship and the target, and a coast trip can't go around it yet.</summary>
        SunInTheWay,
    }

    /// <summary>Can the rest of a trip be flown from where the ship is now, and how long will it take?</summary>
    public sealed class ReplanResult
    {
        public bool Feasible => Problem == ReplanProblem.None;
        public ReplanProblem Problem { get; internal set; }
        /// <summary>From now to arrival (s).</summary>
        public double RemainingTime { get; internal set; }
        /// <summary>The coast budget the new plan uses (m/s); for a full burn, the delta-v the rest of the trip needs.</summary>
        public double DeltaV { get; internal set; }
        /// <summary>What the ship has for it (m/s): the budget left (coast trips) or the ship's delta-v (full burn). NaN if not checked.</summary>
        public double DeltaVAvailable { get; internal set; } = double.NaN;
        /// <summary>The lowest acceleration that beats the target's gravity at the parking orbit, when that is the problem.</summary>
        public double MinimumAcceleration { get; internal set; }
        public double MaxAcceleration { get; internal set; }
        public TripPlan Plan { get; internal set; }
        public CoastPlan Coast { get; internal set; }
        /// <summary>Coast trips: the trip keeps the arrival it was already flying to (see <see cref="TripReplanner.KeepReserveFloor"/>).</summary>
        public bool KeptArrival { get; internal set; }
        /// <summary>The delta-v the trip started with (m/s); NaN when fuel isn't counted.</summary>
        public double StartDeltaV { get; internal set; } = double.NaN;
        /// <summary>The delta-v expected to be left at arrival (m/s), the burns' guidance margin spent; NaN when not counted.</summary>
        public double ReserveAtArrival { get; internal set; } = double.NaN;
        /// <summary>Coast trips: the reserve expected if the arrival being flown to were kept (whether or not it was).</summary>
        public double ReserveIfKept { get; internal set; } = double.NaN;
        /// <summary>The reserve at arrival as a share of the delta-v the trip started with; NaN when not counted.</summary>
        public double ReserveShare => StartDeltaV > 0 ? ReserveAtArrival / StartDeltaV : double.NaN;
    }

    /// <summary>
    /// Re-plans the rest of a trip from the ship's state now: after the player comes back to a paused trip (ADR 0004), or
    /// when the engines can't hold the acceleration (ADR 0002). It applies the same tests the planner applies before
    /// Start, so a trip that couldn't be started from here isn't carried on from here either.
    /// </summary>
    public static class TripReplanner
    {
        /// <summary>
        /// A coast trip keeps the arrival it was flying to only if the ship is still expected to arrive with at least this
        /// share of the delta-v the trip started with: the 10 % reserve less a sliver. A fresh fastest plan from part-way
        /// through a trip lands up to about 1 % either side of the one being flown, with no time away at all (the
        /// simulator: +0.53 %, -0.98 %, +0.32 % after 65 s, 300 s and 1,800 s of a Bebop coast trip); keeping the arrival
        /// then took 100.4-100.6 % of the budget left, an expected reserve of about 9.5 %. A real absence of hours or days
        /// needs far more, and the trip is re-planned instead, keeping the full 10 %.
        /// </summary>
        public const double KeepReserveFloor = 0.09;

        /// <summary>Without a known starting delta-v (infinite propellant): keep the arrival if it needs at most this share over the budget left.</summary>
        public const double KeepTolerance = 0.01;

        /// <param name="request">The ship's state now, the target and the acceleration to fly.</param>
        /// <param name="trip">Full burn or coast.</param>
        /// <param name="budgetLeft">The delta-v left of the budget set at Start (m/s): the budget less what has been burned.</param>
        /// <param name="ship">The ship's engines and fuel now, for the delta-v and acceleration tests; null skips them.</param>
        /// <param name="reserve">Share of the ship's delta-v kept in reserve (the planner keeps 10 %).</param>
        /// <param name="keepArrivalUT">Coast trips: the arrival being flown to, kept if it still fits (NaN: plan the fastest).</param>
        /// <param name="startDeltaV">The ship's delta-v when the trip started (m/s), for the reserve at arrival; NaN if not counted.</param>
        public static ReplanResult Check(TripRequest request, TripType trip, double budgetLeft, ShipPerformance ship, double reserve,
            double keepArrivalUT = double.NaN, double startDeltaV = double.NaN)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var result = new ReplanResult { StartDeltaV = startDeltaV };
            TripPlan plan = TripCalculator.Plan(request);
            result.Plan = plan;
            if (!plan.Feasible)
            {
                result.Problem = plan.Problem == TripProblem.TargetGravityTooStrong ? ReplanProblem.TargetGravityTooStrong : ReplanProblem.NoWayToTarget;
                result.MinimumAcceleration = plan.MinimumAcceleration;
                return result;
            }

            double a = request.Acceleration;
            if (ship != null)
            {
                result.MaxAcceleration = ship.MaxAcceleration;
                if (!ship.HasThrust || a > ship.MaxAcceleration)
                {
                    result.Problem = ReplanProblem.CantHoldAcceleration;
                    return result;
                }
            }

            // The delta-v the ship has for the rest of the trip, reserve included: what the trip started with less what it
            // has burned (the reserve plus the budget left), and no more than the ship's own delta-v now.
            double have = startDeltaV > 0 ? reserve * startDeltaV + Math.Max(0, budgetLeft) : double.NaN;
            if (ship != null) have = double.IsNaN(have) ? ship.DeltaVAtAcceleration(a) : Math.Min(have, ship.DeltaVAtAcceleration(a));
            double ReserveIf(double needed) => startDeltaV > 0 && !double.IsNaN(have) ? have - needed : double.NaN;

            if (trip == TripType.CoastInTheMiddle)
            {
                if (plan.UsesSunDetour)
                {
                    result.Problem = ReplanProblem.SunInTheWay;
                    return result;
                }

                // The budget left from Start keeps the reserve set then; the ship's own delta-v now is the other limit.
                double budget = Math.Max(0, budgetLeft);
                if (ship != null) budget = Math.Min(budget, (1 - reserve) * ship.DeltaVAtAcceleration(a));
                // The arrival being flown to, if it still fits without eating more than a sliver of the reserve: a short
                // absence then changes nothing.
                if (keepArrivalUT > request.StartTime)
                {
                    CoastPlan keep = CoastPlanner.PlanArrivingAt(request, budget, keepArrivalUT);
                    double reserveIfKept = ReserveIf(keep.DeltaVNeeded);
                    bool fits = startDeltaV > 0
                        ? reserveIfKept >= KeepReserveFloor * startDeltaV
                        : keep.DeltaVNeeded <= budget * (1 + KeepTolerance);
                    result.ReserveIfKept = reserveIfKept;
                    if (keep.TripTime > 0 && keep.CoastTime >= 0 && fits)
                    {
                        result.Coast = keep;
                        result.KeptArrival = true;
                        result.DeltaV = keep.DeltaVNeeded;
                        result.DeltaVAvailable = budget;
                        result.RemainingTime = keep.TripTime;
                        result.ReserveAtArrival = reserveIfKept;
                        return result;
                    }
                }

                CoastPlan coast = CoastPlanner.Plan(request, budget);
                result.Coast = coast;
                result.DeltaV = coast.DeltaVNeeded;
                result.DeltaVAvailable = budget;
                if (!coast.Feasible)
                {
                    result.Problem = ReplanProblem.NotEnoughDeltaV;
                    return result;
                }

                result.RemainingTime = coast.TripTime;
                result.ReserveAtArrival = ReserveIf(coast.DeltaVNeeded);
                return result;
            }

            result.DeltaV = plan.TotalDeltaV;
            if (ship != null)
            {
                TripReadiness readiness = TripReadiness.Check(plan, ship, a);
                result.DeltaVAvailable = readiness.DeltaVAvailable;
                if (!readiness.HasEnoughDeltaV)
                {
                    result.Problem = ReplanProblem.NotEnoughDeltaV;
                    return result;
                }
            }

            result.RemainingTime = plan.TripTime;
            result.ReserveAtArrival = ReserveIf(plan.TotalDeltaV);
            return result;
        }
    }
}
