using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The live reserve check (ADR 0002, 2026-10-27 addendum).</summary>
    public class ReserveWatchTests
    {
        // Trip B at Start: 73,397 m/s on board, the plan needs 66,058.
        private const double Start = 73_397, Needed = 66_058;

        [Fact]
        public void AtStart_ThePlansReserve()
        {
            Assert.Equal(Start - Needed, ReserveWatch.ExpectedAtArrival(Start, Needed, 0), 6);
        }

        [Fact]
        public void FlyingThePlan_LeavesTheReserveWhereItWas()
        {
            // 35,983 m/s burned speeding up: the ship has that much less, and the trip needs that much less.
            Assert.Equal(Start - Needed, ReserveWatch.ExpectedAtArrival(Start - 35_983, Needed, 35_983), 6);
        }

        [Fact]
        public void PropellantLostWithoutThrust_ShowsAtOnce()
        {
            // Mid-coast, 900 m/s worth of propellant gone with nothing burned.
            Assert.Equal(Start - Needed - 900, ReserveWatch.ExpectedAtArrival(Start - 35_983 - 900, Needed, 35_983), 6);
        }

        [Fact]
        public void MoreBurnedThanPlanned_NothingLeftToNeed()
        {
            Assert.Equal(500, ReserveWatch.ExpectedAtArrival(500, Needed, Needed + 1000), 6);
        }

        [Fact]
        public void LossAllowance_ComesOffAsItIsUsed()
        {
            Assert.Equal(1, ReserveWatch.AllowanceShareLeft(431_212, 431_212), 6);
            Assert.Equal(0.25, ReserveWatch.AllowanceShareLeft(107_803, 431_212), 6);
            Assert.Equal(0, ReserveWatch.AllowanceShareLeft(0, 431_212), 6);
            Assert.Equal(Start - Needed - 600, ReserveWatch.ExpectedAtArrival(Start, Needed, 0, 600), 6);
        }
    }
}
