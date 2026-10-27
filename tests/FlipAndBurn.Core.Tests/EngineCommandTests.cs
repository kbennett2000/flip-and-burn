using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// The engine decision each physics step. In particular the engine's own throttle is zero whenever the ship points
    /// more than the allowed angle away from the burn direction, including right after a relight (found in the game: the
    /// Bebop swung 20 degrees off just after relighting and its drive kept burning).
    /// </summary>
    public class EngineCommandTests
    {
        private const double Tolerance = 5, OneG = 9.81, FullThrust = 31.09;   // the Bebop: 3.17 g at full thrust

        [Theory]
        [InlineData(5.01)]
        [InlineData(10)]
        [InlineData(20)]      // the swing after the relight in the game
        [InlineData(90)]
        [InlineData(179)]
        public void PointedAway_ThrottleIsZeroButEnginesStayLit(double errorDeg)
        {
            EngineCommand c = EngineCommand.Decide(OneG, flipping: false, errorDeg, Tolerance, FullThrust);
            Assert.Equal(EngineAction.HoldAtZero, c.Action);
            Assert.Equal(0f, c.Throttle);
        }

        [Fact]
        public void RelightSequence_NoThrustUntilPointed()
        {
            // Flip (cut) -> braking wanted while still 160, 60, 20, 6 degrees off -> pointed at 4.9 degrees.
            Assert.Equal(EngineAction.Cut, EngineCommand.Decide(0, flipping: true, 170, Tolerance, FullThrust).Action);
            foreach (double off in new[] { 160.0, 60, 20, 6 })
            {
                EngineCommand c = EngineCommand.Decide(0.99 * OneG, flipping: false, off, Tolerance, FullThrust);
                Assert.Equal(0f, c.Throttle);
                Assert.NotEqual(EngineAction.Burn, c.Action);
            }

            EngineCommand burn = EngineCommand.Decide(0.99 * OneG, flipping: false, 4.9, Tolerance, FullThrust);
            Assert.Equal(EngineAction.Burn, burn.Action);
            Assert.Equal((float)(0.99 * OneG / FullThrust), burn.Throttle, 6);
        }

        [Fact]
        public void Pointed_ThrottleGivesTheCommandedAcceleration()
        {
            EngineCommand c = EngineCommand.Decide(OneG, flipping: false, 0.03, Tolerance, FullThrust);
            Assert.Equal(EngineAction.Burn, c.Action);
            Assert.Equal((float)(OneG / FullThrust), c.Throttle, 6);   // 0.316, as in the game
        }

        [Fact]
        public void NoThrustWanted_Cut()
        {
            Assert.Equal(EngineAction.Cut, EngineCommand.Decide(0, flipping: false, 0, Tolerance, FullThrust).Action);   // coast
            Assert.Equal(EngineAction.Cut, EngineCommand.Decide(0, flipping: true, 90, Tolerance, FullThrust).Action);   // cut, flip, relight
        }

        [Fact]
        public void SkewFlip_BurnsWhileTurning()
        {
            EngineCommand c = EngineCommand.Decide(OneG, flipping: true, 120, Tolerance, FullThrust);
            Assert.Equal(EngineAction.Burn, c.Action);
            Assert.True(c.Throttle > 0);
        }

        [Fact]
        public void MoreThanTheEnginesCanGive_FullThrottle()
        {
            Assert.Equal(1f, EngineCommand.Decide(5 * OneG, false, 0, Tolerance, FullThrust).Throttle);
        }
    }
}
