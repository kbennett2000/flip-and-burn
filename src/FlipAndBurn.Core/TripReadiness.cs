using System;

namespace FlipAndBurn.Core
{
    /// <summary>Can this ship fly this trip? (ADR 0002, decision 3: refuse trips the ship can't finish.)</summary>
    public sealed class TripReadiness
    {
        public bool CanHoldAcceleration { get; }
        public bool HasEnoughDeltaV { get; }
        public double RequestedAcceleration { get; }   // m/s^2
        public double MaxAcceleration { get; }         // m/s^2
        public double DeltaVNeeded { get; }            // m/s
        public double DeltaVAvailable { get; }         // m/s, while holding the requested acceleration

        public bool Ready => CanHoldAcceleration && HasEnoughDeltaV;
        public double DeltaVShortfall => Math.Max(0, DeltaVNeeded - DeltaVAvailable);

        private TripReadiness(bool canHold, bool enough, double requested, double max, double needed, double available)
        {
            CanHoldAcceleration = canHold;
            HasEnoughDeltaV = enough;
            RequestedAcceleration = requested;
            MaxAcceleration = max;
            DeltaVNeeded = needed;
            DeltaVAvailable = available;
        }

        public static TripReadiness Check(TripPlan plan, ShipPerformance ship, double requestedAcceleration)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (ship == null) throw new ArgumentNullException(nameof(ship));

            bool canHold = ship.HasThrust && requestedAcceleration <= ship.MaxAcceleration;
            double available = canHold ? ship.DeltaVAtAcceleration(requestedAcceleration) : 0;
            bool enough = plan.Feasible && available >= plan.TotalDeltaV;
            return new TripReadiness(canHold, enough, requestedAcceleration, ship.MaxAcceleration, plan.TotalDeltaV, available);
        }
    }
}
