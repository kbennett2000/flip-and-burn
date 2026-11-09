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

        /// <summary>
        /// A pace measured this recently (real s) still holds warp under it once the trip has caught up: warp tries a notch
        /// faster only after that, so it doesn't climb straight back past what the trip can fly (in game, run 13: climbing a
        /// notch a second after each drop reached 10,000x for a trip flying 500x and put it 258 s behind).
        /// </summary>
        public const double PaceMemorySeconds = 10;

        /// <summary>Ahead of an arrival, or of the trip's next burn or flip (where its pace can drop a hundredfold: from coasting
        /// in big steps to burning in small ones), warp is held so it is at least this many real seconds away. In game, run 15:
        /// one frame at 10,000x as a coast gave way to a burn put a trip 153 s behind.</summary>
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

            /// <summary>The pace measured within the last <see cref="PaceMemorySeconds"/> (NaN or 0 when none): keeps warp under it.</summary>
            public double RecentPace;

            /// <summary>
            /// The pace the trip could fly with its share of the frame, worked out every frame from the computing time it used
            /// for the game seconds it flew (NaN or 0 when not known). Known even while the trip keeps up, so warp never climbs
            /// past it to find out: in game (runs 16 and 19), trying a notch faster reached 10,000x for a ship flying about
            /// 1,000x as it left Duna, and one frame there put it 93 to 136 s behind.
            /// </summary>
            public double ComputePace;

            /// <summary>Game seconds until the game clock reaches the trip's expected arrival (NaN when not known).</summary>
            public double ClockToArrival;

            /// <summary>Game seconds until the game clock reaches the trip's next burn or flip (NaN or 0 when none is due).</summary>
            public double ClockToNextBurn;
        }

        /// <summary>Why warp is held: falling behind, catching up, or slowing for an arrival.</summary>
        public enum Reason
        {
            None,
            KeepingUp,
            CatchingUp,
            Arriving,
            NextBurn,
        }

        /// <summary>The fastest warp (game s per real s) one trip allows now, and why.</summary>
        public static double AllowedRate(Trip t, double currentRate, out Reason reason)
        {
            double allowed = double.PositiveInfinity;
            reason = Reason.None;
            if (t.Pace > 0)
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
            else if (t.RecentPace > 0)
            {
                allowed = KeepUpShare * t.RecentPace;
                reason = Reason.KeepingUp;
            }

            if (t.ComputePace > 0 && KeepUpShare * t.ComputePace < allowed)
            {
                allowed = KeepUpShare * t.ComputePace;
                reason = Reason.KeepingUp;
            }

            if (t.ClockToNextBurn > 0)
            {
                double forBurn = t.ClockToNextBurn / ArrivalLeadSeconds;
                if (forBurn < allowed)
                {
                    allowed = forBurn;
                    reason = Reason.NextBurn;
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
                    // Only once warp really runs at this notch (KSP ramps up to a new rate) do the trips show whether they
                    // keep up with it; a notch faster after a second of that (in game, run 14: counted from the request,
                    // warp went from 100x to 10,000x in two seconds for a trip flying 600x, 139 s behind).
                    if (currentRate >= 0.99 * rates[Math.Min(Index, top)]) risingFor += realSeconds;
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
