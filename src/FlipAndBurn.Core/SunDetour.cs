using System;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// The detour around the Sun (ADR 0003): when the straight trip passes within the Sun safety distance, fly to a
    /// waypoint beside the Sun, stop there, then fly a normal trip. Shared by the planner (estimate) and the guidance.
    /// </summary>
    public static class SunDetour
    {
        public const double DefaultMargin = 1.2;

        /// <summary>
        /// Plan the detour from the request's start. Returns the waypoint and both legs' estimates, or null if the
        /// detour can't be planned.
        /// </summary>
        public static Vec3? Plan(TripRequest request, double straightTripTime, double margin, Vec3 north, out TripPlan leg1, out TripPlan leg2)
        {
            leg1 = leg2 = null;
            double clear = request.SunSafetyDistance * margin;
            double arrival = request.StartTime + straightTripTime;
            Vec3 waypoint = default;
            bool ok = false;
            for (int iteration = 0; iteration < 4; iteration++)
            {
                Vec3 end = request.Target.Ephemeris.StateAt(arrival).Position;
                waypoint = Waypoint(request.ShipPosition, end, clear, north);

                // Re-estimate the arrival time with the detour, then place the waypoint again.
                TripPlan l1 = TripCalculator.Plan(new TripRequest
                {
                    ShipPosition = request.ShipPosition, ShipVelocity = request.ShipVelocity, StartTime = request.StartTime,
                    Target = new TargetPlanet("waypoint", new FixedEphemeris(waypoint), 1, 1), Acceleration = request.Acceleration,
                    SunSafetyDistance = 0, CheckTargetGravity = false,
                });
                if (!l1.Feasible) break;
                TripPlan l2 = TripCalculator.Plan(new TripRequest
                {
                    ShipPosition = waypoint, ShipVelocity = Vec3.Zero, StartTime = request.StartTime + l1.TripTime, Target = request.Target,
                    Acceleration = request.Acceleration, ParkingAltitude = request.ParkingAltitude, SunSafetyDistance = 0,
                    SunGravParameter = request.SunGravParameter,
                });
                if (!l2.Feasible) break;
                leg1 = l1;
                leg2 = l2;
                ok = true;
                arrival = request.StartTime + l1.TripTime + l2.TripTime;
            }

            return ok ? waypoint : (Vec3?)null;
        }

        /// <summary>A point off to the side of the Sun such that both straight legs stay at least <paramref name="clear"/> from it.</summary>
        public static Vec3 Waypoint(Vec3 start, Vec3 end, double clear, Vec3 north)
        {
            Vec3 line = (end - start).Normalized();
            // The side to pass on: away from the Sun, perpendicular to the line, in the plane of start, end and Sun.
            Vec3 closest = start + line * (-Vec3.Dot(start, line));
            Vec3 side = closest.Length > 1e-6 * start.Length
                ? closest.Normalized()
                : Vec3.Cross(north.Normalized(), line).Normalized();

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
