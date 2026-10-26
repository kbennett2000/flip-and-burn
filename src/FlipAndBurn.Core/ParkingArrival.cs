using System;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// Where a trip ends (ADR 0003): on the target's circular parking orbit, in its equatorial plane, prograde, at the
    /// point where that orbit runs in the ship's direction of travel. Shared by the planner and the guidance.
    /// </summary>
    public static class ParkingArrival
    {
        public static readonly Vec3 DefaultNorth = new Vec3(0, 0, 1);

        /// <summary>
        /// Direction from the planet to the arrival point, for a ship travelling along <paramref name="towardPlanet"/>:
        /// the travel direction flattened onto the equator, crossed with north. There the prograde orbit's velocity,
        /// north x side, equals the travel direction.
        /// </summary>
        public static Vec3 Side(Vec3 towardPlanet, Vec3 north)
        {
            Vec3 n = north.Normalized();
            Vec3 travel = towardPlanet - n * Vec3.Dot(towardPlanet, n);
            if (travel.Length < 1e-9 * Math.Max(1, towardPlanet.Length))
                travel = Vec3.Cross(n, new Vec3(1, 0, 0)).Length > 0.1 ? Vec3.Cross(n, new Vec3(1, 0, 0)) : Vec3.Cross(n, new Vec3(0, 1, 0));
            return Vec3.Cross(travel.Normalized(), n).Normalized();
        }

        public static Vec3 OrbitVelocity(Vec3 side, Vec3 north, double mu, double radius)
            => Vec3.Cross(north.Normalized(), side).Normalized() * Math.Sqrt(mu / radius);

        /// <summary>The arrival state relative to the planet, for a ship now at <paramref name="shipPosition"/>.</summary>
        public static StateVector Relative(TargetPlanet target, double arrivalUT, Vec3 shipPosition, double parkingRadius, Vec3 north)
        {
            Vec3 planet = target.Ephemeris.StateAt(arrivalUT).Position;
            Vec3 side = Side(planet - shipPosition, north);
            return new StateVector(side * parkingRadius, OrbitVelocity(side, north, target.GravParameter, parkingRadius));
        }
    }
}
