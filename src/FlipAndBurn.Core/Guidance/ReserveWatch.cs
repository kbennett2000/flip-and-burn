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

        /// <summary>
        /// What flying a coast plan typically costs over its two planned burns: course corrections, steering and the final
        /// settling into orbit. The plan allows 6 % (<see cref="CoastPlanner.GuidanceMargin"/>) to be safe; measured in the
        /// game, the trips used 2.7 % over their burns (trip B, 0.4 g: 64,044 of 66,058 m/s planned with the margin; the
        /// 1 g trip in the guide: 101 of 104 km/s). The player is shown the realistic figure; the autopilot's own
        /// decisions (re-plan, refuse) keep the whole margin.
        /// </summary>
        public const double TypicalCoastOverhead = 0.027;

        /// <summary>
        /// The delta-v a plan will realistically use (m/s), from the figure the planner gives (<paramref name="plannedNeeded"/>):
        /// a coast plan's figure includes the whole guidance margin, of which a trip typically uses
        /// <see cref="TypicalCoastOverhead"/>; a full burn's has no margin and is flown as planned.
        /// </summary>
        public static double ExpectedUse(double plannedNeeded, bool coastTrip) =>
            coastTrip ? plannedNeeded * (1 + TypicalCoastOverhead) / (1 + CoastPlanner.GuidanceMargin) : plannedNeeded;

        /// <summary>
        /// The delta-v the rest of the trip will realistically use (m/s), from the burning the guidance has left
        /// (<see cref="FlipAndBurnGuidance.BurnTimeLeft"/>) at the acceleration being flown, plus the small cost of steering
        /// and the last settling into orbit (<see cref="FlyingOverhead"/>).
        /// </summary>
        public static double RemainingUse(double acceleration, double burnTimeLeft, bool coastTrip) =>
            acceleration * Math.Max(0, burnTimeLeft) * (1 + (coastTrip ? FlyingOverhead : 0));

        /// <summary>
        /// What course corrections and the final settling into orbit add to a coast trip's remaining burns, measured in the
        /// simulator (0.5 % to Duna, 1.7 % to Jool). A full burn is flown within 0.1 % of its plan and gets none.
        /// </summary>
        public const double FlyingOverhead = 0.01;

        /// <summary>The share of a loss allowance still to come (0 to 1): the time left over the planned trip time.</summary>
        public static double AllowanceShareLeft(double timeToArrival, double plannedTripTime) =>
            plannedTripTime > 0 && timeToArrival >= 0 ? Math.Min(1, timeToArrival / plannedTripTime) : 0;
    }
}
