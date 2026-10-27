using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    public class WarpRatesSnapshotTests
    {
        [Fact]
        public void Restore_HandsBackThePlayersOwnArrayWithItsValues()
        {
            float[] player = { 1, 2, 3, 4 };
            WarpRatesSnapshot saved = WarpRatesSnapshot.Take(player);
            float[] trip = { 1, 4, 10, 25 };   // what the game uses during the trip

            float[] back = saved.Restore();

            Assert.Same(player, back);
            Assert.Equal(new float[] { 1, 2, 3, 4 }, back);
            Assert.False(saved.Matches(trip));
            Assert.True(saved.Matches(back));
        }

        [Fact]
        public void Restore_UndoesAWriteIntoThePlayersArray()
        {
            // Found in cycle 9's log: a write of 25 into the player's array (BetterTimeWarp's preset) outlived the trip.
            float[] player = { 1, 2, 3, 4 };
            WarpRatesSnapshot saved = WarpRatesSnapshot.Take(player);
            player[3] = 25;

            Assert.False(saved.Matches(player));
            Assert.Equal(new float[] { 1, 2, 3, 4 }, saved.Restore());
            Assert.True(saved.Matches(player));
        }

        [Fact]
        public void Matches_NeedsTheSameArrayNotJustEqualValues()
        {
            float[] player = { 1, 2, 3, 4 };
            WarpRatesSnapshot saved = WarpRatesSnapshot.Take(player);
            Assert.False(saved.Matches(new float[] { 1, 2, 3, 4 }));
        }

        [Fact]
        public void SameValues_InANewArray_AfterAQuickload()
        {
            float[] player = { 1, 2, 3, 4 };
            WarpRatesSnapshot saved = WarpRatesSnapshot.Take(player);
            Assert.True(saved.SameValues(new float[] { 1, 2, 3, 4 }));
            Assert.False(saved.SameValues(new float[] { 1, 4, 10, 25 }));
            Assert.False(saved.SameValues(new float[] { 1, 2, 3 }));
            Assert.False(saved.SameValues(null));
        }

        [Fact]
        public void Take_NoRates_Null()
        {
            Assert.Null(WarpRatesSnapshot.Take(null));
        }
    }
}
