using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// Gravity acting on the ship relative to a frame (the target planet, or a fixed waypoint), added up along the path
    /// the ship is expected to take. Used both to plan ahead (aim off by what gravity will do) and to steer.
    /// </summary>
    /// <remarks>
    /// "Gravity relative to the frame" is the frame body's own pull plus the difference between the Sun's pull on the
    /// ship and on the frame (for a planet; a fixed waypoint doesn't move, so there it is the Sun's whole pull). The Sun
    /// term is small, but over a coast of days to weeks it bends the path by kilometres per second, so it is sampled
    /// along the path rather than taken as constant.
    ///
    /// The expected path is either a smooth curve (cubic Hermite) from the current state to the arrival state, relative
    /// to the frame, or a given path (a coast trip's burn-coast-burn, <see cref="CoastPath"/>: over weeks the Sun term
    /// depends strongly on where the ship really is, and a smooth curve puts a coasting ship billions of metres from
    /// where it will be). Samples crowd toward the arrival end, where the planet's pull is strongest and changes fastest.
    /// </remarks>
    public sealed class PathGravity
    {
        private const int Samples = 32; // Simpson intervals (even), for a smooth curve

        /// <summary>Simpson intervals for a coast path: weeks long, the Sun term along it needs finer sampling.</summary>
        public const int CoastSamples = 128;

        private readonly IEphemeris frame;
        private readonly double frameMu, sunMu;
        private readonly bool frameFeelsSun;

        public PathGravity(IEphemeris frame, double frameGravParameter, double sunGravParameter, bool frameFeelsSun)
        {
            this.frame = frame;
            frameMu = frameGravParameter;
            sunMu = sunGravParameter;
            this.frameFeelsSun = frameFeelsSun;
        }

        /// <summary>
        /// Over the next <paramref name="t"/> seconds from <paramref name="ut"/>: g1 = ∫ g ds (the velocity gravity adds)
        /// and g2 = ∫ (t - s) g ds (the displacement it adds), along the path from (r, v) now to (rF, vF) at the end, all
        /// relative to the frame.
        /// </summary>
        public void Integrate(double ut, Vec3 r, Vec3 v, Vec3 rF, Vec3 vF, double t, out Vec3 g1, out Vec3 g2)
        {
            Integrate(ut, s => Hermite(r, v, rF, vF, t, s), t, out g1, out g2);
        }

        /// <summary>
        /// As above, along a given path: <paramref name="path"/>(s) is the expected position relative to the frame
        /// <paramref name="s"/> seconds from now.
        /// </summary>
        public void Integrate(double ut, Func<double, Vec3> path, double t, out Vec3 g1, out Vec3 g2, int intervals = Samples)
        {
            g1 = Vec3.Zero;
            g2 = Vec3.Zero;
            if (!(t > 0)) return;
            double h = 1.0 / intervals;
            for (int i = 0; i <= intervals; i++)
            {
                double x = i * h, oneMinus = 1 - x;
                double s = t * (1 - oneMinus * oneMinus * oneMinus * oneMinus);
                double dsdx = 4 * t * oneMinus * oneMinus * oneMinus;
                double w = (i == 0 || i == intervals ? 1 : i % 2 == 1 ? 4 : 2) * h / 3 * dsdx;
                Vec3 g = Acceleration(ut + s, path(s));
                g1 += g * w;
                g2 += g * (w * (t - s));
            }
        }

        /// <summary>
        /// How far the frame itself strays from a straight line over the next <paramref name="t"/> seconds (the Sun
        /// curving a planet's orbit), in position and velocity. A gravity-free plan in the Sun's frame aims at the
        /// frame's real future position, so to leave the ship where gravity relative to the frame will carry it, it must
        /// allow for this as well as for <see cref="Integrate(double, Func{double, Vec3}, double, out Vec3, out Vec3)"/>.
        /// </summary>
        public void FrameCurve(double ut, double t, out Vec3 position, out Vec3 velocity)
        {
            StateVector now = frame.StateAt(ut), then = frame.StateAt(ut + t);
            position = then.Position - now.Position - now.Velocity * t;
            velocity = then.Velocity - now.Velocity;
        }

        /// <summary>Gravity relative to the frame at time <paramref name="ut"/>, at <paramref name="rel"/> from the frame.</summary>
        public Vec3 Acceleration(double ut, Vec3 rel)
        {
            Vec3 g = Pull(rel, frameMu);
            if (sunMu > 0)
            {
                Vec3 framePos = frame.StateAt(ut).Position;
                g += Pull(framePos + rel, sunMu) - (frameFeelsSun ? Pull(framePos, sunMu) : Vec3.Zero);
            }

            return g;
        }

        /// <summary>
        /// The path of a burn-coast-burn plan relative to the frame, starting at (r, v) now (<paramref name="ut"/>): a1
        /// for t1 seconds, then a coast of tc, then the braking burn of t2 seconds ending at (rF, vF).
        /// </summary>
        /// <remarks>
        /// The first burn is short and taken as a straight push (gravity moves the ship a few hundred kilometres in it).
        /// The coast is where it matters: weeks long, it follows the Sun-centred orbit the ship will really be on, so
        /// the Sun term is sampled where the ship will be. (A smooth curve, or a straight line, puts the ship a
        /// hundred million metres or more from there, and over weeks that is a few hundred m/s of course error.) The
        /// braking part is a smooth curve into the end state, so the samples near arrival, where the planet pulls
        /// hardest, are close to where the ship will be.
        /// </remarks>
        public Func<double, Vec3> CoastPath(double ut, Vec3 r, Vec3 v, double t1, Vec3 a1, double tc, double t2, Vec3 rF, Vec3 vF)
        {
            Vec3 r1 = r + v * t1 + a1 * (0.5 * t1 * t1), v1 = v + a1 * t1;
            StateVector f1 = frame.StateAt(ut + t1);
            Vec3 abs1 = f1.Position + r1, absV1 = f1.Velocity + v1;
            double braking = t1 + tc;

            Vec3 Coasting(double s, out Vec3 velocity)
            {
                StateVector ship = TwoBody.Propagate(abs1, absV1, sunMu, s - t1);
                StateVector f = frame.StateAt(ut + s);
                velocity = ship.Velocity - f.Velocity;
                return ship.Position - f.Position;
            }

            Vec3 brakeFrom = Coasting(braking, out Vec3 brakeFromVelocity);
            return s =>
            {
                if (s <= t1) return r + v * s + a1 * (0.5 * s * s);
                if (s <= braking || !(t2 > 0)) return Coasting(s, out _);
                return Hermite(brakeFrom, brakeFromVelocity, rF, vF, t2, s - braking);
            };
        }

        private static Vec3 Pull(Vec3 p, double mu)
        {
            double l = p.Length;
            return mu > 0 && l > 0 ? p * (-mu / (l * l * l)) : Vec3.Zero;
        }

        private static Vec3 Hermite(Vec3 r, Vec3 v, Vec3 rF, Vec3 vF, double t, double s)
        {
            double x = s / t, x2 = x * x, x3 = x2 * x;
            double h00 = 2 * x3 - 3 * x2 + 1, h10 = x3 - 2 * x2 + x, h01 = -2 * x3 + 3 * x2, h11 = x3 - x2;
            return r * h00 + v * (h10 * t) + rF * h01 + vF * (h11 * t);
        }
    }
}
