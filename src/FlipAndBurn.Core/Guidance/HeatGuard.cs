using System;

namespace FlipAndBurn.Core.Guidance
{
    public enum HeatStepDown
    {
        None,
        /// <summary>A part passed 90 % of its temperature limit: ease off to 80 % of the acceleration, to cool down.</summary>
        Overheating,
        /// <summary>At full throttle the engines give clearly less than asked, for a minute (KSP Interstellar drives lose power when hot).</summary>
        CantHold,
    }

    /// <summary>
    /// The heat rules from cycle 9 (ADR 0002, "When the engines can't hold the acceleration"; ADR 0003, "Time warp"), in one
    /// place so the trip, the rescue near the target (ADR 0003, "Arriving") and the simulator all use the same ones.
    /// </summary>
    public sealed class HeatGuard
    {
        /// <summary>Hottest part, as a share of its temperature limit, above which physics warp is held to 4x / to 1x.</summary>
        public const double WarmPart = 0.6, HotPart = 0.75;
        /// <summary>Above this share of its temperature limit a part is about to fail: step down to 80 % of the acceleration.</summary>
        public const double OverheatingPart = 0.9;
        public const double OverheatStepDown = 0.8;
        /// <summary>After an overheating step-down, give the parts this long (game s) to cool before stepping down again.</summary>
        public const double CoolDownSeconds = 60;
        /// <summary>At full throttle, below this share of the asked acceleration for <see cref="CantHoldSeconds"/>: the engines can't hold it.</summary>
        public const double CantHoldShare = 0.95;
        public const double CantHoldSeconds = 60;

        private double cooldownUntil = double.NaN, weakSince = double.NaN;

        /// <summary>The highest physics warp allowed with the hottest part at this share of its limit.</summary>
        public static double WarpCap(double hottestFraction) =>
            hottestFraction > HotPart ? 1 : hottestFraction > WarmPart ? 4 : double.MaxValue;

        public void Reset()
        {
            cooldownUntil = weakSince = double.NaN;
        }

        /// <summary>
        /// One physics step. Returns a step-down and the acceleration to step down to (m/s^2), or <see cref="HeatStepDown.None"/>.
        /// </summary>
        /// <param name="hottestFraction">The hottest part's temperature as a share of its limit (0 with KSP's "ignore max temperature" cheat on).</param>
        /// <param name="holding">The acceleration being flown (m/s^2): an overheating step-down goes to 80 % of it.</param>
        /// <param name="asked">The acceleration asked for this step (m/s^2).</param>
        /// <param name="measured">The thrust acceleration felt this step (m/s^2).</param>
        /// <param name="thrusting">Thrust is wanted, outside the flip.</param>
        /// <param name="pointed">... and the ship points where it should.</param>
        public HeatStepDown Check(double ut, double hottestFraction, double holding, double asked, double measured, bool fullThrottle,
            bool thrusting, bool pointed, out double stepTo)
        {
            stepTo = double.NaN;
            if (thrusting && hottestFraction > OverheatingPart && double.IsNaN(cooldownUntil))
            {
                cooldownUntil = ut + CoolDownSeconds;
                weakSince = double.NaN;
                stepTo = OverheatStepDown * holding;
                return HeatStepDown.Overheating;
            }

            if (!double.IsNaN(cooldownUntil) && ut > cooldownUntil) cooldownUntil = double.NaN;

            if (thrusting && pointed && fullThrottle && measured < CantHoldShare * asked && measured >= 0.1 * asked)
            {
                if (double.IsNaN(weakSince)) weakSince = ut;
                if (ut - weakSince > CantHoldSeconds)
                {
                    weakSince = double.NaN;
                    stepTo = CantHoldShare * measured;
                    return HeatStepDown.CantHold;
                }
            }
            else weakSince = double.NaN;

            return HeatStepDown.None;
        }
    }
}
