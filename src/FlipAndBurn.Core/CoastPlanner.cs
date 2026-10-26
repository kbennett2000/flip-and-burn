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
        /// <summary>Delta-v of the two planned burns, gravity included (m/s).</summary>
        public double DeltaV { get; internal set; }
        public double Budget { get; internal set; }
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

        /// <summary>
        /// Flying a coast trip costs more delta-v than its two planned burns: steering corrections, the braking law
        /// holding 98-100 % of the acceleration, the last circularizing burn, and gravity the plan models only roughly
        /// (the target's well near arrival). The simulator measured 1.3 % extra to Duna and 4.2 % to Jool, so the burns
        /// are planned to fit the budget less 6 %, and the planner reports the delta-v needed with this margin included.
        /// That keeps the 10 % reserve really there at arrival.
        /// </summary>
        public const double GuidanceMargin = 0.06;

        public static CoastPlan Plan(TripRequest request, double deltaVBudget)
        {
            var result = new CoastPlan { Budget = deltaVBudget };
            if (request?.Target == null || !(request.Acceleration > 0) || !(deltaVBudget > 0))
            {
                result.Problem = "invalid input";
                return result;
            }

            double a1 = request.Acceleration, a2 = request.Acceleration * BrakingFraction;
            // Plan the burns a margin under the budget: flying them costs a little more (see GuidanceMargin).
            double burnBudget = deltaVBudget / (1 + GuidanceMargin);
            double parkingRadius = request.Target.Radius + request.ParkingAltitude;
            var gravity = new PathGravity(request.Target.Ephemeris, request.Target.GravParameter, request.SunGravParameter, true);
            Vec3 wellV = Vec3.Zero, wellP = Vec3.Zero;

            StateVector EndState(double arrivalUT)
            {
                StateVector planet = request.Target.Ephemeris.StateAt(arrivalUT);
                StateVector rel = ParkingArrival.Relative(request.Target, arrivalUT, request.ShipPosition, parkingRadius, request.North);
                return new StateVector(planet.Position + rel.Position - wellP, planet.Velocity + rel.Velocity - wellV);
            }

            var solver = new TwoArcSolver(EndState, a1, a2, request.MaxTripTime);
            double DeltaVFor(TwoArcSolver.Arcs arcs) => a1 * arcs.T1 + a2 * arcs.T2;

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

                // Gravity over the trip just planned, along its burn-coast-burn path (the coast on its orbit around the Sun): the target's well, the Sun's pull
                // relative to it, the target's own curve around the Sun, and the departure planet's pull while leaving it. Aim off
                // by it and plan again.
                StateVector planetNow = request.Target.Ephemeris.StateAt(request.StartTime);
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

            result.Feasible = true;
            result.BurnUpTime = best.T1;
            result.CoastTime = best.Tc;
            result.BurnDownTime = best.T2;
            result.TripTime = best.Total;
            result.ArrivalUT = request.StartTime + best.Total;
            result.DeltaV = DeltaVFor(best);
            return result;
        }
    }
}
