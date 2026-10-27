using System;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Picking a trip up again (ADR 0004): re-planned from where the ship is after time away, and refused, as at Start,
    /// when it can no longer be finished. The trip is the in-game proof's: Kerbin to Duna on the Bebop Refueller, coast in
    /// the middle at 1 g (the simulator's tested Bebop coast case; the in-game run flies 0.4 g, which fits the Bebop's
    /// budget only at the game's date).
    /// </summary>
    public class TripReplannerTests
    {
        private const double AccelG = 1.0;
        private const double ParkingAltitude = 60_000;
        private const double BurnSeconds = 1800;            // flown before the player leaves
        private const double AwaySeconds = 2 * 21_600;      // two Kerbin days coasting, engine off
        private static double A => AccelG * TripCalculator.OneG;
        private static double Budget => GuidanceSimulationTests.BebopBudget;
        /// <summary>The Bebop's delta-v at the start of the trip: the budget is 90 % of it, the rest the reserve.</summary>
        private static double StartDeltaV => GuidanceSimulationTests.BebopDeltaV;

        private readonly ITestOutputHelper output;

        public TripReplannerTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // The first half hour is the same for every test: fly it once.
        private static readonly Lazy<FlightResult> FirstBurn = new Lazy<FlightResult>(() => FlyFor(BurnSeconds));

        private static FlightResult FlyFor(double seconds) => Flights.Fly("burn, then away", KerbolSystem.Duna, AccelG, FlipManeuver.CutFlipRelight, 0,
            ParkingAltitude, stopAfterSeconds: seconds, trip: TripType.CoastInTheMiddle, deltaVBudget: Budget);

        private static ShipSimulator At(FlightResult f) => new ShipSimulator(KerbolSystem.Kerbin, KerbolSystem.Duna, 5 * Math.PI / 180)
        {
            UT = f.EndUT, Position = f.EndPosition, Velocity = f.EndVelocity, Facing = f.EndFacing,
        };

        /// <summary>The trip as started, flown for <see cref="BurnSeconds"/>, then left to coast for <paramref name="away"/>.</summary>
        private static (ShipSimulator ship, FlightResult start, double used) Interrupted(double away)
        {
            FlightResult first = FirstBurn.Value;
            var ship = new ShipSimulator(KerbolSystem.Kerbin, KerbolSystem.Duna, 5 * Math.PI / 180)
            {
                UT = first.EndUT, Position = first.EndPosition, Velocity = first.EndVelocity, Facing = first.EndFacing,
            };
            for (double left = away; left > 0; left -= Flights.MaxRailsStep)
                ship.Coast(Math.Min(Flights.MaxRailsStep, left), ship.Facing);
            return (ship, first, first.DeltaVUsed);
        }

        private static TripRequest From(ShipSimulator ship, double accelG = AccelG, PlanetRecord target = null)
        {
            target ??= KerbolSystem.Duna;
            PlanetRecord kerbin = KerbolSystem.Kerbin;
            TargetPlanet departure = null;
            if ((ship.Position - kerbin.Orbit.StateAt(ship.UT).Position).Length < kerbin.SphereOfInfluence)
            {
                departure = kerbin.AsTarget();
                departure.SphereOfInfluence = kerbin.SphereOfInfluence;
            }

            TargetPlanet t = target.AsTarget();
            t.SphereOfInfluence = target.SphereOfInfluence;
            return new TripRequest
            {
                ShipPosition = ship.Position, ShipVelocity = ship.Velocity, StartTime = ship.UT, Target = t, Departure = departure,
                Acceleration = accelG * TripCalculator.OneG, ParkingAltitude = ParkingAltitude, SunGravParameter = KerbolSystem.SunGravParameter,
            };
        }

        [Fact]
        public void AfterTimeAway_ReplannedFromWhereTheShipIs()
        {
            (ShipSimulator ship, FlightResult start, double used) = Interrupted(AwaySeconds);
            double plannedArrival = start.PlannedTripTime;   // the trip started at UT 0: the arrival it was flying to
            ReplanResult r = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, Budget - used, GuidanceSimulationTests.Bebop, 0.1, plannedArrival);
            double newArrival = ship.UT + r.RemainingTime;
            output.WriteLine($"burned {BurnSeconds} s ({used:F0} m/s), away {AwaySeconds} s; planned arrival {plannedArrival:F0} s, " +
                             $"re-planned arrival {newArrival:F0} s ({newArrival - plannedArrival:+0;-0} s), {r.RemainingTime:F0} s from now, " +
                             $"needs {r.DeltaV:F0} of {r.DeltaVAvailable:F0} m/s");

            Assert.True(r.Feasible, r.Problem.ToString());
            Assert.False(r.KeptArrival, "two days away: the old arrival needs far more than the budget left");
            Assert.Equal(ship.UT + r.Coast.TripTime, newArrival, 6);
            // The ship coasted slower than the plan would have flown it: it arrives later, but well within one more trip.
            Assert.True(newArrival > plannedArrival, "arrives later after time away");
            Assert.True(newArrival < plannedArrival + AwaySeconds + start.PlannedTripTime, "still a sensible trip");
            Assert.True(r.DeltaV <= r.DeltaVAvailable, "fits the delta-v left");

            // Longer away, later still.
            (ShipSimulator later, _, double used2) = Interrupted(4 * AwaySeconds);
            ReplanResult r2 = TripReplanner.Check(From(later), TripType.CoastInTheMiddle, Budget - used2, GuidanceSimulationTests.Bebop, 0.1);
            Assert.True(r2.Feasible);
            Assert.True(later.UT + r2.RemainingTime > newArrival);
        }

        /// <summary>
        /// Paused and resumed at once (a quicksave and quickload, the Space Center and back): the trip keeps the arrival it
        /// was flying to. A fresh fastest plan from part-way through lands up to about 1 % either side of it, which in the game
        /// showed as "away 5 s, arrival 32 minutes later".
        /// </summary>
        [Theory]
        [InlineData(65)]
        [InlineData(300)]
        [InlineData(1800)]
        public void ShortAbsence_KeepsTheArrivalBeingFlown(double burnSeconds)
        {
            FlightResult first = burnSeconds == BurnSeconds ? FirstBurn.Value : FlyFor(burnSeconds);
            ShipSimulator ship = At(first);
            double left = Budget - first.DeltaVUsed, flying = first.PlannedTripTime;
            CoastPlan fastest = CoastPlanner.Plan(From(ship), left);
            ReplanResult r = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, left, null, 0.1, flying, StartDeltaV);
            output.WriteLine($"after {burnSeconds} s: flying to {flying:F0}; a fresh fastest plan says {fastest.ArrivalUT:F0} ({fastest.ArrivalUT - flying:+0;-0} s); " +
                             $"kept {r.KeptArrival}, needs {r.DeltaV:F0} of {left:F0} m/s ({r.DeltaV / left * 100:F2} %); expected reserve at arrival " +
                             $"{r.ReserveAtArrival / 1000:F2} km/s ({r.ReserveShare * 100:F2} % of {StartDeltaV / 1000:F2} km/s)");

            Assert.True(r.Feasible);
            Assert.True(r.KeptArrival);
            Assert.Equal(flying, ship.UT + r.RemainingTime, 3);
            Assert.True(r.ReserveShare >= TripReplanner.KeepReserveFloor);
        }

        // The three ways the same trip can go, flown once each: (a) no pause, (b) paused and resumed at once, keeping its
        // arrival, (c) left for two days and re-planned.
        // (a) is the simulator's own Bebop coast case, flown once for all the tests that use it.
        private static readonly Lazy<FlightResult> NoPause = new Lazy<FlightResult>(() => GuidanceSimulationTests.Flown("Duna coast Bebop"));

        private static readonly Lazy<(ReplanResult plan, FlightResult rest)> Kept = new Lazy<(ReplanResult, FlightResult)>(() =>
        {
            FlightResult first = FirstBurn.Value;
            ShipSimulator ship = At(first);
            double left = Budget - first.DeltaVUsed;
            ReplanResult plan = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, left, null, 0.1, first.PlannedTripTime, StartDeltaV);
            FlightResult rest = Flights.Fly("resumed at once", KerbolSystem.Duna, AccelG, FlipManeuver.CutFlipRelight, 0, ParkingAltitude,
                trip: TripType.CoastInTheMiddle, deltaVBudget: left, configure: c => c.CoastArrivalUT = plan.Coast.ArrivalUT,
                start: s => { s.UT = ship.UT; s.Position = ship.Position; s.Velocity = ship.Velocity; s.Facing = ship.Facing; });
            return (plan, rest);
        });

        private static readonly Lazy<(ReplanResult plan, FlightResult rest)> Away = new Lazy<(ReplanResult, FlightResult)>(() =>
        {
            (ShipSimulator ship, _, double used) = Interrupted(AwaySeconds);
            double left = Budget - used;
            ReplanResult plan = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, left, null, 0.1, FirstBurn.Value.PlannedTripTime, StartDeltaV);
            FlightResult rest = Flights.Fly("resumed after two days", KerbolSystem.Duna, AccelG, FlipManeuver.CutFlipRelight, 0, ParkingAltitude,
                trip: TripType.CoastInTheMiddle, deltaVBudget: left, configure: c => c.CoastArrivalUT = plan.Coast.ArrivalUT,
                start: s => { s.UT = ship.UT; s.Position = ship.Position; s.Velocity = ship.Velocity; s.Facing = ship.Facing; });
            return (plan, rest);
        });

        /// <summary>
        /// The reserve the ship really arrives with, against the 10 % promised: (a) no pause, (b) the kept arrival after a short
        /// pause, (c) the re-plan after two days away. As km/s and as a share of the delta-v the trip started with.
        /// </summary>
        [Fact]
        [Trait("Category", "Simulation")]
        public void ReserveAtArrival_ThreeCases()
        {
            double s0 = StartDeltaV, burned = FirstBurn.Value.DeltaVUsed;
            FlightResult a = NoPause.Value;
            (ReplanResult kb, FlightResult b) = Kept.Value;
            (ReplanResult kc, FlightResult c) = Away.Value;
            double ra = s0 - a.DeltaVUsed, rb = s0 - burned - b.DeltaVUsed, rc = s0 - burned - c.DeltaVUsed;
            output.WriteLine($"started with {s0 / 1000:F2} km/s; 10 % reserve = {0.1 * s0 / 1000:F2} km/s");
            output.WriteLine($"(a) no pause:                 arrived with {ra / 1000:F2} km/s = {ra / s0 * 100:F1} %");
            output.WriteLine($"(b) short pause, kept arrival: arrived with {rb / 1000:F2} km/s = {rb / s0 * 100:F1} % (expected at the resume {kb.ReserveAtArrival / 1000:F2} km/s = {kb.ReserveShare * 100:F1} %)");
            output.WriteLine($"(c) two days away, re-planned: arrived with {rc / 1000:F2} km/s = {rc / s0 * 100:F1} % (expected at the resume {kc.ReserveAtArrival / 1000:F2} km/s = {kc.ReserveShare * 100:F1} %)");

            Assert.True(a.Finished && b.Finished && c.Finished);
            Assert.True(kb.KeptArrival && !kc.KeptArrival);
            Assert.True(ra >= 0.10 * s0, "no pause keeps the 10 %");
            Assert.True(rb >= TripReplanner.KeepReserveFloor * s0, "a kept arrival keeps at least 9 %");
            Assert.True(rc >= 0.10 * s0, "a re-plan keeps the 10 %");
            // The expectation at the resume is conservative: flying costs less than the burns plus the full guidance margin.
            Assert.True(rb >= kb.ReserveAtArrival && rc >= kc.ReserveAtArrival);
        }

        /// <summary>
        /// The 9 % floor: the same kept arrival, for a ship that started with a little less or a little more delta-v, falls
        /// just below the floor (re-planned, keeping the full 10 %) or just above it (kept).
        /// </summary>
        [Fact]
        public void KeptArrival_OnlyAboveTheReserveFloor()
        {
            FlightResult first = FirstBurn.Value;
            ShipSimulator ship = At(first);
            double left = Budget - first.DeltaVUsed;
            CoastPlan keep = CoastPlanner.PlanArrivingAt(From(ship), left, first.PlannedTripTime);
            double over = keep.DeltaVNeeded - left;   // how far keeping the arrival goes into the reserve
            Assert.True(over > 0);
            // Reserve if kept = 10 % of the start + budget left - needed = 0.1 s - over: choose s to put it at 8.9 % and 9.1 %.
            double below = over / (0.1 - 0.089), above = over / (0.1 - 0.091);
            ReplanResult r1 = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, left, null, 0.1, first.PlannedTripTime, below);
            ReplanResult r2 = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, left, null, 0.1, first.PlannedTripTime, above);
            output.WriteLine($"keeping the arrival goes {over:F0} m/s into the reserve; reserve if kept: {r1.ReserveIfKept / below * 100:F2} % -> kept {r1.KeptArrival}, " +
                             $"{r2.ReserveIfKept / above * 100:F2} % -> kept {r2.KeptArrival}; re-planned instead, the reserve is {r1.ReserveShare * 100:F2} %");

            Assert.Equal(0.089, r1.ReserveIfKept / below, 6);
            Assert.False(r1.KeptArrival);
            Assert.True(r1.Feasible);
            Assert.True(r1.ReserveShare >= 0.1 - 1e-9, "re-planned: the full 10 %");
            Assert.Equal(0.091, r2.ReserveIfKept / above, 6);
            Assert.True(r2.KeptArrival);
        }

        /// <summary>The kept arrival, flown from where the ship was paused, reaches the parking orbit on time and within the budget.</summary>
        [Fact]
        [Trait("Category", "Simulation")]
        public void ShortAbsence_TheTripArrivesWhenItWasGoingTo()
        {
            FlightResult first = FirstBurn.Value;
            double left = Budget - first.DeltaVUsed;
            (ReplanResult plan, FlightResult rest) = Kept.Value;
            Assert.True(plan.KeptArrival);
            output.WriteLine($"kept arrival {first.PlannedTripTime:F0}, arrived {rest.EndUT:F0}; periapsis {rest.FinalPeriapsisAltitude / 1000:F1} km; " +
                             $"used {rest.DeltaVUsed:F0} of {left:F0} m/s");

            Assert.True(rest.Finished);
            Assert.True(Math.Abs(rest.FinalPeriapsisAltitude - ParkingAltitude) <= 0.1 * ParkingAltitude);
            Assert.True(Math.Abs(rest.EndUT - first.PlannedTripTime) <= 0.01 * first.PlannedTripTime, "arrives when it was going to, within 1 %");
            Assert.True(rest.DeltaVUsed <= left, "within the budget left");
        }

        /// <summary>The re-planned trip, flown from where the ship was left, reaches the parking orbit when the re-plan said.</summary>
        [Fact]
        [Trait("Category", "Simulation")]
        public void AfterTimeAway_TheReplannedTripArrives()
        {
            (_, _, double used) = Interrupted(AwaySeconds);
            double left = Budget - used;
            (ReplanResult plan, FlightResult rest) = Away.Value;
            Assert.True(plan.Feasible);
            output.WriteLine($"re-planned {plan.RemainingTime:F0} s, flown {rest.TripTime:F0} s; periapsis {rest.FinalPeriapsisAltitude / 1000:F1} km, " +
                             $"e {rest.FinalEccentricity:F4}; used {rest.DeltaVUsed:F0} of {left:F0} m/s; {rest.WallSeconds:F1} s wall");

            Assert.True(rest.Finished, "the resumed trip never reported Done");
            Assert.True(rest.FinalEccentricity < 0.05);
            Assert.True(Math.Abs(rest.FinalPeriapsisAltitude - ParkingAltitude) <= 0.1 * ParkingAltitude);
            Assert.True(Math.Abs(rest.TripTime / plan.RemainingTime - 1) <= 0.01, "arrives when the re-plan said, within 1 %");
            Assert.True(rest.DeltaVUsed <= left, "within the budget left");
        }

        [Fact]
        public void NotEnoughDeltaVLeft_Refused()
        {
            (ShipSimulator ship, _, _) = Interrupted(AwaySeconds);
            ReplanResult r = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, 1_000, GuidanceSimulationTests.Bebop, 0.1);
            Assert.False(r.Feasible);
            Assert.Equal(ReplanProblem.NotEnoughDeltaV, r.Problem);
            Assert.Equal(1_000, r.DeltaVAvailable);
        }

        [Fact]
        public void ShipHasLessThanTheBudgetLeft_TheShipLimits()
        {
            // The budget left says plenty, but the ship itself now has only a little (fuel lost or used elsewhere).
            (ShipSimulator ship, _, _) = Interrupted(AwaySeconds);
            var almostDry = new ShipPerformance(4_746_093.26171875, 281_249.96875, 0.5, 142_000, 141_422.38260853774);
            ReplanResult r = TripReplanner.Check(From(ship), TripType.CoastInTheMiddle, Budget, almostDry, 0.1);
            Assert.Equal(ReplanProblem.NotEnoughDeltaV, r.Problem);
            Assert.True(r.DeltaVAvailable < 0.9 * almostDry.DeltaVAtAcceleration(A) + 1);
        }

        [Fact]
        public void FullBurn_NotEnoughDeltaV_RefusedUnlessFuelIsntCounted()
        {
            (ShipSimulator ship, _, _) = Interrupted(AwaySeconds);
            var small = new ShipPerformance(4_746_093.26171875, 281_249.96875, 0.5, 145_000, 141_422.38260853774);
            ReplanResult r = TripReplanner.Check(From(ship), TripType.FullBurn, 0, small, 0.1);
            Assert.Equal(ReplanProblem.NotEnoughDeltaV, r.Problem);
            Assert.True(r.DeltaV > r.DeltaVAvailable);

            // Infinite propellant: no ship check, as in the planner.
            Assert.True(TripReplanner.Check(From(ship), TripType.FullBurn, 0, null, 0.1).Feasible);
        }

        [Fact]
        public void TooWeakForTheTargetsGravity_Refused()
        {
            // Duna's pull at a 60 km parking orbit is about 0.21 g: 0.1 g can't stop there.
            (ShipSimulator ship, _, _) = Interrupted(AwaySeconds);
            ReplanResult r = TripReplanner.Check(From(ship, accelG: 0.1), TripType.FullBurn, 0, null, 0.1);
            Assert.Equal(ReplanProblem.TargetGravityTooStrong, r.Problem);
            Assert.True(r.MinimumAcceleration > 0.1 * TripCalculator.OneG);
        }

        [Fact]
        public void EnginesCantReachTheAcceleration_Refused()
        {
            (ShipSimulator ship, _, _) = Interrupted(AwaySeconds);
            ReplanResult r = TripReplanner.Check(From(ship, accelG: 5), TripType.FullBurn, 0, GuidanceSimulationTests.Bebop, 0.1);
            Assert.Equal(ReplanProblem.CantHoldAcceleration, r.Problem);
        }
    }
}
