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
            double clock = 0, flight = 0, maxLag = 0, nextNotch = 0, real = 0;
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
                var t = new FleetWarpHold.Trip { Name = "Bebop V2.4", Lag = lag, Pace = cutShort ? Pace(flight) : double.NaN, ClockToArrival = arrival - clock };
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
