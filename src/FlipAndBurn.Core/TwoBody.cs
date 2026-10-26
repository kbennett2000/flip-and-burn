using System;

namespace FlipAndBurn.Core
{
    /// <summary>Coasting under one body's gravity, and the shape of the resulting orbit.</summary>
    public static class TwoBody
    {
        /// <summary>
        /// Where a coasting object is <paramref name="dt"/> seconds later, relative to a body with gravitational
        /// parameter <paramref name="mu"/>. Works for ellipses, parabolas and hyperbolas (universal-variable Kepler
        /// solution, Vallado's algorithm 8). With mu = 0 it is a straight line.
        /// </summary>
        public static StateVector Propagate(Vec3 r0, Vec3 v0, double mu, double dt) => Propagate(r0, v0, mu, dt, 0);

        // A very eccentric hyperbola (a fast flyby far out) can send the iteration astray. The answer must keep the
        // orbit's energy; if it doesn't, take two half steps (each starts closer to the answer).
        private static StateVector Propagate(Vec3 r0, Vec3 v0, double mu, double dt, int depth)
        {
            StateVector result = PropagateOnce(r0, v0, mu, dt);
            if (mu <= 0 || depth >= 4) return result;
            double e0 = v0.LengthSquared / 2 - mu / r0.Length;
            double e1 = result.Velocity.LengthSquared / 2 - mu / result.Position.Length;
            if (result.Position.IsFinite && result.Velocity.IsFinite &&
                Math.Abs(e1 - e0) <= 1e-6 * Math.Max(Math.Abs(e0), v0.LengthSquared / 2)) return result;
            StateVector half = Propagate(r0, v0, mu, dt / 2, depth + 1);
            return Propagate(half.Position, half.Velocity, mu, dt / 2, depth + 1);
        }

        private static StateVector PropagateOnce(Vec3 r0, Vec3 v0, double mu, double dt)
        {
            if (dt == 0) return new StateVector(r0, v0);
            if (mu <= 0) return new StateVector(r0 + v0 * dt, v0);

            double rLen = r0.Length;
            double sqrtMu = Math.Sqrt(mu);
            double rv = Vec3.Dot(r0, v0) / sqrtMu;
            double alpha = 2 / rLen - v0.LengthSquared / mu;

            // Gravity negligible over this coast (fast and far away): a straight line plus constant gravity is exact
            // enough, and avoids the badly conditioned near-straight hyperbola.
            if (mu * dt * dt / (rLen * rLen * rLen) < 1e-12)
            {
                Vec3 g = r0 * (-mu / (rLen * rLen * rLen));
                return new StateVector(r0 + v0 * dt + g * (0.5 * dt * dt), v0 + g * dt);
            }

            double x;
            if (alpha > 1e-12) x = sqrtMu * dt * alpha;
            else if (alpha < -1e-12)
            {
                double a = 1 / alpha;
                double s = Math.Sign(dt);
                double arg = -2 * mu * alpha * dt / (Vec3.Dot(r0, v0) + s * Math.Sqrt(-mu * a) * (1 - rLen * alpha));
                x = arg > 0 ? s * Math.Sqrt(-a) * Math.Log(arg) : sqrtMu * dt / rLen;
            }
            else x = sqrtMu * dt / rLen;

            // Time as a function of x rises steadily (its slope is the distance over sqrt(mu)), so keep a bracket
            // around the answer and fall back to halving it whenever a Newton step would leave it. Plain Newton can
            // run away on a near-straight hyperbola aimed at the body.
            double TimeAt(double xx, out double rr)
            {
                double p = xx * xx * alpha;
                Stumpff(p, out double cc2, out double cc3);
                rr = xx * xx * cc2 + rv * xx * (1 - p * cc3) + rLen * (1 - p * cc2);
                return (xx * xx * xx * cc3 + rv * xx * xx * cc2 + rLen * xx * (1 - p * cc3)) / sqrtMu;
            }

            double lo = Math.Min(0, x), hi = Math.Max(0, x);
            if (dt > 0)
            {
                lo = 0;
                hi = Math.Max(x, sqrtMu * dt / rLen);
                for (int i = 0; i < 200 && TimeAt(hi, out _) < dt; i++) { lo = hi; hi *= 2; }
            }
            else
            {
                hi = 0;
                lo = Math.Min(x, sqrtMu * dt / rLen);
                for (int i = 0; i < 200 && TimeAt(lo, out _) > dt; i++) { hi = lo; lo *= 2; }
            }

            if (!(x > lo && x < hi)) x = 0.5 * (lo + hi);
            double c2 = 0.5, c3 = 1.0 / 6, psi = 0, r = rLen;
            for (int i = 0; i < 200; i++)
            {
                double t = TimeAt(x, out r);
                double f = t - dt;
                if (f > 0) hi = x; else lo = x;
                double next = x - f * sqrtMu / r;
                if (!(next > lo && next < hi) || double.IsNaN(next)) next = 0.5 * (lo + hi);
                double step = next - x;
                x = next;
                if (Math.Abs(step) <= 1e-12 * Math.Max(1, Math.Abs(x)) || hi - lo <= 1e-14 * Math.Max(1, Math.Abs(x))) break;
            }

            psi = x * x * alpha;
            Stumpff(psi, out c2, out c3);
            r = x * x * c2 + rv * x * (1 - psi * c3) + rLen * (1 - psi * c2);
            double ff = 1 - x * x / rLen * c2;
            double gg = dt - x * x * x / sqrtMu * c3;
            double gdot = 1 - x * x / r * c2;
            double fdot = sqrtMu / (r * rLen) * x * (psi * c3 - 1);
            var pos = r0 * ff + v0 * gg;
            var vel = r0 * fdot + v0 * gdot;
            if (!pos.IsFinite || !vel.IsFinite)
            {
                Vec3 g = r0 * (-mu / (rLen * rLen * rLen));
                return new StateVector(r0 + v0 * dt + g * (0.5 * dt * dt), v0 + g * dt);
            }

            return new StateVector(pos, vel);
        }

