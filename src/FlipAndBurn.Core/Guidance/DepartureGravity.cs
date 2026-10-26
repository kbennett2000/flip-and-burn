using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// What the departure planet's gravity takes away while the ship burns out of its sphere of influence.
    /// </summary>
    /// <remarks>
    /// A ship starting in low orbit doesn't keep its orbital speed, and climbing out of the well costs a little more.
    /// Over a burn of an hour or more that is a few km/s. The plan is solved without gravity, so this works out the
    /// difference gravity makes: fly the first burn (direction <c>A</c>, for <c>t1</c> seconds, then coasting) from the
    /// ship's state relative to the planet with the planet's pull on, until the ship leaves the sphere of influence,
    /// and compare with the same burn without the pull. The velocity difference then carries on to arrival, so by then
    /// the position difference has grown by that velocity times the time left.
    /// </remarks>
    public static class DepartureGravity
    {
        private const double Step = 2.0;  // s, RK4

        /// <param name="remaining">Seconds from now to arrival.</param>
        /// <param name="velocity">What gravity adds to the velocity at arrival (m/s).</param>
        /// <param name="position">What gravity adds to the position at arrival (m).</param>
        public static void Effect(TargetPlanet departure, double ut, Vec3 shipPosition, Vec3 shipVelocity, Vec3 burn, double t1,
            double remaining, out Vec3 velocity, out Vec3 position)
        {
            velocity = Vec3.Zero;
            position = Vec3.Zero;
            if (departure == null || !(departure.GravParameter > 0) || !(departure.SphereOfInfluence > 0)) return;
            StateVector planet = departure.Ephemeris.StateAt(ut);
            Vec3 r = shipPosition - planet.Position, v = shipVelocity - planet.Velocity;
            if (r.Length >= departure.SphereOfInfluence) return;

            double mu = departure.GravParameter;
            Vec3 Pull(Vec3 p) => p * (-mu / Math.Pow(p.Length, 3));
            Vec3 r0 = r, v0 = v;
            double t = 0;
            while (r.Length < departure.SphereOfInfluence && t < remaining)
            {
                double h = Math.Min(Step, remaining - t);
                Vec3 thrust = t < t1 ? burn : Vec3.Zero;
                Vec3 k1v = Pull(r) + thrust, k1r = v;
                Vec3 k2v = Pull(r + k1r * (h / 2)) + thrust, k2r = v + k1v * (h / 2);
                Vec3 k3v = Pull(r + k2r * (h / 2)) + thrust, k3r = v + k2v * (h / 2);
                Vec3 k4v = Pull(r + k3r * h) + thrust, k4r = v + k3v * h;
                r += (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6);
                v += (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6);
                t += h;
            }

            // The same burn without the pull.
            double tb = Math.Min(t, t1);
            Vec3 vFree = v0 + burn * tb;
            Vec3 rFree = r0 + v0 * t + burn * (0.5 * tb * tb) + burn * (tb * (t - tb));
            velocity = v - vFree;
            position = r - rFree + velocity * Math.Max(0, remaining - t);
        }
    }
}
