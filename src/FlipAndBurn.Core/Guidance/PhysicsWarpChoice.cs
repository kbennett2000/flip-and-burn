using System;
using System.Globalization;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// The player's "Max physics warp" (ADR 0003, cycle 13): 10x (the default, steady hot or cool on Kris's machine with
    /// the Bebop), 25x or 50x. The heat rules (<see cref="HeatGuard"/>) and the <see cref="WarpStabilityGuard"/> apply at
    /// every setting.
    /// </summary>
    public static class PhysicsWarpChoice
    {
        public static readonly double[] Rates = { 10, 25, 50 };
        public static readonly string[] Names = { "10x", "25x", "50x" };
        public const int Default = 0;

        public static int Clamp(int index) => Math.Max(0, Math.Min(Rates.Length - 1, index));

        public static double RateFor(int index) => Rates[Clamp(index)];

        /// <summary>The line shown under the choice for 25x and 50x; null for 10x.</summary>
        public static string Warning(int index) => Clamp(index) == Default
            ? null
            : "Faster physics warp can shake big ships apart or overheat engines. Flip and Burn drops back as parts heat up, " +
              $"and to {Names[Default]} if the ship looks unstable.";

        /// <summary>
        /// The physics warp rates a trip hands the game: x1, x4 and x10 for the heat rules and the safe rate, then the top
        /// rate (25 for the 10x choice, as before, capped at 10 by the autopilot).
        /// </summary>
        public static float[] PhysicsRatesFor(double maxRate) => new[] { 1f, 4f, 10f, (float)Math.Max(25, maxRate) };

        public static string Format(double rate) => "x" + rate.ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Watches a ship at physics warp above x10 for signs it's coming apart, and drops the trip back to x10 for the rest of
    /// the trip, with the reason, when it sees one (ADR 0003, cycle 13):
    /// <list type="bullet">
    /// <item>part temperatures climbing so fast the hottest would reach the warm line (60 %) within
    /// <see cref="HeatLeadSeconds"/> real seconds;</item>
    /// <item>a part breaking off;</item>
    /// <item>the ship turning faster than <see cref="WobbleDegreesPerSecond"/> while it should hold steady, for
    /// <see cref="WobbleChecks"/> checks in a row.</item>
    /// </list>
    /// Fed once a real second.
    /// </summary>
    public sealed class WarpStabilityGuard
    {
        public const double SafeRate = 10;
        public const double HeatLeadSeconds = 10;
        public const double WobbleDegreesPerSecond = 3;
        public const int WobbleChecks = 2;

        private int startParts = -1, wobbles;
        private double lastHot = double.NaN, lastSeconds = double.NaN;

        /// <summary>Tripped: physics warp is held at <see cref="SafeRate"/> for the rest of the trip.</summary>
        public bool Tripped { get; private set; }
        /// <summary>Why it tripped, in plain words ("a part broke off the ship"); null if it hasn't.</summary>
        public string Reason { get; private set; }

        /// <summary>The cap on physics warp: <see cref="SafeRate"/> once tripped, otherwise none.</summary>
        public double Cap => Tripped ? SafeRate : double.MaxValue;

        /// <summary>A new trip (or the ship taken again): forget everything, and count parts from here.</summary>
        public void Reset(int partCount)
        {
            startParts = partCount;
            wobbles = 0;
            lastHot = lastSeconds = double.NaN;
            Tripped = false;
            Reason = null;
        }

        /// <summary>
        /// One check. Returns true the moment it trips (so the caller can tell the player once).
        /// </summary>
        /// <param name="realSeconds">Real time now (s).</param>
        /// <param name="warpRate">The physics warp rate in use.</param>
        /// <param name="hottestFraction">The hottest part's temperature as a share of its limit.</param>
        /// <param name="hottestPart">Its name, for the reason.</param>
        /// <param name="partCount">Parts on the ship now.</param>
        /// <param name="turnDegreesPerSecond">How fast the ship is turning.</param>
        /// <param name="turning">The guidance is turning the ship on purpose (the flip, a new direction): no wobble check.</param>
        public bool Check(double realSeconds, double warpRate, double hottestFraction, string hottestPart, int partCount,
            double turnDegreesPerSecond, bool turning)
        {
            double hotBefore = lastHot, secondsBefore = lastSeconds;
            lastHot = hottestFraction;
            lastSeconds = realSeconds;
            if (Tripped) return false;
            if (startParts < 0) startParts = partCount;
            if (!(warpRate > SafeRate + 0.01))
            {
                wobbles = 0;
                return false;
            }

            if (partCount < startParts)
                return Trip($"a part broke off the ship ({startParts - partCount} gone)");

            double dt = realSeconds - secondsBefore;
            if (dt > 0 && !double.IsNaN(hotBefore))
            {
                double rise = (hottestFraction - hotBefore) / dt;   // share of the limit per real second
                if (rise > 0 && hottestFraction < HeatGuard.WarmPart && hottestFraction + rise * HeatLeadSeconds >= HeatGuard.WarmPart)
                    return Trip($"{hottestPart} was heating fast ({hottestFraction * 100:0}% of its limit, up " +
                                $"{rise * 100:0.0} points a second)");
            }

            if (!turning && turnDegreesPerSecond > WobbleDegreesPerSecond)
            {
                if (++wobbles >= WobbleChecks)
                    return Trip($"the ship was wobbling (turning {turnDegreesPerSecond:0.0}° a second while it should hold steady)");
            }
            else wobbles = 0;

            return false;
        }

        private bool Trip(string reason)
        {
            Tripped = true;
            Reason = reason;
            return true;
        }
    }
}
