using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The "Max physics warp" choice and the stability guard (ADR 0003, cycle 13 addendum).</summary>
    public class PhysicsWarpTests
    {
        [Theory]
        [InlineData(0, 10)]
        [InlineData(1, 25)]
        [InlineData(2, 50)]
        [InlineData(-3, 10)]   // a damaged settings file reads as the default
        [InlineData(9, 50)]
        public void Choice_Rates(int index, double rate) => Assert.Equal(rate, PhysicsWarpChoice.RateFor(index));

        [Fact]
        public void Choice_WarningOnlyAboveTenX()
        {
            Assert.Null(PhysicsWarpChoice.Warning(0));
            Assert.StartsWith("Faster physics warp can shake big ships apart or overheat engines.", PhysicsWarpChoice.Warning(1));
            Assert.Equal(PhysicsWarpChoice.Warning(1), PhysicsWarpChoice.Warning(2));
        }

        [Fact]
        public void Choice_RateTable_KeepsTheHeatRatesAndTheTop()
        {
            Assert.Equal(new[] { 1f, 4f, 10f, 25f }, PhysicsWarpChoice.PhysicsRatesFor(10));   // as before cycle 13
            Assert.Equal(new[] { 1f, 4f, 10f, 25f }, PhysicsWarpChoice.PhysicsRatesFor(25));
            Assert.Equal(new[] { 1f, 4f, 10f, 50f }, PhysicsWarpChoice.PhysicsRatesFor(50));
        }

        [Fact]
        public void Choice_TheHeatRulesStillApplyAtEverySetting()
        {
            // HeatGuard's caps don't depend on the choice: x4 above 60 %, normal speed above 75 %.
            Assert.Equal(4, HeatGuard.WarpCap(0.61));
            Assert.Equal(1, HeatGuard.WarpCap(0.76));
            Assert.Equal(double.MaxValue, HeatGuard.WarpCap(0.5));
        }

        private static WarpStabilityGuard Fresh(int parts = 40)
        {
            var g = new WarpStabilityGuard();
            g.Reset(parts);
            return g;
        }

        [Fact]
        public void Stability_SteadyShip_NeverTrips()
        {
            WarpStabilityGuard g = Fresh();
            for (int s = 0; s < 120; s++)
                Assert.False(g.Check(s, 50, 0.30 + s * 0.0005, "Kerbstein drive", 40, 0.2, false));   // +0.05 points a second
            Assert.False(g.Tripped);
            Assert.Equal(double.MaxValue, g.Cap);
        }

        [Fact]
        public void Stability_PartBreaksOff_DropsToTen()
        {
            WarpStabilityGuard g = Fresh();
            Assert.False(g.Check(0, 50, 0.3, "drive", 40, 0, false));
            Assert.True(g.Check(1, 50, 0.3, "drive", 39, 0, false));
            Assert.True(g.Tripped);
            Assert.Equal(10, g.Cap);
            Assert.Equal("a part broke off the ship (1 gone)", g.Reason);
            Assert.False(g.Check(2, 10, 0.3, "drive", 39, 0, false));   // tells the player once
        }

        [Fact]
        public void Stability_TemperatureClimbingFast_DropsToTen()
        {
            WarpStabilityGuard g = Fresh();
            Assert.False(g.Check(0, 25, 0.40, "Kerbstein drive", 40, 0, false));
            // +3 points a second: would reach 60 % in under 10 seconds.
            Assert.True(g.Check(1, 25, 0.43, "Kerbstein drive", 40, 0, false));
            Assert.Equal("Kerbstein drive was heating fast (43% of its limit, up 3.0 points a second)", g.Reason);
        }

        [Fact]
        public void Stability_SlowWarmUp_LeftToTheHeatRules()
        {
            WarpStabilityGuard g = Fresh();
            Assert.False(g.Check(0, 50, 0.40, "drive", 40, 0, false));
            Assert.False(g.Check(1, 50, 0.41, "drive", 40, 0, false));   // 60 % is 19 s away at this rate
            // Already past the warm line: the heat rules hold warp at x4 / normal speed; nothing for this guard to add.
            Assert.False(g.Check(2, 50, 0.65, "drive", 40, 0, false));
        }

        [Fact]
        public void Stability_Wobble_TwoChecksInARow()
        {
            WarpStabilityGuard g = Fresh();
            Assert.False(g.Check(0, 50, 0.3, "drive", 40, 5, false));
            Assert.False(g.Check(1, 50, 0.3, "drive", 40, 0.5, false));   // a single twitch resets
            Assert.False(g.Check(2, 50, 0.3, "drive", 40, 5, false));
            Assert.True(g.Check(3, 50, 0.3, "drive", 40, 4.2, false));
            Assert.Equal("the ship was wobbling (turning 4.2° a second while it should hold steady)", g.Reason);
        }

        [Fact]
        public void Stability_TurningOnPurpose_IsNotAWobble()
        {
            WarpStabilityGuard g = Fresh();
            for (int s = 0; s < 10; s++) Assert.False(g.Check(s, 50, 0.3, "drive", 40, 6, true));   // the flip
        }

        [Fact]
        public void Stability_AtTenXOrBelow_NothingToGuard()
        {
            WarpStabilityGuard g = Fresh();
            Assert.False(g.Check(0, 10, 0.3, "drive", 40, 9, false));
            Assert.False(g.Check(1, 10, 0.5, "drive", 38, 9, false));
            Assert.False(g.Tripped);
        }

        [Fact]
        public void Stability_ResetForANewTrip()
        {
            WarpStabilityGuard g = Fresh();
            g.Check(0, 50, 0.3, "drive", 40, 0, false);
            g.Check(1, 50, 0.3, "drive", 30, 0, false);
            Assert.True(g.Tripped);
            g.Reset(30);
            Assert.False(g.Tripped);
            Assert.Null(g.Reason);
            Assert.False(g.Check(2, 50, 0.3, "drive", 30, 0, false));
        }
    }
}
