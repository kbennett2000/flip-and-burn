using System;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// The full-burn estimate for a trip among a planet's moons (ADR 0005). The planet's pull is a large share of the
    /// thrust near it (three quarters of 1 g in a low Kerbin orbit), so the gravity-free estimate is a long way off. This
    /// plans the way the guidance flies: wait for the departure window if the ship is leaving the planet, then the two burns
    /// solved again and again, each time aiming off by what gravity does along the path those burns really fly, until it
    /// settles. The same path is checked against the planet and every other moon.
    /// </summary>
    public static class SystemTrip
    {
        private const int Passes = 8;

        /// <summary>
        /// Flying the plan costs a little more than its two burns: the last burn into the orbit, and the braking held at
        /// 98-100 % of the acceleration. The simulator measured up to 1.5 % (Kerbin to the Mun), so the delta-v shown is the
        /// burns' plus 3 %.
        /// </summary>
        public const double GuidanceMargin = 0.03;

        /// <summary>
        /// Replace the plan's numbers with the gravity-aware ones. Returns false (the plan left as it is) when the path still
        /// crosses the planet or no plan settled, so the caller can plan a detour.
        /// </summary>
        public static bool Refine(TripRequest request, TripPlan plan)
        {
            if (!plan.Feasible || request.Target == null) return false;
            TargetPlanet dep = request.Departure;
            double wait = 0;
            var start = new StateVector(request.ShipPosition, request.ShipVelocity);
            if (dep != null && request.WaitForDepartureWindow)
                wait = DepartureWindow.Find(request, out start);

            double tDep = request.StartTime + wait;
            double a1 = request.Acceleration, a2 = a1 * CoastPlanner.BrakingFractionFor(request.Target, request.ParkingAltitude, a1);
            double parkingRadius = request.Target.Radius + request.ParkingAltitude;
            PathGravity gravity = GravityFor(request);
            Vec3 wellV = Vec3.Zero, wellP = Vec3.Zero;

            StateVector EndState(double arrivalUT)
            {
                StateVector planet = request.Target.Ephemeris.StateAt(arrivalUT);
                StateVector rel = ParkingArrival.Relative(request.Target, arrivalUT, start.Position, parkingRadius, request.North);
                return new StateVector(planet.Position + rel.Position - wellP, planet.Velocity + rel.Velocity - wellV);
            }

            var solver = new TwoArcSolver(EndState, a1, a2, request.MaxTripTime);
            StateVector frameNow = request.Target.Ephemeris.StateAt(tDep);
            Vec3 r = start.Position - frameNow.Position, v = start.Velocity - frameNow.Velocity;
            TwoArcSolver.Arcs arcs = default;
            for (int pass = 0; pass < Passes; pass++)
            {
                arcs = solver.Solve(tDep, start.Position, start.Velocity, 0);
                if (!arcs.Valid) return false;
                gravity.IntegratePlanned(tDep, r, v, arcs.A1, arcs.T1, arcs.Tc, arcs.A2, arcs.T2, out Vec3 g1, out Vec3 g2);
                gravity.FrameCurve(tDep, arcs.Total, out Vec3 cp, out Vec3 cv);
                wellV = g1 + cv;
                wellP = g2 + cp;
            }

            // The path those burns fly: how close it comes to the planet and the moons, and the speed at the flip.
            double closestPlanet = start.Position.Length;
            Vec3 atFlip = Vec3.Zero;
            plan.ClosestObstacle = null;
            plan.ClosestObstacleMargin = double.PositiveInfinity;
            gravity.IntegratePlanned(tDep, r, v, arcs.A1, arcs.T1, arcs.Tc, arcs.A2, arcs.T2, out _, out _, visit: (s, rel, vel) =>
            {
                StateVector f = request.Target.Ephemeris.StateAt(tDep + s);
                Vec3 p = f.Position + rel;
                closestPlanet = Math.Min(closestPlanet, p.Length);
                Track(request, plan, tDep + s, p);
                if (Math.Abs(s - arcs.T1) < 1e-6 * Math.Max(1, arcs.T1)) atFlip = f.Velocity + vel;
            });

            double floor = Math.Min(start.Position.Length, 1.2 * request.SunSafetyDistance) - 1000;
            if (request.SunSafetyDistance > 0 && closestPlanet < Math.Min(floor, request.SunSafetyDistance)) return false;

            // The braking costs more per m/s on an engine that loses Isp at part throttle, at the share the arrival law flies.
            double burns = a1 * arcs.T1 + a2 * arcs.T2 * ShipPerformance.ThrottleCost(CoastPlanner.BrakingFlownShare(a2 / a1), request.IspThrottleExponent);
            Vec3 depVelocity = dep == null || dep.IsCentralBody ? Vec3.Zero : dep.Ephemeris.StateAt(tDep + arcs.T1).Velocity;
            StateVector target = request.Target.Ephemeris.StateAt(tDep + arcs.Total);
            plan.GravityIncluded = true;
            plan.DepartureWait = wait;
            plan.TripTime = wait + arcs.Total;
            plan.TimeToFlip = wait + arcs.T1;
            plan.ArrivalTime = request.StartTime + plan.TripTime;
            plan.AccelerationBeforeFlip = a1;
            plan.AccelerationAfterFlip = a2;
            plan.BurnDeltaV = burns * (1 + GuidanceMargin);
            plan.ParkingOrbitDeltaV = 0;   // the braking ends moving along the parking orbit
            plan.TotalDeltaV = plan.BurnDeltaV;
            plan.PeakSpeed = (atFlip - depVelocity).Length;
            plan.TargetPositionAtArrival = target.Position;
            plan.TargetVelocityAtArrival = target.Velocity;
            plan.AimPoint = target.Position + ParkingArrival.Relative(request.Target, tDep + arcs.Total, start.Position, parkingRadius, request.North).Position;
            plan.ClosestSunDistance = closestPlanet;
            plan.PassesNearSun = false;
            plan.UsesSunDetour = false;
            if (plan.ClosestObstacleMargin < 0)
            {
                plan.Problem = TripProblem.ObstacleInTheWay;
                plan.ProblemDetail = $"the path passes {plan.ClosestObstacle}";
            }

            return true;
        }

        /// <summary>
        /// Moon trips without the gravity-aware estimate (a detour): check the planned straight legs against the moons.
        /// </summary>
        public static void CheckObstacles(TripRequest request, TripPlan plan)
        {
            if (!plan.Feasible || plan.UsesSunDetour || plan.TripTime <= 0 || request.Obstacles == null || request.Obstacles.Count == 0) return;
            const int samples = 400;
            for (int i = 0; i <= samples; i++)
            {
                double s = plan.Profile.T * i / samples;
                Track(request, plan, request.StartTime + s, plan.Profile.StateAt(s).Position);
            }

            if (plan.ClosestObstacleMargin < 0)
            {
                plan.Problem = TripProblem.ObstacleInTheWay;
                plan.ProblemDetail = $"the path passes {plan.ClosestObstacle}";
            }
        }

        // The closest the path comes to any moon in the way, above its safe radius.
        private static void Track(TripRequest request, TripPlan plan, double ut, Vec3 position)
        {
            if (request.Obstacles == null) return;
            foreach (Obstacle o in request.Obstacles)
            {
                double margin = (position - o.Ephemeris.StateAt(ut).Position).Length - o.SafeRadius;
                if (margin < plan.ClosestObstacleMargin)
                {
                    plan.ClosestObstacleMargin = margin;
                    plan.ClosestObstacle = o.Name;
                }
            }
        }

        /// <summary>Gravity for a moon trip: the target's well, the planet's pull, and the moon being left.</summary>
        public static PathGravity GravityFor(TripRequest request)
        {
            var gravity = new PathGravity(request.Target.Ephemeris, request.Target.GravParameter, request.SunGravParameter, true);
            if (request.Departure != null && !request.Departure.IsCentralBody)
                gravity.AddBody(request.Departure.Ephemeris, request.Departure.GravParameter);
            return gravity;
        }
    }
}
