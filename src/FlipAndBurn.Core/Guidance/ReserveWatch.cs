using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// The reserve as the trip goes (ADR 0002, 2026-10-27 addendum): the ship's delta-v now against what the plan still
    /// needs. Delta-v burned comes off both sides, so flying the plan leaves the reserve where it was; propellant that goes
    /// without thrust (boil-off, a drive's idle use) comes off only what the ship has, and shows at once.
    /// </summary>
    public static class ReserveWatch
    {
        /// <summary>
        /// The delta-v expected to be left at arrival (m/s).
        /// </summary>
        /// <param name="have">The ship's delta-v now (m/s).</param>
        /// <param name="plannedNeeded">The delta-v the last plan (Start, a resume or a re-plan) needed from where it was made (m/s).</param>
        /// <param name="usedSincePlan">Delta-v burned since that plan (m/s), measured.</param>
        /// <param name="allowanceLeft">Delta-v still set aside for propellant lost while the engines are off (m/s).</param>
        public static double ExpectedAtArrival(double have, double plannedNeeded, double usedSincePlan, double allowanceLeft = 0) =>
            have - Math.Max(0, plannedNeeded - Math.Max(0, usedSincePlan)) - Math.Max(0, allowanceLeft);

        /// <summary>The share of a loss allowance still to come (0 to 1): the time left over the planned trip time.</summary>
        public static double AllowanceShareLeft(double timeToArrival, double plannedTripTime) =>
            plannedTripTime > 0 && timeToArrival >= 0 ? Math.Min(1, timeToArrival / plannedTripTime) : 0;
    }
}
