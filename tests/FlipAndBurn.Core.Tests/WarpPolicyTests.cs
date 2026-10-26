using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    public class WarpPolicyTests
    {
        private static GuidanceCommand Cmd(GuidancePhase phase, double toFlip = 0, double toArrival = 0, double toBurn = 0) =>
            new GuidanceCommand { Phase = phase, TimeToFlip = toFlip, TimeToArrival = toArrival, TimeToBurn = toBurn, Acceleration = 9.81 };

        [Fact]
        public void EngineFiring_PhysicsWarpCappedByTimeToFlip()
        {
            WarpDecision d = WarpPolicy.Decide(Cmd(GuidancePhase.Accelerate, toFlip: 3000), pointed: true);
            Assert.Equal(WarpMode.Physics, d.Mode);
            Assert.Equal(3000 / WarpPolicy.PhysicsLeadSeconds, d.MaxRate, 9);   // flip stays 30 real seconds away
        }

        [Fact]
        public void NearTheFlip_NormalSpeed()
        {
            WarpDecision d = WarpPolicy.Decide(Cmd(GuidancePhase.Accelerate, toFlip: 25), pointed: true);
            Assert.Equal(WarpMode.None, d.Mode);
            Assert.Equal(1, d.MaxRate);
        }

        [Fact]
        public void Flip_NormalSpeed()
        {
            Assert.Equal(WarpMode.None, WarpPolicy.Decide(Cmd(GuidancePhase.Flip), true).Mode);
        }

        [Fact]
        public void Braking_PhysicsWarpCappedByTimeToArrival()
        {
            WarpDecision d = WarpPolicy.Decide(Cmd(GuidancePhase.Decelerate, toArrival: 60_000), true);
            Assert.Equal(WarpMode.Physics, d.Mode);
            Assert.Equal(2000, d.MaxRate, 9);
        }

        [Fact]
        public void Coast_RailsWarpOnlyWhenPointed()
        {
            Assert.Equal(WarpMode.None, WarpPolicy.Decide(Cmd(GuidancePhase.Coast, toBurn: 500_000), pointed: false).Mode);
            WarpDecision d = WarpPolicy.Decide(Cmd(GuidancePhase.Coast, toBurn: 500_000), pointed: true);
            Assert.Equal(WarpMode.Rails, d.Mode);
            Assert.Equal(500_000 / WarpPolicy.RailsLeadSeconds, d.MaxRate, 9);
        }

        [Fact]
        public void CourseCorrectionAndCircularizing_NormalSpeed()
        {
            GuidanceCommand c = Cmd(GuidancePhase.Coast, toBurn: 500_000);
            c.Correcting = true;
            Assert.Equal(WarpMode.None, WarpPolicy.Decide(c, true).Mode);
            GuidanceCommand f = Cmd(GuidancePhase.FinalApproach, toArrival: 1000);
            f.Circularizing = true;
            Assert.Equal(WarpMode.None, WarpPolicy.Decide(f, true).Mode);
        }

        [Fact]
        public void PickIndex_HighestRateWithinBothLimits()
        {
            float[] rates = { 1, 2, 3, 4, 10, 25, 50 };
            Assert.Equal(4, WarpPolicy.PickIndex(rates, 12));
            Assert.Equal(6, WarpPolicy.PickIndex(rates, 1000));
            Assert.Equal(5, WarpPolicy.PickIndex(rates, 1000, cap: 25));
            Assert.Equal(0, WarpPolicy.PickIndex(rates, 1.5));
        }
    }
}
