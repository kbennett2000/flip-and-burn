using System;

namespace FlipAndBurn.Core
{
    /// <summary>How the default parking orbit altitude is picked. Recorded in ADR 0002 (addendum).</summary>
    public static class ParkingOrbit
    {
        public const double RoundTo = 10_000;          // m
        public const double AtmosphereMargin = 0.10;   // 10 % above the top of the atmosphere
        public const double TerrainClearance = 10_000; // m above the highest terrain
        public const double MinimumAltitude = 10_000;  // m

        /// <summary>
        /// Default parking altitude (m) for a planet.
        /// With an atmosphere: 10 % above its top. Without: 10 km above the highest terrain (or 10 % of the radius when
        /// the terrain height is unknown). Rounded up to the next 10 km, at least 10 km, and kept inside the sphere of
        /// influence.
        /// </summary>
        public static double DefaultAltitude(double radius, double atmosphereDepth, double maxTerrainHeight, double sphereOfInfluence)
        {
            double raw;
            if (atmosphereDepth > 0) raw = atmosphereDepth * (1 + AtmosphereMargin);
            else if (maxTerrainHeight > 0) raw = maxTerrainHeight + TerrainClearance;
            else raw = 0.1 * radius;

            // The small tolerance stops float noise (200 km * 1.1 = 220000.00000000003) from rounding up a whole step.
            double alt = Math.Max(MinimumAltitude, Math.Ceiling(raw / RoundTo - 1e-9) * RoundTo);
            if (sphereOfInfluence > radius)
                alt = Math.Min(alt, 0.5 * (sphereOfInfluence - radius));
            return alt;
        }
    }
}
