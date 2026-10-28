using System;
using System.Globalization;

namespace FlipAndBurn.Core
{
    /// <summary>How the window writes numbers for the player.</summary>
    public static class Readout
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A speed: "850 m/s", "7.34 km/s", "73.4 km/s", "116 km/s".</summary>
        public static string Speed(double metresPerSecond) =>
            double.IsPositiveInfinity(metresPerSecond) ? "unlimited" : Speed(metresPerSecond, StepFor(metresPerSecond));

        /// <summary>A rate of electric charge: "850 a second", "12,500 a second", "1.9 million a second".</summary>
        public static string ChargeRate(double perSecond) => Math.Abs(perSecond) < 0.05 ? "nothing" : Charge(perSecond) + " a second";

        /// <summary>An amount of electric charge: "850", "12,500", "9.6 million".</summary>
        public static string Charge(double amount)
        {
            double a = Math.Abs(amount);
            if (a >= 1e6) return (amount / 1e6).ToString(a >= 1e8 ? "N0" : "N1", Inv) + " million";
            return amount.ToString(a >= 100 ? "N0" : "N1", Inv);
        }

        /// <summary>
        /// What the trip needs, what the ship has, and the shortfall, written to the same precision (the largest number's),
        /// with the shortfall taken from the two rounded numbers so the three always add up as written.
        /// </summary>
        public static (string Needed, string Available, string Short) Shortfall(double needed, double available)
        {
            double step = StepFor(Math.Max(Math.Abs(needed), Math.Abs(available)));
            double n = Round(needed, step), a = Round(available, step);
            return (Speed(n, step), Speed(a, step), Speed(Math.Max(0, n - a), step));
        }

        // The rounding step for a speed: 1 m/s under 1 km/s, then 10 m/s, 100 m/s and 1 km/s.
        private static double StepFor(double metresPerSecond)
        {
            double v = Math.Abs(metresPerSecond);
            if (Round(v, 1) < 1000) return 1;
            if (Round(v, 10) < 10_000) return 10;
            if (Round(v, 100) < 100_000) return 100;
            return 1000;
        }

        private static double Round(double value, double step) => Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

        private static string Speed(double metresPerSecond, double step)
        {
            double v = Round(metresPerSecond, step);
            if (step <= 1) return v.ToString("N0", Inv) + " m/s";
            double kms = v / 1000;
            string format = step <= 10 ? "N2" : step <= 100 ? "N1" : "N0";
            return kms.ToString(format, Inv) + " km/s";
        }
    }
}
