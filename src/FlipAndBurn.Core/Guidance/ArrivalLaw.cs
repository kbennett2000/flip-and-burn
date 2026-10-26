using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// Steering after the flip: brings the ship to a required state relative to a moving frame (the target planet, or a
    /// waypoint), accounting for gravity.
    /// </summary>
    /// <remarks>
    /// Zero-effort-miss / zero-effort-velocity guidance. With time to go τ, predict where the ship would be if it
    /// coasted: rB = r + v τ + G2, vB = v + G1, where G1 = ∫ g ds and G2 = ∫ (τ - s) g ds over the rest of the trip.
    /// The misses are ZEM = rF - rB and ZEV = vF - vB, and the thrust acceleration that removes both by τ is
    /// a = 6 ZEM / τ^2 - 2 ZEV / τ. (With constant g this is 6 (rF - r) / τ^2 - (4 v + 2 vF) / τ - g.)
    ///
    /// Gravity is the frame body's pull, sampled along the path the ship is expected to take (a smooth curve from its
    /// current state to the arrival state), plus the difference between the Sun's pull on the ship and on the frame
    /// (for the target planet; for a fixed waypoint, the Sun's whole pull), taken as constant. Sampling along the path
    /// matters on the final approach: the planet's pull grows from almost nothing to several m/s^2 there, and using
    /// only today's value leaves the ship arriving too fast.
    ///
    /// τ is a deadline: it starts at the time to go from the plan made before the flip and counts down. On the planned
    /// path the formula then gives exactly the planned braking. When the ship is a little behind, it gives a little
    /// less; a little ahead, a little more. That is the along-track correction, and it stays within a narrow band
    /// (98.2 % to 100 % of the chosen acceleration by default). Only when the formula would leave the band is the
    /// deadline moved, just enough to bring it back to the band's edge. τ never jumps, so the steering can't switch to
    /// a different solution (one of which speeds the ship up into the target).
    ///
    /// Why not the exact two-body coast: while still braking toward the planet, a coast prediction would pass close to
    /// its centre, where a two-body orbit swings around wildly and the prediction stops meaning anything. Sampling
    /// gravity along the expected (braked) path avoids that.
    /// </remarks>
    public sealed class ArrivalLaw
    {
        /// <summary>Diagnostics: steps where the deadline was moved, and steps where it just counted down.</summary>
        public int Nudged, Carried;

        private readonly IEphemeris frame;
        private readonly double mu;
        private readonly Vec3 finalPosition;   // relative to the frame body
        private readonly Vec3 finalVelocity;
        private readonly double nominal, min, max;
        private readonly double sunMu;
        private readonly bool frameFeelsSun;
        private Vec3 sunTerm;
        private double tau, lastUT;
        private bool started;

        /// <param name="nominal">Braking acceleration the trip was planned with.</param>
        /// <param name="min">Lowest thrust acceleration allowed.</param>
        /// <param name="max">Highest thrust acceleration allowed (the chosen acceleration).</param>
        /// <param name="sunGravParameter">The Sun's gravitational parameter (0 to ignore the Sun).</param>
        /// <param name="frameFeelsSun">True if the frame orbits the Sun (a planet); false for a fixed point.</param>
        public ArrivalLaw(IEphemeris frame, double frameGravParameter, Vec3 finalPosition, Vec3 finalVelocity,
            double nominal, double min, double max, double sunGravParameter = 0, bool frameFeelsSun = true)
        {
            this.frame = frame;
            mu = frameGravParameter;
            this.finalPosition = finalPosition;
            this.finalVelocity = finalVelocity;
            this.nominal = nominal;
            this.min = min;
            this.max = max;
            sunMu = sunGravParameter;
            this.frameFeelsSun = frameFeelsSun;
        }

        /// <summary>Start with a time to go, from the plan made before the flip.</summary>
        public void Start(double ut, double timeToGo)
        {
            tau = timeToGo;
            lastUT = ut;
            started = true;
        }

        /// <summary>Thrust acceleration vector (magnitude within the band) and the time to go.</summary>
        public Vec3 Solve(double ut, Vec3 position, Vec3 velocity, out double timeToGo)
        {
            StateVector f = frame.StateAt(ut);
            Vec3 r = position - f.Position, v = velocity - f.Velocity;
            sunTerm = SunPull(position) - (frameFeelsSun ? SunPull(f.Position) : Vec3.Zero);

            if (!started) Start(ut, Math.Max(1, (finalVelocity - v).Length / nominal));
            tau = Math.Max(1e-3, tau - (ut - lastUT));
            lastUT = ut;

            Vec3 a = AccelFor(r, v, tau);
            double mag = a.Length;
            if (mag > max || mag < min)
            {
                // Outside the band: move the deadline just enough to land on the band's edge.
                if (NudgeTau(r, v, tau, mag > max ? max : min, out double nudged))
                {
                    Nudged++;
                    tau = nudged;
                    a = AccelFor(r, v, tau);
                    mag = a.Length;
                }
                else Carried++;
            }
            else Carried++;

            timeToGo = tau;
            if (mag == 0) return a;
            return a * (Math.Max(min, Math.Min(max, mag)) / mag);
        }

        private const int GravitySamples = 32; // Simpson intervals along the expected path (even)

        private Vec3 AccelFor(Vec3 r, Vec3 v, double t)
        {
            GravityIntegrals(mu, r, v, finalPosition, finalVelocity, t, out Vec3 g1, out Vec3 g2);
            g1 += sunTerm * t;
            g2 += sunTerm * (0.5 * t * t);
            Vec3 zem = finalPosition - (r + v * t + g2);
            Vec3 zev = finalVelocity - (v + g1);
            return zem * (6 / (t * t)) - zev * (2 / t);
        }

        /// <summary>
        /// The velocity (g1 = ∫ g ds) and position (g2 = ∫ (t - s) g ds) a body of gravitational parameter
        /// <paramref name="mu"/> adds over the next <paramref name="t"/> seconds, along the expected path: a smooth curve
        /// from (r, v) now to (rF, vF) at time t, all relative to the body. Simpson's rule, with the samples crowded toward
        /// the arrival end (s = t (1 - (1 - x)^4)), where the pull is strongest and changes fastest.
        /// </summary>
        public static void GravityIntegrals(double mu, Vec3 r, Vec3 v, Vec3 rF, Vec3 vF, double t, out Vec3 g1, out Vec3 g2)
        {
            g1 = Vec3.Zero;
            g2 = Vec3.Zero;
            if (!(mu > 0) || !(t > 0)) return;
            double h = 1.0 / GravitySamples;
            for (int i = 0; i <= GravitySamples; i++)
            {
                double x = i * h, oneMinus = 1 - x;
                double sTime = t * (1 - oneMinus * oneMinus * oneMinus * oneMinus);
                double dsdx = 4 * t * oneMinus * oneMinus * oneMinus;
                double w = (i == 0 || i == GravitySamples ? 1 : i % 2 == 1 ? 4 : 2) * h / 3 * dsdx;
                Vec3 p = ExpectedPosition(r, v, rF, vF, t, sTime);
                double l = p.Length;
                Vec3 g = l > 0 ? p * (-mu / (l * l * l)) : Vec3.Zero;
                g1 += g * w;
                g2 += g * (w * (t - sTime));
            }
        }

        // Cubic Hermite curve from (r, v) now to (rF, vF) at time t: where the ship should be at s.
        private static Vec3 ExpectedPosition(Vec3 r, Vec3 v, Vec3 rF, Vec3 vF, double t, double s)
        {
            double x = s / t, x2 = x * x, x3 = x2 * x;
            double h00 = 2 * x3 - 3 * x2 + 1, h10 = x3 - 2 * x2 + x, h01 = -2 * x3 + 3 * x2, h11 = x3 - x2;
            return r * h00 + v * (h10 * t) + rF * h01 + vF * (h11 * t);
        }

        private Vec3 SunPull(Vec3 p)
        {
            double l = p.Length;
            return sunMu > 0 && l > 0 ? p * (-sunMu / (l * l * l)) : Vec3.Zero;
        }

        // Newton on τ for |a(τ)| = level, starting from the current deadline, accepting only a nearby answer.
        private bool NudgeTau(Vec3 r, Vec3 v, double start, double level, out double result)
        {
            double t = start;
            for (int i = 0; i < 12; i++)
            {
                double f = AccelFor(r, v, t).Length / level - 1;
                if (Math.Abs(f) < 1e-10) break;
                double h = Math.Max(1e-5, 1e-7 * t);
                double d = (AccelFor(r, v, t + h).Length / level - 1 - f) / h;
                if (d == 0 || double.IsNaN(d)) break;
                t -= f / d;
                if (!(t > 0)) break;
            }

            bool ok = t > 0 && Math.Abs(AccelFor(r, v, t).Length / level - 1) < 1e-8 &&
                      Math.Abs(t - start) <= 0.2 * start + 1;
            result = ok ? t : 0;
            return ok;
        }
    }
}
