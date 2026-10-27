using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// The reserve the window shows during a trip is the realistic expected figure, and agrees with what the ship arrives
    /// with to within a point (ADR 0002, cycle 12 addendum).
    /// </summary>
    public class ReserveShownTests
    {
        private readonly ITestOutputHelper output;
        public ReserveShownTests(ITestOutputHelper output) { this.output = output; }


        // The shown figure along the trip, worked out the way the window does it: from the plan while the ship climbs out of
        // Kerbin's pull, then from the burning the guidance has left. Sampled every game hour (every 2 minutes while
        // climbing out) in the simulator's own trips, and compared with what the ship arrives with.
        [Theory]
        [InlineData("Duna coast Bebop", true)]
        [InlineData("Jool coast Bebop", true)]
        [InlineData("Duna 1g cut", false)]
        public void ShownReserve_AlongTheTrip(string name, bool coast)
        {
            FlightResult r = GuidanceSimulationTests.Flown(name);
            double a = TripCalculator.OneG;
            double planned = coast ? r.PlannedDeltaVNeeded : r.PlannedDeltaV;
            // A coast trip: the Bebop. A full burn: a ship with just enough for it and 10 % over (no real ship has that).
            double start = coast ? GuidanceSimulationTests.BebopDeltaV : planned / 0.9;
            double arrived = start - r.DeltaVUsed;
            double worstClimb = 0, worst = 0;
            int after = 0;
            foreach ((double ut, bool climbing, double used, double burnLeft, GuidancePhase phase) in r.ReserveSamples)
            {
                double shown = climbing || double.IsNaN(burnLeft)
                    ? ReserveWatch.ExpectedAtArrival(start - used, ReserveWatch.ExpectedUse(planned, coast), used)
                    : start - used - ReserveWatch.RemainingUse(a, burnLeft, coast);
                double gap = Math.Abs(shown - arrived) / start * 100;
                if (climbing) worstClimb = Math.Max(worstClimb, gap);
                else { worst = Math.Max(worst, gap); after++; }
            }

            output.WriteLine($"{name}: arrived with {arrived / start * 100:F2} %; while climbing out the window shows " +
                             $"{(start - ReserveWatch.ExpectedUse(planned, coast)) / start * 100:F2} % (gap {worstClimb:F2} points); after that " +
                             $"{after} samples, largest gap {worst:F2} points");
            Assert.True(r.Finished);
            Assert.True(after > 10, "too few samples");
            Assert.InRange(worst, 0, 1.0);
            Assert.InRange(worstClimb, 0, 1.5);
        }

        [Fact]
        public void ShownReserve_InTheGame()
        {
            // Trip B (0.4 g, cycle 11): 73,397 m/s at Start, 66,058 planned with the margin, arrived with 9,201 m/s left.
            double shownB = ReserveWatch.ExpectedAtArrival(73_397, ReserveWatch.ExpectedUse(66_058, true), 0);
            // The guide's trip (1 g, cycle 12): 116,051 m/s at Start, 104,446 planned, arrived with 15,200 m/s (13.1 %).
            double shownGuide = ReserveWatch.ExpectedAtArrival(116_051, ReserveWatch.ExpectedUse(104_446, true), 0);
            output.WriteLine($"trip B: shown {shownB / 73_397 * 100:F2} %, arrived 12.54 %; the guide's trip: shown {shownGuide / 116_051 * 100:F2} %, arrived 13.10 %");
            Assert.InRange(shownB / 73_397 * 100 - 12.54, -1.0, 1.0);
            Assert.InRange(shownGuide / 116_051 * 100 - 13.10, -1.0, 1.0);
        }
    }
}
