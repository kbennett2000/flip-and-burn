using System;
using System.Collections.Generic;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// How fast time warp may go while ships fly trips on their own (ADR 0006, Kris's decision on the warp hold: hold warp,
    /// and say which ship is holding it). Every frame each trip gets a few milliseconds to fly; at a warp faster than it can
    /// fly in that time it falls behind the game clock, its map position goes stale and its arrival message comes late (a
    /// trip to Dres once finished 3.5 hours late at 100,000x, when warp only stepped down a notch every half second). So
    /// warp drops straight to a rate every trip can keep up with, and slows ahead of each arrival so it lands on the clock.
    /// </summary>
    public static class FleetWarpHold
    {
        /// <summary>A trip this far behind the game clock (game s) has warp held down until it catches up.</summary>
        public const double LagLimit = 60;

        /// <summary>Behind by more than <see cref="LagLimit"/>: warp at this share of what the trip can fly, so it catches up.</summary>
        public const double CatchUpShare = 0.5;

        /// <summary>Falling behind (it ran out of time this frame and flies slower than the warp): warp at this share of it.</summary>
        public const double KeepUpShare = 0.9;

        /// <summary>Warp drops straight to the rate allowed, but climbs back a notch at a time, each after every trip has kept
        /// up this many real seconds (so it doesn't bounce between a rate a trip can't fly and one it can).</summary>
        public const double RiseAfterSeconds = 1.0;

        /// <summary>Ahead of an arrival, warp is held so the arrival is at least this many real seconds away.</summary>
        public const double ArrivalLeadSeconds = 10;

        /// <summary>One trip as the fleet sees it this frame.</summary>
        public struct Trip
        {
            /// <summary>The ship's name, for the message saying who holds warp.</summary>
            public string Name;

            /// <summary>Game seconds the trip's flight is behind the game clock.</summary>
            public double Lag;

            /// <summary>
            /// Game seconds the trip flies per real second when it uses all the time it gets (measured; NaN when it kept up
            /// this frame, so its pace isn't the limit).
            /// </summary>
            public double Pace;

            /// <summary>Game seconds until the game clock reaches the trip's expected arrival (NaN when not known).</summary>
            public double ClockToArrival;
        }

        /// <summary>Why warp is held: falling behind, catching up, or slowing for an arrival.</summary>
        public enum Reason
        {
            None,
            KeepingUp,
            CatchingUp,
            Arriving,
        }

        /// <summary>The fastest warp (game s per real s) one trip allows now, and why.</summary>
        public static double AllowedRate(Trip t, double currentRate, out Reason reason)
        {
            double allowed = double.PositiveInfinity;
            reason = Reason.None;
            if (t.Pace >= 0)
            {
                if (t.Lag > LagLimit)
                {
                    allowed = CatchUpShare * t.Pace;
                    reason = Reason.CatchingUp;
                }
                else if (t.Pace < currentRate)
                {
                    allowed = KeepUpShare * t.Pace;
                    reason = Reason.KeepingUp;
                }
            }

            if (!double.IsNaN(t.ClockToArrival))
            {
                double forArrival = Math.Max(0, t.ClockToArrival) / ArrivalLeadSeconds;
                if (forArrival < allowed)
                {
                    allowed = forArrival;
                    reason = Reason.Arriving;
                }
            }

            return allowed;
        }

        /// <summary>
        /// The fleet's cap on time warp, kept from frame to frame: drops straight to the fastest rate every trip allows, climbs
        /// back a notch at a time once they all keep up.
        /// </summary>
        public sealed class Cap
        {
            private double risingFor;

            /// <summary>The fastest rate index allowed (KSP's warp rates); the top one when nothing holds warp.</summary>
            public int Index { get; private set; } = int.MaxValue;
            /// <summary>The trip holding warp down (the slowest), and why; null and None when nothing does.</summary>
            public string HeldBy { get; private set; }
            public Reason Why { get; private set; }

            public bool Holding(int topIndex) => Index < topIndex;

            /// <summary>Update the cap for this frame (<paramref name="realSeconds"/> since the last) and return it.</summary>
            public int Update(IReadOnlyList<float> rates, double currentRate, IEnumerable<Trip> trips, double realSeconds)
            {
                int top = rates.Count - 1;
                if (Index > top) Index = top;
                double allowed = double.PositiveInfinity;
                string by = null;
                Reason why = Reason.None;
                foreach (Trip t in trips)
                {
                    double a = AllowedRate(t, currentRate, out Reason r);
                    if (a < allowed) { allowed = a; by = t.Name; why = r; }
                }

                int want = RateIndex(rates, allowed);
                if (want < Index)
                {
                    Index = want;
                    risingFor = 0;
                    HeldBy = by;
                    Why = why;
                }
                else if (want > Index)
                {
                    risingFor += realSeconds;
                    if (risingFor >= RiseAfterSeconds)
                    {
                        Index++;
                        risingFor = 0;
                    }
                }
                else risingFor = 0;

                if (Index >= top) { HeldBy = null; Why = Reason.None; }
                else if (by != null && want == Index) { HeldBy = by; Why = why; }
                return Index;
            }
        }

        /// <summary>The fastest of <paramref name="rates"/> (KSP's warp rates, slowest first) at or under <paramref name="allowed"/>; 0 at least.</summary>
        public static int RateIndex(IReadOnlyList<float> rates, double allowed)
        {
            int best = 0;
            for (int i = 1; i < rates.Count; i++)
                if (rates[i] <= allowed) best = i;
            return best;
        }
    }
}
