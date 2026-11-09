using System;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// Leaving a body on a moon trip (ADR 0005): the planet the trip is centred on, or a moon. When the target is behind
    /// that body, a burn now would aim through it. Instead the ship waits in its orbit, engine off, until it has come round
    /// to the target's side, and burns from there. Waiting costs no fuel; at most one orbit.
    /// </summary>
    public static class DepartureWindow
    {
        /// <summary>Candidate departure times tried per orbit.</summary>
        public const int StepsPerOrbit = 180;

        private const int PathSamples = 400;

        /// <summary>
        /// The lowest safe radius over the body being left: the planet at the centre's (the request's safety distance), or
        /// a moon's (its departure safe radius).
        /// </summary>
        public static double SafeRadiusOf(TripRequest request) =>
            request.Departure == null ? 0
            : request.Departure.IsCentralBody ? request.SunSafetyDistance
            : request.Departure.SafeRadius > 0 ? request.Departure.SafeRadius : request.Departure.Radius;

        /// <summary>
        /// How long to wait (s) before burning, from the request's start: 0 when the trip can start now. The trip is clear
        /// when its planned path never comes closer to the body being left than where the burn starts (a ship in a low
        /// orbit), or than 1.2 times the safe radius (a ship already well above it), and the burn starts climbing: the upward
        /// part of the thrust is at least the body's pull there. (A burn along the orbit toward a target ahead or behind
        /// looks clear as a straight line, but slowing the orbit drops the ship into the body.)
        /// </summary>
        public static double Find(TripRequest request, out StateVector atDeparture)
        {
            atDeparture = new StateVector(request.ShipPosition, request.ShipVelocity);
            TargetPlanet body = request.Departure;
            double safeRadius = SafeRadiusOf(request);
            if (request.Target == null || body == null || !(body.GravParameter > 0) || !(safeRadius > 0)) return 0;
            double mu = body.GravParameter, t0 = request.StartTime;
            StateVector b0 = body.Ephemeris.StateAt(t0);
            Vec3 r = request.ShipPosition - b0.Position, v = request.ShipVelocity - b0.Velocity;
            OrbitShape shape = TwoBody.Shape(r, v, mu);
            if (!(shape.Eccentricity < 1)) return 0;   // not in orbit: nothing to wait for
            double a = shape.SemiMajorAxis;
            double period = 2 * Math.PI * Math.Sqrt(a * a * a / mu);
            for (int i = 0; i <= StepsPerOrbit; i++)
            {
                double wait = period * i / StepsPerOrbit;
                StateVector rel = i == 0 ? new StateVector(r, v) : TwoBody.Propagate(r, v, mu, wait);
                StateVector b = body.Ephemeris.StateAt(t0 + wait);
                var s = new StateVector(b.Position + rel.Position, b.Velocity + rel.Velocity);
                if (Clear(request, s, t0 + wait, body, safeRadius))
                {
                    atDeparture = s;
                    return wait;
                }
            }

            return 0;   // nowhere clear within an orbit (shouldn't happen): leave it to the climb out and the detour
        }

        /// <summary>True when the straight trip from <paramref name="s"/> at <paramref name="ut"/> stays clear of <paramref name="body"/>.</summary>
        public static bool Clear(TripRequest request, StateVector s, double ut, TargetPlanet body, double safeRadius)
        {
            var from = new TripRequest
            {
                ShipPosition = s.Position, ShipVelocity = s.Velocity, StartTime = ut, Target = request.Target,
                Departure = request.Departure, Acceleration = request.Acceleration, ParkingAltitude = request.ParkingAltitude,
                SunGravParameter = 0, SunSafetyDistance = 0, MaxTripTime = request.MaxTripTime, North = request.North,
                CheckTargetGravity = false, PlanSunDetour = false,
            };
            TripPlan plan = TripCalculator.Plan(from);
            if (!plan.Feasible) return false;
            Vec3 rel = s.Position - body.Ephemeris.StateAt(ut).Position;
            double start = rel.Length;
            if (!(plan.TripTime > 0)) return true;
            double floor = Math.Min(start, 1.2 * safeRadius) - 1000;
            for (int i = 1; i <= PathSamples; i++)
            {
                double t = plan.TripTime * i / PathSamples;
                if ((plan.Profile.StateAt(t).Position - body.Ephemeris.StateAt(ut + t).Position).Length < floor) return false;
            }

            Vec3 thrust = plan.Profile.C + plan.Profile.E * plan.Profile.Ab;
            double up = Vec3.Dot(thrust, rel / start);
            return up >= body.GravParameter / (start * start);
        }
    }
}
