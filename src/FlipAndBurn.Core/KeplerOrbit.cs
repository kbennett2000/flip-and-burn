using System;

namespace FlipAndBurn.Core
{
    /// <summary>Where something is at a given time.</summary>
    public interface IEphemeris
    {
        /// <summary>Position and velocity relative to the Sun at universal time <paramref name="ut"/> (seconds).</summary>
        StateVector StateAt(double ut);
    }

    /// <summary>
    /// An elliptical orbit around a central body, given by the same elements KSP uses for its planets.
    /// </summary>
    /// <remarks>
    /// Frame: right-handed and inertial, centred on the parent body. +Z is the normal of the reference plane, +X points
    /// at the reference direction (longitude of ascending node 0). Prograde orbits go counter-clockwise seen from +Z.
    /// KSP's own vectors are left-handed with Y and Z swapped ("xzy"); whoever feeds KSP state into this code must
    /// convert first.
    /// </remarks>
    public sealed class KeplerOrbit : IEphemeris
    {
        public double SemiMajorAxis { get; }           // m
        public double Eccentricity { get; }            // 0 <= e < 1
        public double InclinationDeg { get; }
        public double LongitudeOfAscendingNodeDeg { get; }
        public double ArgumentOfPeriapsisDeg { get; }
        public double MeanAnomalyAtEpochRad { get; }
        public double EpochSeconds { get; }
        public double ParentGravParameter { get; }     // m^3/s^2

        public KeplerOrbit(double semiMajorAxis, double eccentricity, double inclinationDeg, double longitudeOfAscendingNodeDeg,
            double argumentOfPeriapsisDeg, double meanAnomalyAtEpochRad, double epochSeconds, double parentGravParameter)
        {
            if (!(semiMajorAxis > 0)) throw new ArgumentOutOfRangeException(nameof(semiMajorAxis), "must be > 0");
            if (!(eccentricity >= 0 && eccentricity < 1)) throw new ArgumentOutOfRangeException(nameof(eccentricity), "must be in [0, 1)");
            if (!(parentGravParameter > 0)) throw new ArgumentOutOfRangeException(nameof(parentGravParameter), "must be > 0");
            SemiMajorAxis = semiMajorAxis;
            Eccentricity = eccentricity;
            InclinationDeg = inclinationDeg;
            LongitudeOfAscendingNodeDeg = longitudeOfAscendingNodeDeg;
            ArgumentOfPeriapsisDeg = argumentOfPeriapsisDeg;
            MeanAnomalyAtEpochRad = meanAnomalyAtEpochRad;
            EpochSeconds = epochSeconds;
            ParentGravParameter = parentGravParameter;
        }

        public double MeanMotion => Math.Sqrt(ParentGravParameter / (SemiMajorAxis * SemiMajorAxis * SemiMajorAxis));
        public double Period => 2 * Math.PI / MeanMotion;

        public StateVector StateAt(double ut)
        {
            double e = Eccentricity, a = SemiMajorAxis, n = MeanMotion;
            double m = MeanAnomalyAtEpochRad + n * (ut - EpochSeconds);
            m = m - 2 * Math.PI * Math.Floor(m / (2 * Math.PI));
            double bigE = SolveKepler(m, e);

            double cosE = Math.Cos(bigE), sinE = Math.Sin(bigE);
            double root = Math.Sqrt(1 - e * e);
            double denom = 1 - e * cosE;
            // Perifocal frame: x toward periapsis, y 90 degrees ahead in the direction of motion.
            double px = a * (cosE - e), py = a * root * sinE;
            double vx = -a * n * sinE / denom, vy = a * n * root * cosE / denom;

            return new StateVector(ToReference(px, py), ToReference(vx, vy));
        }

        // Rotate from the perifocal frame: Rz(LAN) * Rx(i) * Rz(argPe).
        private Vec3 ToReference(double x, double y)
        {
            const double deg = Math.PI / 180;
            double cw = Math.Cos(ArgumentOfPeriapsisDeg * deg), sw = Math.Sin(ArgumentOfPeriapsisDeg * deg);
            double ci = Math.Cos(InclinationDeg * deg), si = Math.Sin(InclinationDeg * deg);
            double co = Math.Cos(LongitudeOfAscendingNodeDeg * deg), so = Math.Sin(LongitudeOfAscendingNodeDeg * deg);

            double x1 = cw * x - sw * y, y1 = sw * x + cw * y;        // Rz(argPe)
            double y2 = ci * y1, z2 = si * y1;                         // Rx(i)
            return new Vec3(co * x1 - so * y2, so * x1 + co * y2, z2); // Rz(LAN)
        }

        /// <summary>Solves Kepler's equation E - e sin E = M for E (radians).</summary>
        public static double SolveKepler(double meanAnomaly, double e)
        {
            double bigE = e < 0.8 ? meanAnomaly : Math.PI;
            for (int i = 0; i < 50; i++)
            {
                double f = bigE - e * Math.Sin(bigE) - meanAnomaly;
                double step = f / (1 - e * Math.Cos(bigE));
                bigE -= step;
                if (Math.Abs(step) < 1e-14) break;
            }

            return bigE;
        }
    }

    /// <summary>A point that never moves. Used for "planets held still" estimates and tests.</summary>
    public sealed class FixedEphemeris : IEphemeris
    {
        private readonly Vec3 position;

        public FixedEphemeris(Vec3 position)
        {
            this.position = position;
        }

        public StateVector StateAt(double ut) => new StateVector(position, Vec3.Zero);
    }
}
