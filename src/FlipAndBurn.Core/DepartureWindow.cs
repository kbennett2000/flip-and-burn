using System;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// Leaving the planet a moon trip is centred on (ADR 0005): when the target is behind the planet, a burn now would aim
    /// through it. Instead the ship waits in its orbit, engine off, until it has come round to the target's side, and
    /// burns from there. Waiting costs no fuel; at most one orbit.
    /// </summary>
    public static class DepartureWindow
    {
        /// <summary>Candidate departure times tried per orbit.</summary>
        public const int StepsPerOrbit = 180;

        /// <summary>
        /// How long to wait (s) before burning, from the request's start: 0 when the trip can start now. The ship is
        /// in orbit around the planet at the centre (positions relative to it); <paramref name="planetMu"/> is its
        /// gravitational parameter and <paramref name="safeRadius"/> the top of its atmosphere (or highest terrain).
        /// The trip is clear when its planned path never comes closer to the planet than where the burn starts (a ship in a
        /// low orbit), or than 1.2 times the safe radius (a ship already well above it), and the burn starts climbing: the
        /// upward part of the thrust is at least the planet's pull there. (A burn along the orbit toward a target ahead or
        /// behind looks clear as a straight line, but slowing the orbit drops the ship into the planet.)
        /// </summary>
        public static double Find(TripRequest request, double planetMu, double safeRadius, out StateVector atDeparture)
        {
            atDeparture = new StateVector(request.ShipPosition, request.ShipVelocity);
            if (request.Target == null || !(planetMu > 0) || !(safeRadius > 0)) return 0;
            OrbitShape shape = TwoBody.Shape(request.ShipPosition, request.ShipVelocity, planetMu);
            if (!(shape.Eccentricity < 1)) return 0;   // not in orbit: nothing to wait for
            double a = shape.SemiMajorAxis;
            double period = 2 * Math.PI * Math.Sqrt(a * a * a / planetMu);
            for (int i = 0; i <= StepsPerOrbit; i++)
            {
                double wait = period * i / StepsPerOrbit;
                StateVector s = i == 0 ? atDeparture : TwoBody.Propagate(request.ShipPosition, request.ShipVelocity, planetMu, wait);
                if (Clear(request, s, request.StartTime + wait, safeRadius, planetMu))
                {
                    atDeparture = s;
                    return wait;
                }
            }

            return 0;   // nowhere clear within an orbit (shouldn't happen): leave it to the detour
        }

        /// <summary>True when the straight trip from <paramref name="s"/> at <paramref name="ut"/> stays clear of the planet.</summary>
        public static bool Clear(TripRequest request, StateVector s, double ut, double safeRadius, double planetMu)
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
            double start = s.Position.Length;
            double floor = Math.Min(start, 1.2 * safeRadius) - 1000;
            if (plan.ClosestSunDistance < floor) return false;
            if (!(plan.TripTime > 0)) return true;
            Vec3 thrust = plan.Profile.C + plan.Profile.E * plan.Profile.Ab;
            double up = Vec3.Dot(thrust, s.Position / start);
            return up >= planetMu / (start * start);
        }
    }
}
