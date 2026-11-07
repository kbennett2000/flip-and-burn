using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// Steering for the braking half: brings the ship to a required state relative to a moving frame (the target
    /// planet, or a waypoint), accounting for gravity.
    /// </summary>
    /// <remarks>
    /// Zero-effort-miss / zero-effort-velocity guidance. With time to go τ, predict where the ship would be if it
    /// coasted: rB = r + v τ + G2, vB = v + G1, where G1 and G2 add up gravity along the expected path
    /// (<see cref="PathGravity"/>). The misses are ZEM = rF - rB and ZEV = vF - vB, and the thrust acceleration that
    /// removes both by τ is a = 6 ZEM / τ^2 - 2 ZEV / τ.
    ///
    /// τ is a deadline: it starts at the time to go from the plan and counts down. On the planned path the formula then
    /// gives exactly the planned braking. When the ship is a little behind, it gives a little less; a little ahead, a
    /// little more. That is the along-track correction, and it stays within a narrow band (98.2 % to 100 % of the chosen
    /// acceleration by default). Only when the formula would leave the band is the deadline moved, just enough to bring
    /// it back to the band's edge. τ never jumps, so the steering can't switch to a different solution (one of which
    /// speeds the ship up into the target).
    /// </remarks>
    public sealed class ArrivalLaw
    {
        /// <summary>Diagnostics: steps where the deadline was moved, and steps where it just counted down.</summary>
        public int Nudged, Carried;

        /// <summary>Diagnostics: what the law asked for last step before the band was applied (m/s^2), and the deadline then.</summary>
        public double LastAsked, LastTau;

        private readonly IEphemeris frame;
        private readonly PathGravity gravity;
        private readonly Vec3 finalPosition;   // relative to the frame body
        private readonly Vec3 finalVelocity;
        private readonly double nominal, min, max;
        private double tau, lastUT, nowUT;
        private Vec3 lastZem, lastZev, evalZem, evalZev;   // the misses at the deadline flown (last), and at the last evaluation
        private double lastT;
        private bool started;

        /// <param name="nominal">Braking acceleration the trip was planned with.</param>
        /// <param name="min">Lowest thrust acceleration allowed.</param>
        /// <param name="max">Highest thrust acceleration allowed (the chosen acceleration).</param>
        public ArrivalLaw(IEphemeris frame, PathGravity gravity, Vec3 finalPosition, Vec3 finalVelocity,
            double nominal, double min, double max)
        {
            this.frame = frame;
            this.gravity = gravity;
            this.finalPosition = finalPosition;
            this.finalVelocity = finalVelocity;
            this.nominal = nominal;
            this.min = min;
            this.max = max;
        }

        public double Nominal => nominal;

        /// <summary>Start with a time to go, from the plan.</summary>
        public void Start(double ut, double timeToGo)
        {
            tau = timeToGo;
            lastUT = ut;
            started = true;
        }

        /// <summary>Thrust acceleration vector (magnitude within the band) and the time to go.</summary>
        public Vec3 Solve(double ut, Vec3 position, Vec3 velocity, out double timeToGo)
        {
            Relative(ut, position, velocity, out Vec3 r, out Vec3 v);
            CountDown(ut, v);

            Vec3 a = AccelFor(r, v, tau);
            double mag = a.Length;
            LastAsked = mag;
            LastTau = tau;
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

            // The misses at the deadline being flown, for the forecast (NudgeTau evaluates other deadlines on the way).
            AccelFor(r, v, tau);
            lastZem = evalZem;
            lastZev = evalZev;
            lastT = tau;
            timeToGo = tau;
            if (mag == 0) return a;
            return a * (Math.Max(min, Math.Min(max, mag)) / mag);
        }

        private void Relative(double ut, Vec3 position, Vec3 velocity, out Vec3 r, out Vec3 v)
        {
            StateVector f = frame.StateAt(ut);
            r = position - f.Position;
            v = velocity - f.Velocity;
            nowUT = ut;
        }

        private void CountDown(double ut, Vec3 v)
        {
            if (!started) Start(ut, Math.Max(1, (finalVelocity - v).Length / nominal));
            tau = Math.Max(1e-3, tau - (ut - lastUT));
            lastUT = ut;
        }

        private Vec3 AccelFor(Vec3 r, Vec3 v, double t)
        {
            gravity.Integrate(nowUT, r, v, finalPosition, finalVelocity, t, out Vec3 g1, out Vec3 g2);
            Vec3 zem = finalPosition - (r + v * t + g2);
            Vec3 zev = finalVelocity - (v + g1);
            evalZem = zem;
            evalZev = zev;
            return zem * (6 / (t * t)) - zev * (2 / t);
        }

        /// <summary>
        /// The delta-v the rest of the braking will use (m/s), as the law plans it at the last <see cref="Solve"/>: its thrust
        /// changes evenly from now to the deadline (the zero-effort solution: from 6 ZEM/τ² - 2 ZEV/τ now to
        /// -6 ZEM/τ² + 4 ZEV/τ at the end), each moment held within the band. In a deep well it rises toward full thrust at the end.
        /// </summary>
        /// <param name="chosen">The chosen acceleration (m/s^2), with <paramref name="ispThrottleExponent"/>: each moment's m/s
        /// counted at what it costs out of the ship's delta-v there (<see cref="ShipPerformance.ThrottleCost"/>).</param>
        /// <param name="ispThrottleExponent">How the engine's Isp falls at part throttle; 0 counts plain m/s.</param>
        public double ForecastDeltaV(double chosen = 0, double ispThrottleExponent = 0)
        {
            double t = lastT;
            if (!(t > 0)) return 0;
            Vec3 now = lastZem * (6 / (t * t)) - lastZev * (2 / t), end = lastZem * (-6 / (t * t)) + lastZev * (4 / t);
            const int n = 16;
            double sum = 0;
            for (int i = 0; i <= n; i++)
            {
                double x = (double)i / n, w = i == 0 || i == n ? 0.5 : 1;
                double a = Math.Max(min, Math.Min(max, (now + (end - now) * x).Length));
                sum += w * a * (chosen > 0 ? ShipPerformance.ThrottleCost(a / chosen, ispThrottleExponent) : 1);
            }

            return sum / n * t;
        }

        /// <summary>
        /// Braking really starts now (the flip is over): move the deadline so the law asks for exactly <paramref name="level"/>
        /// (the planned braking). Through the flip the ship drifts with the engine off, or thrusts sideways in a skew flip, so
        /// the deadline set at the flip no longer matches; left as it is, the law starts at its upper edge and slides to its
        /// lower one, braking too hard early and arriving with time to spare (ADR 0005). Returns false (deadline unchanged)
        /// when no nearby deadline gives that level.
        /// </summary>
        public bool Retune(double ut, Vec3 position, Vec3 velocity, double level)
        {
            Relative(ut, position, velocity, out Vec3 r, out Vec3 v);
            CountDown(ut, v);
            if (!NudgeTau(r, v, tau, level, out double t, 0.5)) return false;
            tau = t;
            return true;
        }

        // Newton on τ for |a(τ)| = level, starting from the current deadline, accepting only a nearby answer.
        private bool NudgeTau(Vec3 r, Vec3 v, double start, double level, out double result, double reach = 0.2)
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
                      Math.Abs(t - start) <= reach * start + 1;
            result = ok ? t : 0;
            return ok;
        }
    }
}
