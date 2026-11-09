using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// Leaving the departure planet: where to point, and whether to fire, until the line toward the target clears it. The
    /// guidance flies this, and the coast planner's departure model (<see cref="DepartureGravity"/>) flies the same, so the
    /// plan counts what the climb-out costs.
    /// </summary>
    public static class ClimbOut
    {
        /// <summary>Trips between planets: the line burned along while climbing out clears the safe height by this factor.</summary>
        public const double Margin = 1.1;

        /// <summary>
        /// Trips between planets: burn along a clear line only while it points within this angle of the target line (its
        /// cosine, 45 degrees). Further round, half or more of each km/s burned would have to be taken off again, so the ship
        /// coasts on its way out until the line comes round.
        /// </summary>
        public const double UsefulCosine = 0.7071;

        /// <summary>
        /// True while still climbing out. <paramref name="direction"/> is where to point; <paramref name="fire"/> whether the
        /// engine fires.
        /// </summary>
        /// <param name="r">The ship's position relative to the departure planet.</param>
        /// <param name="v">Its velocity relative to the planet.</param>
        /// <param name="aim">Unit vector: where the plan wants to burn.</param>
        /// <param name="planetSystemTrip">A moon trip (ADR 0005): leaving from the departure window, already climbing.</param>
        /// <param name="acceleration">The ship's acceleration (m/s^2).</param>
        public static bool Steer(Vec3 r, Vec3 v, double mu, double safeRadius, Vec3 aim, double acceleration, bool planetSystemTrip, out Vec3 direction, out bool fire)
        {
            direction = aim;
            fire = true;
            // Leaving the planet a moon trip is centred on: getting clear of it is enough (escaping it would leave its system).
            // Moon trips leave from the departure window, already climbing: only the line toward the target matters.
            bool escaping = planetSystemTrip || v.LengthSquared / 2 - mu / r.Length >= 0;
            double safe = safeRadius;
            // Moon trips (ADR 0005): a line skimming the moon's safe height still bends into it under the moon's pull, so it
            // must pass 1.2 times that height; until then the ship keeps climbing along its orbit.
            if (planetSystemTrip) safe *= 1.2;
            double along = -Vec3.Dot(r, aim);
            bool lineClear = along <= 0 || (r + aim * along).Length > safe;
            // Trips between planets: the line alone isn't enough. With thrust weaker than the planet's pull, burning toward the
            // target against the way the ship is going dragged it back down to 65 km over Kerbin. Fly the path itself.
            if (escaping && lineClear && !planetSystemTrip) lineClear = PathClears(r, v, mu, safe, aim * acceleration);
            if (escaping && lineClear) return false;

            direction = v.Normalized();
            if (!escaping || planetSystemTrip) return true;

            // Escaping, with the line toward the target through the planet. Prograde could point well away from the target,
            // and every km/s burned that way had to be taken off again: a slow coast from Duna to Dres flew 6 km/s more than
            // planned. Burn along the clear line nearest the target line instead, grazing the safe height with a margin,
            // while that helps; otherwise, with thrust strong against the pull, coast facing the target line until it comes
            // round.
            Vec3 inward = r * (-1 / r.Length);
            Vec3 side = aim - inward * Vec3.Dot(aim, inward);
            Vec3 forward = v - inward * Vec3.Dot(v, inward);
            // With the target almost straight through the planet every side is as near: lean the way the ship is moving.
            side += forward.Normalized() * 0.1;
            double edge = Math.Min(1, Margin * safe / r.Length);
            Vec3 clear = inward * Math.Sqrt(1 - edge * edge) + side.Normalized() * edge;
            if (Vec3.Dot(clear, aim) >= UsefulCosine && PathClears(r, v, mu, safe, clear * acceleration))
            {
                direction = clear;
                return true;
            }

            // Thrust weak against the planet's pull here: keep climbing along the orbit (coasting wouldn't get the ship away).
            if (acceleration < StrongThrust * mu / r.LengthSquared) return true;
            direction = aim;
            fire = false;
            return true;
        }

        /// <summary>A climb-out path counts as clear once it reaches this many safe radii out.</summary>
        public const double FarOut = 20;

        /// <summary>Coasting out instead of burning prograde needs thrust at least this many times the planet's pull.</summary>
        public const double StrongThrust = 2;

        /// <summary>
        /// Whether the ship, thrusting steadily with <paramref name="thrust"/> (m/s^2) from here under the planet's pull, stays
        /// above <paramref name="safe"/> until it is <see cref="FarOut"/> times that radius out. A path still undecided after six
        /// hours counts as not clear.
        /// </summary>
        public static bool PathClears(Vec3 r, Vec3 v, double mu, double safe, Vec3 thrust)
        {
            double a = thrust.Length;
            if (!(a > 0)) return false;
            double t = 0;
            while (t < 6 * 3600)
            {
                double d = r.Length;
                if (d < safe) return false;
                double speed = v.Length;
                // On its way: 20 times the safe radius out. Nearer in, a path climbing with thrust outward still turned back
                // into Kerbin at 0.25 g (its sideways motion swung the thrust round), so nothing short of far out counts.
                if (d > FarOut * safe) return true;
                // Steps of about 1 % of the time to cover the distance at the current speed, 0.5 to 20 s.
                double h = Math.Max(0.5, Math.Min(20, 0.01 * d / Math.Max(1, speed)));
                Vec3 a1 = thrust - r * (mu / (d * d * d));
                Vec3 rm = r + v * (h / 2), vm = v + a1 * (h / 2);
                double dm = rm.Length;
                Vec3 a2 = thrust - rm * (mu / (dm * dm * dm));
                r += vm * h;
                v += a2 * h;
                t += h;
            }

            return false;
        }
    }
}
