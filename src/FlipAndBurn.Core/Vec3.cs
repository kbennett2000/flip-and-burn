using System;

namespace FlipAndBurn.Core
{
    /// <summary>A 3D vector of doubles. Units are whatever the caller uses (metres, m/s).</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public bool IsFinite => !(double.IsNaN(X) || double.IsNaN(Y) || double.IsNaN(Z) || double.IsInfinity(X) || double.IsInfinity(Y) || double.IsInfinity(Z));

        /// <summary>Unit vector in this direction, or zero if this vector is zero.</summary>
        public Vec3 Normalized()
        {
            double len = Length;
            return len > 0 ? this / len : Zero;
        }

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Angle between two vectors in radians (0 if either is zero).</summary>
        public static double Angle(Vec3 a, Vec3 b)
        {
            double la = a.Length, lb = b.Length;
            if (la == 0 || lb == 0) return 0;
            return Math.Acos(Math.Max(-1, Math.Min(1, Dot(a, b) / (la * lb))));
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => a * s;
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() ^ (Y.GetHashCode() * 397) ^ (Z.GetHashCode() * 7919);
        public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }

    /// <summary>Position and velocity at one moment.</summary>
    public readonly struct StateVector
    {
        public readonly Vec3 Position;
        public readonly Vec3 Velocity;

        public StateVector(Vec3 position, Vec3 velocity)
        {
            Position = position;
            Velocity = velocity;
        }
    }
}
