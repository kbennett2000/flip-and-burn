using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>Numbers as the player reads them.</summary>
    public class ReadoutTests
    {
        [Theory]
        [InlineData(850, "850 m/s")]
        [InlineData(999.6, "1.00 km/s")]       // rounds up into the next unit, not "1,000 m/s"
        [InlineData(7340, "7.34 km/s")]
        [InlineData(9995, "10.0 km/s")]
        [InlineData(73_397, "73.4 km/s")]
        [InlineData(116_051, "116 km/s")]
        [InlineData(692_603, "693 km/s")]
        public void Speed(double v, string shown) => Assert.Equal(shown, Readout.Speed(v));

        [Theory]
        // needed, available -> as written, and the shortfall is the difference of the two written numbers
        [InlineData(692_400, 116_600, "692 km/s", "117 km/s", "575 km/s")]   // separately rounded, 575.8 would read 576
        [InlineData(1_250, 800, "1.25 km/s", "0.80 km/s", "0.45 km/s")]      // one unit for all three
        [InlineData(73_397, 9_201, "73.4 km/s", "9.2 km/s", "64.2 km/s")]
        [InlineData(900, 899.6, "900 m/s", "900 m/s", "0 m/s")]
        public void Shortfall_AddsUpAsWritten(double needed, double available, string n, string a, string s)
        {
            (string N, string A, string S) r = Readout.Shortfall(needed, available);
            Assert.Equal((n, a, s), (r.N, r.A, r.S));
        }
    }
}
