using System;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// Propellant that disappears while the engines aren't firing: boil-off from cryogenic tanks, or a drive drawing fuel
    /// while idle (ADR 0002, 2026-10-27 addendum). Fed the ship's propellant mass over time,
    /// it counts only the stretches when nothing fired and nothing was added, and gives the average loss rate.
    /// </summary>
    public sealed class BackgroundLoss
    {
        /// <summary>Game seconds of not firing needed before the rate counts as measured.</summary>
        public const double MinimumObserved = 3600;

        private double lastUT = double.NaN, lastMass = double.NaN;

        /// <summary>Propellant lost while not firing (kg) and over how long (s), so far.</summary>
        public double LostKg { get; private set; }
        public double ObservedSeconds { get; private set; }

        public bool Measured => ObservedSeconds >= MinimumObserved;

        /// <summary>The loss rate (kg/s), or 0 until measured.</summary>
        public double RateKgPerSecond => Measured ? Math.Max(0, LostKg / ObservedSeconds) : 0;

        /// <summary>Loss per game day (kg), for the player.</summary>
        public double KgPerDay(double dayLength) => RateKgPerSecond * dayLength;

        /// <summary>Start from an earlier estimate (saved with the ship).</summary>
        public void Restore(double lostKg, double observedSeconds)
        {
            LostKg = Math.Max(0, lostKg);
            ObservedSeconds = Math.Max(0, observedSeconds);
        }

        /// <summary>
        /// One sample: the time, the propellant on board (kg), and whether any engine fired since the last sample. A stretch
        /// with firing, or with propellant added (refuelling), only moves the starting point on.
        /// </summary>
        public void Sample(double ut, double propellantKg, bool firedSinceLast)
        {
            if (!double.IsNaN(lastUT) && !firedSinceLast && ut > lastUT && propellantKg <= lastMass)
            {
                LostKg += lastMass - propellantKg;
                ObservedSeconds += ut - lastUT;
            }

            lastUT = ut;
            lastMass = propellantKg;
        }

        /// <summary>
        /// The delta-v a ship loses when <paramref name="lostKg"/> of its propellant goes other than for thrust (m/s):
        /// its delta-v at <paramref name="acceleration"/> with the full load, less that with the lost propellant gone.
        /// </summary>
        public static double DeltaVCost(ShipPerformance ship, double acceleration, double lostKg)
        {
            if (ship == null || !(lostKg > 0)) return 0;
            double propellant = ship.StartMass - ship.EndMass;
            double left = Math.Max(0, propellant - lostKg);
            var lighter = new ShipPerformance(ship.FullThrust, ship.VacuumIsp, ship.IspThrottleExponent, ship.EndMass + left, ship.EndMass);
            return Math.Max(0, ship.DeltaVAtAcceleration(acceleration) - (left > 0 ? lighter.DeltaVAtAcceleration(acceleration) : 0));
        }
    }
}
