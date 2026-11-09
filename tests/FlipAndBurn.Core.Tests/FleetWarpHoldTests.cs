using System;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The warp hold for ships flying on their own (ADR 0006): warp drops straight to a rate every trip can keep up with.</summary>
    public class FleetWarpHoldTests
    {
        private readonly ITestOutputHelper output;
        public FleetWarpHoldTests(ITestOutputHelper output) { this.output = output; }

        // KSP's rails warp rates.
        private static readonly float[] Rates = { 1, 5, 10, 50, 100, 1000, 10000, 100000 };

        [Fact]
        public void Cap_DropsStraight_ClimbsANotchASecond()
        {
            var cap = new FleetWarpHold.Cap();
            var slow = new FleetWarpHold.Trip { Name = "V2.4", Lag = 5, Pace = 2000, ClockToArrival = double.NaN };
            Assert.Equal(5, cap.Update(Rates, 100000, new[] { slow }, 0.02));   // 1,000x (90 % of its pace), straight from 100,000x
            Assert.Equal("V2.4", cap.HeldBy);
            Assert.Equal(FleetWarpHold.Reason.KeepingUp, cap.Why);
            var keeping = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, ClockToArrival = double.NaN };
            Assert.Equal(5, cap.Update(Rates, 1000, new[] { keeping }, 0.5));
            Assert.Equal(6, cap.Update(Rates, 1000, new[] { keeping }, 0.5));   // a notch after a second of keeping up
            Assert.Equal(6, cap.Update(Rates, 10000, new[] { keeping }, 0.9));
            Assert.Equal(7, cap.Update(Rates, 10000, new[] { keeping }, 0.2));
            Assert.False(cap.Holding(7));
            Assert.Null(cap.HeldBy);
        }

        [Fact]
        public void RecentPace_KeepsWarpUnderIt_ThenTriesANotch()
        {
            // Dropped for a trip flying 500x; it has caught up. While that pace is recent, warp stays under it (100x); once
            // it is forgotten, warp tries a notch faster.
            var cap = new FleetWarpHold.Cap();
            var dropped = new FleetWarpHold.Trip { Name = "V2.4", Lag = 20, Pace = 500, RecentPace = 500, ClockToArrival = double.NaN };
            Assert.Equal(4, cap.Update(Rates, 10000, new[] { dropped }, 0.02));
            var recent = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, RecentPace = 500, ClockToArrival = double.NaN };
            for (int i = 0; i < 20; i++) Assert.Equal(4, cap.Update(Rates, 100, new[] { recent }, 0.5));
            var forgotten = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, RecentPace = double.NaN, ClockToArrival = double.NaN };
            Assert.Equal(4, cap.Update(Rates, 100, new[] { forgotten }, 0.5));
            Assert.Equal(5, cap.Update(Rates, 100, new[] { forgotten }, 0.5));
        }

        [Fact]
        public void Cap_ClimbsOnlyOnceWarpRunsAtTheNotch()
        {
            var cap = new FleetWarpHold.Cap();
            var slow = new FleetWarpHold.Trip { Name = "V2.4", Lag = 5, Pace = 600, ClockToArrival = double.NaN };
            Assert.Equal(4, cap.Update(Rates, 100000, new[] { slow }, 0.02));   // 100x
            var keeping = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, ClockToArrival = double.NaN };
            Assert.Equal(5, cap.Update(Rates, 100, new[] { keeping }, 1.0));    // a notch: 1,000x allowed
            // KSP is still ramping up to 1,000x: no further climb however long that takes.
            for (int i = 0; i < 5; i++) Assert.Equal(5, cap.Update(Rates, 400, new[] { keeping }, 1.0));
            Assert.Equal(5, cap.Update(Rates, 1000, new[] { keeping }, 0.5));
            Assert.Equal(6, cap.Update(Rates, 1000, new[] { keeping }, 0.5));   // a second at 1,000x, keeping up
        }

        [Fact]
        public void NextBurn_SlowsAhead()
        {
            // Coasting, keeping up easily; a burn in 20,000 game s: no faster than 2,000x, so 1,000x.
            var t = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, ClockToArrival = 4e6, ClockToNextBurn = 20000 };
            double allowed = FleetWarpHold.AllowedRate(t, 100000, out FleetWarpHold.Reason why);
            Assert.Equal(FleetWarpHold.Reason.NextBurn, why);
            Assert.Equal(5, FleetWarpHold.RateIndex(Rates, allowed));
        }

        [Fact]
        public void ComputePace_WarpNeverClimbsPastIt()
        {
            // Keeping up at 100x, but its computing time says it could fly only about 1,000x: warp may go to 100x, not 1,000x
            // (90 % of 1,000), and never tries 10,000x to find out.
            var cap = new FleetWarpHold.Cap();
            var t = new FleetWarpHold.Trip { Name = "V2.4", Lag = 0, Pace = double.NaN, ComputePace = 1000, ClockToArrival = double.NaN };
            Assert.Equal(4, cap.Update(Rates, 100000, new[] { t }, 0.02));
            for (int i = 0; i < 30; i++) Assert.Equal(4, cap.Update(Rates, 100, new[] { t }, 0.5));
            // In the cruise it could fly 700,000x: warp climbs back a notch a second to the top.
            t.ComputePace = 700_000;
            for (int i = 0; i < 6; i++) cap.Update(Rates, Rates[Math.Min(7, cap.Index)], new[] { t }, 1.0);
            Assert.Equal(7, cap.Index);
        }

        /// <summary>
        /// Leaving a planet, the ship's pace wanders between 550 and 1,500 game s per real s while the player asks for
        /// 100,000x. Without the computing-time estimate, warp tries a notch faster ten seconds after each drop and a frame at
        /// 10,000x puts the ship over a minute behind (in game: 93 and 136 s); with it, warp stays under what the ship can fly.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LeavingAPlanet_NoBlindClimbs(bool computePace)
        {
            const double frame = 1.0 / 60;
            double Pace(double ut) => ut < 2e5 ? 1000 + 450 * Math.Sin(ut / 3000) : 700_000;
            double clock = 0, flight = 0, maxLag = 0, real = 0, pacedAt = -100, lastPace = double.NaN;
            int index = 0;   // warp starts at normal speed; the player asks for the top rate and gets the cap
            var cap = new FleetWarpHold.Cap();
            var rng = new Random(7);
            while (flight < 3e5 && real < 3600)
            {
                double rate = Rates[index];
                clock += rate * frame;
                real += frame;
                double can = Pace(flight) * frame;
                bool cutShort = clock - flight > can;
                flight = Math.Min(clock, flight + can);
                double lag = clock - flight;
                maxLag = Math.Max(maxLag, lag);
                if (cutShort) { lastPace = Pace(flight); pacedAt = real; }
                var t = new FleetWarpHold.Trip
                {
                    Name = "V2.4", Lag = lag, Pace = cutShort ? Pace(flight) : double.NaN,
                    RecentPace = real - pacedAt < FleetWarpHold.PaceMemorySeconds ? lastPace : double.NaN,
                    ComputePace = computePace ? Pace(flight) * (0.9 + 0.2 * rng.NextDouble()) : double.NaN,   // within 10 %
                    ClockToArrival = 4.5e6 - clock,
                };
                index = Math.Min(Rates.Length - 1, cap.Update(Rates, rate, new[] { t }, frame));
            }

            output.WriteLine($"{(computePace ? "with" : "without")} the computing-time estimate: furthest behind {maxLag:F0} s of game time");
            if (computePace) Assert.InRange(maxLag, 0, 10);
            else Assert.True(maxLag > 60, "without it, the blind climbs should show");
        }

        [Fact]
        public void KeepingUp_NoHold()
        {
            var t = new FleetWarpHold.Trip { Name = "Bebop", Lag = 0, Pace = double.NaN, ClockToArrival = 1e9 };
            Assert.Equal(7, FleetWarpHold.RateIndex(Rates, FleetWarpHold.AllowedRate(t, 100000, out _)));
            var cap = new FleetWarpHold.Cap();
            Assert.Equal(7, cap.Update(Rates, 100000, new[] { t }, 0.02));
            Assert.False(cap.Holding(7));
            Assert.Null(cap.HeldBy);
        }

        [Fact]
        public void FallingBehind_DropsStraightToARateItCanFly()
        {
            // Flies 20,000 game s a real second: from 100,000x straight to 10,000x (not one notch, to 10,000x, and on).
            var t = new FleetWarpHold.Trip { Lag = 5, Pace = 20000, ClockToArrival = 1e9 };
            double allowed = FleetWarpHold.AllowedRate(t, 100000, out FleetWarpHold.Reason why);
            Assert.Equal(FleetWarpHold.Reason.KeepingUp, why);
            Assert.Equal(6, FleetWarpHold.RateIndex(Rates, allowed));
        }

        [Fact]
        public void FarBehind_CatchesUp()
        {
            // Over a minute behind: half its pace, so it gains on the clock.
            var t = new FleetWarpHold.Trip { Lag = 500, Pace = 15000, ClockToArrival = 1e9 };
            double allowed = FleetWarpHold.AllowedRate(t, 10000, out FleetWarpHold.Reason why);
            Assert.Equal(FleetWarpHold.Reason.CatchingUp, why);
            Assert.Equal(5, FleetWarpHold.RateIndex(Rates, allowed));   // 1,000x: under 7,500
        }

        [Fact]
        public void Arrival_SlowsAhead()
        {
            // Arriving in an hour of game time: no faster than 360x, so the arrival is 10 real seconds away.
            var t = new FleetWarpHold.Trip { Lag = 0, Pace = double.NaN, ClockToArrival = 3600 };
            double allowed = FleetWarpHold.AllowedRate(t, 100000, out FleetWarpHold.Reason why);
            Assert.Equal(FleetWarpHold.Reason.Arriving, why);
            Assert.Equal(4, FleetWarpHold.RateIndex(Rates, allowed));   // 100x
            var past = new FleetWarpHold.Trip { Lag = 30, Pace = double.NaN, ClockToArrival = -30 };
            Assert.Equal(0, FleetWarpHold.RateIndex(Rates, FleetWarpHold.AllowedRate(past, 100, out _)));
        }

        /// <summary>
        /// A Dres-type arrival with the player holding 100,000x all the way: a slow coast, then the final approach in small
        /// steps, then the last two minutes in physics steps. The ship's pace (game s it flies per real s) drops with its step
        /// size. With the old hold (a notch every half second) it fell hours behind; with this one it never gets more than a
        /// minute behind and arrives on the clock.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DresTypeArrival_At100000x(bool oldHold)
        {
            const double frame = 1.0 / 60;                      // real s
            double arrival = 4.5e6, finalApproach = 2e5;         // game s from the start: 52 days, the last 2.3 days in Dres's sphere
            double Pace(double ut) => ut < arrival - finalApproach ? 700_000 : ut < arrival - 120 ? 12_000 : 2_500;
            double clock = 0, flight = 0, maxLag = 0, nextNotch = 0, real = 0, pacedAt = -100, lastPace = double.NaN;
            int index = Rates.Length - 1;
            var cap = new FleetWarpHold.Cap();
            while (flight < arrival && real < 3600)
            {
                // The player (or the test helper) keeps asking for the top rate whenever nothing holds it.
                double rate = Rates[index];
                clock += rate * frame;
                real += frame;
                double can = Pace(flight) * frame;
                bool cutShort = clock - flight > can;
                flight = Math.Min(clock, flight + can);
                double lag = clock - flight;
                maxLag = Math.Max(maxLag, lag);
                if (cutShort) { lastPace = Pace(flight); pacedAt = real; }
                var t = new FleetWarpHold.Trip
                {
                    Name = "Bebop V2.4", Lag = lag, Pace = cutShort ? Pace(flight) : double.NaN,
                    RecentPace = real - pacedAt < FleetWarpHold.PaceMemorySeconds ? lastPace : double.NaN, ClockToArrival = arrival - clock,
                };
                if (oldHold)
                {
                    bool behind = lag > FleetWarpHold.LagLimit || (arrival - flight > 0 && arrival - flight < 5 * rate);
                    if (behind && index > 0 && real >= nextNotch) { index--; nextNotch = real + 0.5; }
                    else if (!behind && index < Rates.Length - 1 && real >= nextNotch) { index++; nextNotch = real + 0.5; }
                }
                else
                {
                    // The player keeps asking for the top rate: they get the cap.
                    index = Math.Min(Rates.Length - 1, cap.Update(Rates, rate, new[] { t }, frame));
                }
            }

            double late = clock - arrival;
            output.WriteLine($"{(oldHold ? "old hold (a notch every 0.5 s)" : "this hold")}: furthest behind {maxLag:F0} s of game time; " +
                             $"arrived {late:F0} s after the clock reached the arrival; {real:F0} real s");
            if (oldHold) Assert.True(maxLag > 3600, "the old hold should show the problem");
            else
            {
                Assert.InRange(maxLag, 0, FleetWarpHold.LagLimit * 2);
                Assert.InRange(late, 0, 5);
            }
        }
    }
}
