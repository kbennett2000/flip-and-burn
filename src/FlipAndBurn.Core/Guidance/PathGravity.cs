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
        // Moon trips: other bodies that pull on the ship (the moon it is leaving), on their own orbits around the centre.
        private readonly System.Collections.Generic.List<(IEphemeris Ephemeris, double Mu)> extra =
            new System.Collections.Generic.List<(IEphemeris, double)>();

        /// <summary>
        /// Moon trips (ADR 0005): another body that pulls on the ship, such as the moon it is leaving. Its pull on the frame
        /// isn't counted: in KSP moons move on fixed orbits around their planet.
        /// </summary>
        public void AddBody(IEphemeris ephemeris, double gravParameter)
        {
            if (ephemeris != null && gravParameter > 0) extra.Add((ephemeris, gravParameter));
        }

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
            if (sunMu > 0 || extra.Count > 0)
            {
                Vec3 framePos = frame.StateAt(ut).Position;
                if (sunMu > 0) g += Pull(framePos + rel, sunMu) - (frameFeelsSun ? Pull(framePos, sunMu) : Vec3.Zero);
                foreach ((IEphemeris e, double mu) in extra)
                    g += Pull(framePos + rel - e.StateAt(ut).Position, mu);
            }

            return g;
        }

        /// <summary>
        /// Moon trips (ADR 0005): what gravity adds over a planned burn, along the path the ship will really fly. Thrust
        /// <paramref name="a1"/> for <paramref name="t1"/> s, a coast of <paramref name="tc"/> s, then <paramref name="a2"/>
        /// for <paramref name="t2"/> s, from (r, v) relative to the frame now, gravity included. Near a planet its pull is
        /// a large share of the thrust (leaving a low Kerbin orbit, three quarters of 1 g), and a smooth curve from here to
        /// the arrival is far from where the ship will be, so the path is flown instead: RK4 with the steps crowded toward
        /// the start and end of each part, where gravity changes fastest. Returns g1 = ∫ g ds and g2 = ∫ (t - s) g ds.
        /// </summary>
        /// <param name="visit">If given, called after every step with the time since <paramref name="ut"/> and the ship's
        /// position and velocity relative to the frame.</param>
        public void IntegratePlanned(double ut, Vec3 r, Vec3 v, Vec3 a1, double t1, double tc, Vec3 a2, double t2,
            out Vec3 g1, out Vec3 g2, int stepsPerPart = 40, Action<double, Vec3, Vec3> visit = null)
        {
            var s = new Flown { R = r, V = v };
            double clock = 0;
            Part(ref s, ref clock, a1, t1, stepsPerPart);
            Part(ref s, ref clock, Vec3.Zero, tc, stepsPerPart);
            Part(ref s, ref clock, a2, t2, stepsPerPart);
            g1 = s.G1;
            g2 = s.G2;

            void Part(ref Flown state, ref double start, Vec3 thrust, double length, int n)
            {
                if (!(length > 0)) return;
                double from = 0;
                for (int i = 1; i <= n; i++)
                {
                    // Crowded at both ends of the part: s = L (1 - cos(pi x)) / 2.
                    double to = length * (1 - Math.Cos(Math.PI * i / n)) / 2;
                    state = Rk4(ut + start, state, thrust, from, to - from);
                    from = to;
                    visit?.Invoke(start + to, state.R, state.V);
                }

                start += length;
            }
        }

        // The ship relative to the frame, with the running gravity sums: G1' = g, G2' = G1 (so G2 = ∫ (t - s) g ds at the end).
        private struct Flown
        {
            public Vec3 R, V, G1, G2;
        }

        private Flown Rk4(double partStartUT, Flown s, Vec3 thrust, double t, double h)
        {
            Vec3 ga = Acceleration(partStartUT + t, s.R);
            Vec3 k1r = s.V, k1v = ga + thrust, k1g = ga, k1h = s.G1;
            Vec3 r2 = s.R + k1r * (h / 2), v2 = s.V + k1v * (h / 2);
            Vec3 gb = Acceleration(partStartUT + t + h / 2, r2);
            Vec3 k2r = v2, k2v = gb + thrust, k2g = gb, k2h = s.G1 + k1g * (h / 2);
            Vec3 r3 = s.R + k2r * (h / 2), v3 = s.V + k2v * (h / 2);
            Vec3 gc = Acceleration(partStartUT + t + h / 2, r3);
            Vec3 k3r = v3, k3v = gc + thrust, k3g = gc, k3h = s.G1 + k2g * (h / 2);
            Vec3 r4 = s.R + k3r * h, v4 = s.V + k3v * h;
            Vec3 gd = Acceleration(partStartUT + t + h, r4);
            Vec3 k4r = v4, k4v = gd + thrust, k4g = gd, k4h = s.G1 + k3g * h;
            return new Flown
            {
                R = s.R + (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6),
                V = s.V + (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6),
                G1 = s.G1 + (k1g + k2g * 2 + k3g * 2 + k4g) * (h / 6),
                G2 = s.G2 + (k1h + k2h * 2 + k3h * 2 + k4h) * (h / 6),
            };
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