        private static void Stumpff(double psi, out double c2, out double c3)
        {
            if (psi > 1e-6)
            {
                double sp = Math.Sqrt(psi);
                c2 = (1 - Math.Cos(sp)) / psi;
                c3 = (sp - Math.Sin(sp)) / (sp * psi);
            }
            else if (psi < -1e-6)
            {
                double sp = Math.Sqrt(-psi);
                c2 = (1 - Math.Cosh(sp)) / psi;
                c3 = (Math.Sinh(sp) - sp) / (sp * -psi);
            }
            else
            {
                c2 = 0.5 - psi / 24;
                c3 = 1.0 / 6 - psi / 120;
            }
        }

        /// <summary>Eccentricity and periapsis/apoapsis radius of the orbit through this state.</summary>
        public static OrbitShape Shape(Vec3 r, Vec3 v, double mu)
        {
            Vec3 h = Vec3.Cross(r, v);
            Vec3 e = Vec3.Cross(v, h) / mu - r.Normalized();
            double ecc = e.Length;
            double energy = v.LengthSquared / 2 - mu / r.Length;
            double sma = -mu / (2 * energy);
            double periapsis = h.LengthSquared / mu / (1 + ecc);
            double apoapsis = ecc < 1 ? sma * (1 + ecc) : double.PositiveInfinity;
            return new OrbitShape(ecc, periapsis, apoapsis, sma, h.Normalized());
        }
    }

    public readonly struct OrbitShape
    {
        public readonly double Eccentricity;
        public readonly double PeriapsisRadius;
        public readonly double ApoapsisRadius;   // infinite for an open orbit
        public readonly double SemiMajorAxis;    // negative for a hyperbola
        public readonly Vec3 Normal;             // unit angular momentum

        public OrbitShape(double eccentricity, double periapsisRadius, double apoapsisRadius, double semiMajorAxis, Vec3 normal)
        {
            Eccentricity = eccentricity;
            PeriapsisRadius = periapsisRadius;
            ApoapsisRadius = apoapsisRadius;
            SemiMajorAxis = semiMajorAxis;
            Normal = normal;
        }

        public bool Closed => Eccentricity < 1;
    }
}
