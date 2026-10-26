using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// Two constant-thrust arcs: A1 at acceleration a1 for t1 seconds, a coast of tc seconds (the flip), then A2 at
    /// acceleration a2 for t2 seconds, taking the ship from its current state to a required end state. Gravity is ignored; the
    /// guidance re-solves from the actual state every step, which absorbs it.
    /// </summary>
    /// <remarks>
    /// Six equations (end position and velocity) in six unknowns (two directions, t1, t2):
    ///   A1 t1 + A2 t2 = vEnd - v0
    ///   A1 (t1^2/2 + t1 (tc + t2)) + A2 t2^2/2 = rEnd - r0 - v0 T,  T = t1 + tc + t2
    /// For given t1 and t2 the pair is linear in A1 and A2; t1 and t2 are then chosen so |A1| = a1 and |A2| = a2.
    /// tc is a coast between the arcs. The guidance uses it for the time the flip takes. A burn-coast-burn profile
    /// could later be added by making tc longer; the solver doesn't change.
    /// </remarks>
    public sealed class TwoArcSolver
    {
        /// <summary>The state the trip must end in, for a given arrival time.</summary>
        public delegate StateVector EndStateAt(double arrivalUT);

        public struct Arcs
        {
            public double T1, Tc, T2;
            public Vec3 A1, A2;
            public double Total => T1 + Tc + T2;
            public bool Valid => T1 > 0 && T2 > 0 && A1.IsFinite && A2.IsFinite;
        }

        /// <summary>Diagnostics: how often the warm start worked and how often the slow bracketing search ran.</summary>
        public int WarmSolves, RobustSolves;

        private readonly EndStateAt endState;
        private readonly double accel1, accel2;
        private readonly double maxTime;
        private bool haveLast;
        private Arcs last;
        private double lastUT;

        public TwoArcSolver(EndStateAt endState, double accelerationBeforeFlip, double accelerationAfterFlip, double maxTime = 1e10)
        {
            this.endState = endState;
            accel1 = accelerationBeforeFlip;
            accel2 = accelerationAfterFlip;
            this.maxTime = maxTime;
        }

        /// <summary>
        /// Solve from the ship's state at <paramref name="ut"/>, with a coast of <paramref name="coast"/> seconds between
        /// the arcs. Warm-starts from the previous solution.
        /// </summary>
        public Arcs Solve(double ut, Vec3 r0, Vec3 v0, double coast = 0)
        {
            if (haveLast)
            {
                double elapsed = ut - lastUT;
                Arcs warm = Newton(ut, r0, v0, Math.Max(1e-3, last.T1 - elapsed), Math.Max(1e-3, last.T2), coast);
                if (warm.Valid)
                {
                    WarmSolves++;
                    Remember(ut, warm);
                    return warm;
                }
            }

            RobustSolves++;
            Arcs robust = Robust(ut, r0, v0, coast);
            if (robust.Valid) Remember(ut, robust);
            return robust;
        }

        /// <summary>
        /// Solve with the arrival time fixed: t1 + coast + t2 = <paramref name="total"/>, the coast taking up whatever
        /// the burns don't need (a coast trip). Warm-starts from the previous solution.
        /// </summary>
        public Arcs SolveFixedTotal(double ut, Vec3 r0, Vec3 v0, double total)
        {
            if (haveLast)
            {
                double elapsed = ut - lastUT;
                Arcs warm = NewtonFixedTotal(ut, r0, v0, total, Math.Max(1e-3, last.T1 - elapsed), Math.Max(1e-3, last.T2));
                if (warm.Valid)
                {
                    WarmSolves++;
                    Remember(ut, warm);
                    return warm;
                }
            }

            RobustSolves++;
            Arcs robust = RobustFixedTotal(ut, r0, v0, total);
            if (robust.Valid) Remember(ut, robust);
            return robust;
        }

        private Arcs NewtonFixedTotal(double ut, Vec3 r0, Vec3 v0, double total, double t1, double t2)
        {
            for (int iter = 0; iter < 10; iter++)
            {
                if (t1 + t2 >= total) return default;
                Arcs arcs = Evaluate(ut, r0, v0, t1, t2, total - t1 - t2);
                double f1 = arcs.A1.Length / accel1 - 1, f2 = arcs.A2.Length / accel2 - 1;
                if (Math.Abs(f1) < 1e-11 && Math.Abs(f2) < 1e-11) return arcs;

                double h1 = Math.Max(1e-4, 1e-7 * t1), h2 = Math.Max(1e-4, 1e-7 * t2);
                Arcs d1 = Evaluate(ut, r0, v0, t1 + h1, t2, total - t1 - h1 - t2);
                Arcs d2 = Evaluate(ut, r0, v0, t1, t2 + h2, total - t1 - t2 - h2);
                double j11 = (d1.A1.Length / accel1 - 1 - f1) / h1, j21 = (d1.A2.Length / accel2 - 1 - f2) / h1;
                double j12 = (d2.A1.Length / accel1 - 1 - f1) / h2, j22 = (d2.A2.Length / accel2 - 1 - f2) / h2;
                double det = j11 * j22 - j12 * j21;
                if (det == 0 || double.IsNaN(det)) break;
                double s1 = (f1 * j22 - f2 * j12) / det, s2 = (j11 * f2 - j21 * f1) / det;
                double scale = 1;
                while ((t1 - scale * s1 <= 0 || t2 - scale * s2 <= 0 || t1 - scale * s1 + t2 - scale * s2 >= total) && scale > 1e-6) scale *= 0.5;
                t1 -= scale * s1;
                t2 -= scale * s2;
            }

            return default;
        }

        // For a fixed arrival time, more burn time means less acceleration needed: find the burn time B = t1 + t2 (split so
        // |A1|/a1 = |A2|/a2) where that ratio is exactly 1.
        private Arcs RobustFixedTotal(double ut, Vec3 r0, Vec3 v0, double total)
        {
            double lo = 0, hi = total * (1 - 1e-9);
            Arcs best = default;
            if (BalancedSplit(ut, r0, v0, hi, total - hi).A1.Length > accel1) return default; // can't make it even burning all the way
            for (int i = 0; i < 100 && hi - lo > 1e-9 * total; i++)
            {
                double mid = 0.5 * (lo + hi);
                Arcs s = BalancedSplit(ut, r0, v0, mid, total - mid);
                if (s.A1.Length <= accel1)
                {
                    hi = mid;
                    best = s;
                }
                else lo = mid;
            }

            if (!best.Valid) return default;
            Arcs polished = NewtonFixedTotal(ut, r0, v0, total, best.T1, best.T2);
            return polished.Valid ? polished : best;
        }

        private void Remember(double ut, Arcs arcs)
        {
            last = arcs;
            lastUT = ut;
            haveLast = true;
        }

        public Arcs Evaluate(double ut, Vec3 r0, Vec3 v0, double t1, double t2, double tc = 0)
        {
            double total = t1 + tc + t2;
            StateVector end = endState(ut + total);
            Vec3 p = end.Velocity - v0;
            Vec3 q = end.Position - r0 - v0 * total;
            double a11 = t1, a12 = t2, a21 = t1 * t1 / 2 + t1 * (tc + t2), a22 = t2 * t2 / 2;
            double det = a11 * a22 - a12 * a21;
            return new Arcs
            {
                T1 = t1,
                Tc = tc,
                T2 = t2,
                A1 = (p * a22 - q * a12) / det,
                A2 = (q * a11 - p * a21) / det,
            };
        }

        // Newton's method on (t1, t2) for |A1| = a1 and |A2| = a2, with a finite-difference Jacobian.
        private Arcs Newton(double ut, Vec3 r0, Vec3 v0, double t1, double t2, double tc)
        {
            for (int iter = 0; iter < 8; iter++)
            {
                Arcs arcs = Evaluate(ut, r0, v0, t1, t2, tc);
                double f1 = arcs.A1.Length / accel1 - 1, f2 = arcs.A2.Length / accel2 - 1;
                if (Math.Abs(f1) < 1e-11 && Math.Abs(f2) < 1e-11) return arcs;

                double h1 = Math.Max(1e-4, 1e-7 * t1), h2 = Math.Max(1e-4, 1e-7 * t2);
                Arcs d1 = Evaluate(ut, r0, v0, t1 + h1, t2, tc);
                Arcs d2 = Evaluate(ut, r0, v0, t1, t2 + h2, tc);
                double j11 = (d1.A1.Length / accel1 - 1 - f1) / h1, j21 = (d1.A2.Length / accel2 - 1 - f2) / h1;
                double j12 = (d2.A1.Length / accel1 - 1 - f1) / h2, j22 = (d2.A2.Length / accel2 - 1 - f2) / h2;
                double det = j11 * j22 - j12 * j21;
                if (det == 0 || double.IsNaN(det)) break;
                double s1 = (f1 * j22 - f2 * j12) / det, s2 = (j11 * f2 - j21 * f1) / det;
                // Keep both times positive.
                double scale = 1;
                while ((t1 - scale * s1 <= 0 || t2 - scale * s2 <= 0) && scale > 1e-6) scale *= 0.5;
                t1 -= scale * s1;
                t2 -= scale * s2;
            }

            return default;
        }

        // Bracketing: for each burn time T = t1 + t2 find the split where |A1|/a1 = |A2|/a2, then the smallest T where
        // that ratio is <= 1.
        private Arcs Robust(double ut, Vec3 r0, Vec3 v0, double tc)
        {
            double prev = 0;
            for (double t = 1; t <= maxTime * 1.3; t *= 1.3)
            {
                double total = Math.Min(t, maxTime);
                Arcs split = BalancedSplit(ut, r0, v0, total, tc);
                if (split.A1.Length <= accel1)
                {
                    double lo = prev, hi = total;
                    Arcs best = split;
                    for (int i = 0; i < 100 && hi - lo > 1e-9 * hi; i++)
                    {
                        double mid = 0.5 * (lo + hi);
                        Arcs s = BalancedSplit(ut, r0, v0, mid, tc);
                        if (s.A1.Length <= accel1)
                        {
                            hi = mid;
                            best = s;
                        }
                        else lo = mid;
                    }

                    // Polish to the exact acceleration.
                    Arcs polished = Newton(ut, r0, v0, best.T1, best.T2, tc);
                    return polished.Valid ? polished : best;
                }

                prev = total;
                if (total >= maxTime) break;
            }

            return default;
        }

        private Arcs BalancedSplit(double ut, Vec3 r0, Vec3 v0, double total, double tc)
        {
            double lo = 1e-9, hi = 1 - 1e-9;
            Arcs mid = default;
            for (int i = 0; i < 100; i++)
            {
                double s = 0.5 * (lo + hi);
                mid = Evaluate(ut, r0, v0, s * total, (1 - s) * total, tc);
                if (mid.A1.Length / accel1 > mid.A2.Length / accel2) lo = s; else hi = s;
            }

            return mid;
        }
    }
}
