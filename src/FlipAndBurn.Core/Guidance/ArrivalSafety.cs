using System;

namespace FlipAndBurn.Core.Guidance
{
    public enum ArrivalIssue
    {
        None,
        /// <summary>Falling toward the planet too fast: stopping above its atmosphere needs most of the engines' full thrust.</summary>
        CantStopInTime,
        /// <summary>Past the planet and leaving it: on a path out of its sphere of influence.</summary>
        Escaping,
    }

    /// <summary>
    /// Watches the final approach for a trip that can't arrive cleanly (ADR 0003, "Arriving"). The arrival law brakes onto
    /// the parking orbit by design; this catches the cases where it can't, early enough to save the ship, and only those:
    /// it must never fire on a trip that is arriving normally.
    /// </summary>
    public static class ArrivalSafety
    {
        /// <summary>
        /// The engines must keep up at least this many times the chosen acceleration for a rescue. A flip-and-burn arrival
        /// brakes at 99-100 % of the chosen acceleration by design, and a normal approach can need up to about 1.2 times the
        /// simple stopping distance (Jool's pull, a curved path): with less headroom a rescue couldn't be told apart from a
        /// normal arrival, and couldn't do much more.
        /// </summary>
        public const double RescueHeadroom = 2.0;

        /// <summary>
        /// Fire when stopping at the sustainable thrust would take more than this share of the height left above the
        /// atmosphere. A normal approach sits at about 0.6 of it with 2x headroom; a rescue from 0.9 stops with 10 % to spare.
        /// </summary>
        public const double RescueMargin = 0.9;

        /// <param name="r">The ship's position relative to the target (m).</param>
        /// <param name="v">The ship's velocity relative to the target (m/s).</param>
        /// <param name="mu">The target's gravitational parameter.</param>
        /// <param name="safeRadius">The lowest safe radius: the top of the atmosphere (or the highest terrain), plus a margin.</param>
        /// <param name="parkingRadius">The parking orbit's radius.</param>
        /// <param name="soi">The target's sphere of influence (m).</param>
        /// <param name="chosenAcceleration">What the trip brakes at (m/s^2).</param>
        /// <param name="fullThrustAcceleration">The most the engines can keep up without overheating (m/s^2): the rescue's thrust.</param>
        public static ArrivalIssue Check(Vec3 r, Vec3 v, double mu, double safeRadius, double parkingRadius, double soi,
            double chosenAcceleration, double fullThrustAcceleration)
        {
            double rl = r.Length;
            if (!(rl > 0) || (soi > 0 && rl > soi)) return ArrivalIssue.None;
            Vec3 up = r / rl;
            double vr = Vec3.Dot(v, up);
            double vt = (v - up * vr).Length;

            if (vr < 0)
            {
                // Inbound. The arrival law already brakes at nearly the chosen acceleration, so only extra sustainable thrust is
                // a rescue; without headroom there is nothing to add. With it: braking straight up at that thrust, how far down
                // does the ship fall before its descent stops? Gravity works against the braking; the centrifugal effect of the
                // sideways speed works with it (on a curved approach it holds the ship up).
                if (!(fullThrustAcceleration >= RescueHeadroom * chosenAcceleration)) return ArrivalIssue.None;
                return FallShare(r, v, mu, safeRadius, fullThrustAcceleration) > RescueMargin ? ArrivalIssue.CantStopInTime : ArrivalIssue.None;
            }

            // Outbound, unbound, and well beyond the parking orbit: flying past.
            double energy = v.LengthSquared / 2 - mu / rl;
            return energy > 0 && rl > 2 * parkingRadius ? ArrivalIssue.Escaping : ArrivalIssue.None;
        }

        /// <summary>
        /// Inbound: how far the ship falls before braking straight up at <paramref name="thrust"/> stops its descent, as a share
        /// of the height left above <paramref name="safeRadius"/> (0 when outbound; infinity when it can't stop).
        /// </summary>
        public static double FallShare(Vec3 r, Vec3 v, double mu, double safeRadius, double thrust)
        {
            double rl = r.Length;
            Vec3 up = r / rl;
            double vr = Vec3.Dot(v, up);
            if (vr >= 0) return 0;
            double vt = (v - up * vr).Length;
            double braking = thrust - (mu / (rl * rl) - vt * vt / rl);
            if (!(braking > 0)) return double.PositiveInfinity;
            return vr * vr / (2 * braking) / Math.Max(1, rl - safeRadius);
        }
    }
}
