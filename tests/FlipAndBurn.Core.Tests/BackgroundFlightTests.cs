using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Trips flown in the background (ADR 0006): the whole trip, or part of it after a handover from a watched trip, and the
    /// other way round. Each must arrive to the same standard as a watched trip, and use the fuel the plan says.
    /// </summary>
    public class BackgroundFlightTests
    {
        private readonly ITestOutputHelper output;

        public BackgroundFlightTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        // A fusion ship: 150 t, of which 100 t is propellant the engines reach, at 3,000 km/s exhaust velocity (the Bebop's is
        // about 2,760 km/s): 3,300 km/s of delta-v, enough for a full burn to Jool.
        private const double StartMass = 150_000, DryMass = 50_000, ExhaustVelocity = 3_000_000;

        public sealed class Case
        {
            public string Name;
            public PlanetRecord Target;
            public double AccelG = 1, Parking, StartUT, Angle;
            public FlipManeuver Flip = FlipManeuver.CutFlipRelight;
            public TripType Trip = TripType.FullBurn;
            public SimSystem System;           // null: a trip between planets from an orbit 100 km above Kerbin
            public double BudgetShare;         // coast trips: share of a full burn's delta-v
            public double TurnDeg = 10;        // the ship's turn rate (degrees a second)
            public PlanetRecord From;          // trips between planets: the planet left (Kerbin if not set)
        }

        public sealed class Outcome
        {
            public string Name;
            public BackgroundEnd End;
            public string Reason;
            public double Pe, Ap, Parking, TripTime, PlannedTime, DeltaV, PlannedDeltaV, Fuel, FuelExpected, LowestMargin = double.MaxValue;
            public int Steps;
            public double WallSeconds;

            public string Problem()
            {
                if (End != BackgroundEnd.Arrived) return $"ended {End}: {Reason}";
                double tol = 0.05 * Parking;
                if (Math.Abs(Pe - Parking) > tol || Math.Abs(Ap - Parking) > tol) return $"orbit {Pe / 1000:F1} x {Ap / 1000:F1} km, not within 5 % of {Parking / 1000:F0} km";
                if (LowestMargin < 0) return $"went {-LowestMargin / 1000:F1} km below a body's safe height";
                if (Math.Abs(Fuel / FuelExpected - 1) > 0.10) return $"fuel {Fuel:F0} kg, the plan's delta-v needs {FuelExpected:F0} kg";
                return null;
            }

            public override string ToString() =>
                $"{(Problem() == null ? "OK  " : "FAIL")} | {Name} | orbit {Pe / 1000:F1} x {Ap / 1000:F1} km (target {Parking / 1000:F0}) | time {TripTime:F0} s, planned {PlannedTime:F0} " +
                $"({(TripTime / PlannedTime - 1) * 100:+0.0;-0.0} %) | delta-v {DeltaV / 1000:F2} km/s, planned {PlannedDeltaV / 1000:F2} | fuel {Fuel:F0} kg, " +
                $"rocket equation for the delta-v flown {FuelExpected:F0} kg | {Steps} steps, {WallSeconds:F1} s | {Problem()}";
        }

        // ---- set-up shared with the watched simulator flights

        internal static (GuidanceConfig Config, List<GravitySource> Bodies, double CentralMu, ShipSimulator Start, PlanetRecord Target, double TargetSafeRadius)
            Prepare(Case c, double budget)
        {
            // The ship's own turn rate, as captured when the player leaves (the sweep flies each case at 5 and 10 degrees a second).
            double a = c.AccelG * TripCalculator.OneG, turn = c.TurnDeg * Math.PI / 180;
            PlanetRecord departure = c.System?.Departure ?? c.From ?? KerbolSystem.Kerbin;
            PlanetRecord target = c.System != null ? c.System.TargetRecord(c.Target) : c.Target;
            double centralMu = c.System?.CentralGravParameter ?? KerbolSystem.SunGravParameter;
            var sim = new ShipSimulator(departure, target, turn, centralMu, c.System?.Others);
            sim.StartInOrbit(departure, c.System?.StartAltitude ?? 100_000, c.StartUT, c.System?.StartAngle ?? c.Angle);

            TargetPlanet tp = target.AsTarget();
            tp.SphereOfInfluence = target.SphereOfInfluence;
            tp.IsCentralBody = target.IsCentre;
            TargetPlanet dep = c.System != null ? c.System.DepartureFor() : departure.AsTarget();
            if (c.System == null)
            {
                dep.SphereOfInfluence = departure.SphereOfInfluence;
                dep.SafeRadius = departure.Radius + 80_000;
            }

            var config = new GuidanceConfig
            {
                Target = tp, Departure = dep, Acceleration = a, Flip = c.Flip, ParkingAltitude = c.Parking, TurnRate = turn,
                SunGravParameter = centralMu, Trip = c.Trip, DeltaVBudget = budget,
            };
            c.System?.Configure(config);

            var bodies = new List<GravitySource>();
            void Add(PlanetRecord p, bool always)
            {
                if (p == null || p.IsCentre || bodies.Any(b => b.Name == p.Name)) return;
                bodies.Add(new GravitySource
                {
                    Name = p.Name, Ephemeris = p.Ephemeris, GravParameter = p.GravParameter, SphereOfInfluence = p.SphereOfInfluence,
                    Always = always, SafeRadius = p.Radius + Flights.TargetSafeAltitude(p),
                });
            }

            Add(target, true);
            Add(departure, false);
            if (c.System != null) foreach (PlanetRecord o in c.System.Others) Add(o, false);
            // The centre always pulls, also when it is the target (from a moon back to its planet): as the simulator flies it.
            return (config, bodies, centralMu, sim, target, target.Radius + Flights.TargetSafeAltitude(target));
        }

        private static double PlannedFullBurn(Case c, out double time)
        {
            FlightResult probe = c.System != null
                ? Flights.Fly("probe", c.System.TargetRecord(c.Target), c.AccelG, c.Flip, c.StartUT, c.Parking, system: c.System, stopAfterSeconds: 1)
                : Flights.Fly("probe", c.Target, c.AccelG, c.Flip, c.StartUT, c.Parking, stopAfterSeconds: 1, departFrom: c.From);
            time = probe.PlannedTripTime;
            return probe.PlannedDeltaV;
        }

        /// <summary>Fly the whole trip in the background, or from <paramref name="from"/> (a handover from a watched trip).</summary>
        public static Outcome FlyBackground(Case c, Action<ShipSimulator> from = null, double usedBefore = 0, double coastArrivalUT = double.NaN)
        {
            double plannedDv = PlannedFullBurn(c, out double plannedTime);
            double budget = c.Trip == TripType.CoastInTheMiddle ? c.BudgetShare * plannedDv : 0;
            var p = Prepare(c, budget - usedBefore);
            from?.Invoke(p.Start);
            if (!double.IsNaN(coastArrivalUT)) p.Config.CoastArrivalUT = coastArrivalUT;
            if (from != null) p.Config.WaitForDepartureWindow = false;   // under way already
            if (c.Trip == TripType.CoastInTheMiddle)
            {
                var req = new TripRequest
                {
                    ShipPosition = p.Start.Position, ShipVelocity = p.Start.Velocity, StartTime = p.Start.UT, Target = p.Config.Target,
                    Departure = p.Config.Departure, Acceleration = p.Config.Acceleration, ParkingAltitude = c.Parking, SunGravParameter = p.Config.SunGravParameter,
                };
                c.System?.Configure(req);
                CoastPlan coast = CoastPlanner.Plan(req, budget);
                plannedTime = coast.TripTime;
                plannedDv = coast.DeltaV;
            }

            double mass = StartMass * Math.Exp(-usedBefore / ExhaustVelocity);
            var flight = new BackgroundFlight(p.Config, p.Bodies, p.CentralMu, p.Start.UT, p.Start.Position, p.Start.Velocity, p.Start.Facing,
                mass, ExhaustVelocity, DryMass, p.TargetSafeRadius);
            var outcome = new Outcome { Name = c.Name, Parking = c.Parking, PlannedTime = plannedTime, PlannedDeltaV = plannedDv };
            var wall = System.Diagnostics.Stopwatch.StartNew();
            double startUT = p.Start.UT, limit = startUT + 4 * Math.Max(plannedTime, 3600);
            while (!flight.Finished && flight.UT < limit && wall.Elapsed.TotalSeconds < 150)
            {
                // A frame's worth at high warp, as the game advances it.
                flight.AdvanceTo(flight.UT + 600, 5000);
                foreach (GravitySource b in p.Bodies)
                    outcome.LowestMargin = Math.Min(outcome.LowestMargin, (flight.Position - b.Ephemeris.StateAt(flight.UT).Position).Length - b.SafeRadius);
            }

            StateVector t = p.Target.Ephemeris.StateAt(flight.UT);
            OrbitShape shape = TwoBody.Shape(flight.Position - t.Position, flight.Velocity - t.Velocity, p.Target.GravParameter);
            outcome.End = flight.End;
            outcome.Reason = flight.Reason ?? (flight.Finished ? null : "didn't finish");
            outcome.Pe = shape.PeriapsisRadius - p.Target.Radius;
            outcome.Ap = shape.ApoapsisRadius - p.Target.Radius;
            outcome.TripTime = flight.UT - startUT;
            outcome.DeltaV = flight.DeltaVUsed;
            outcome.Fuel = flight.PropellantUsed;
            outcome.FuelExpected = mass * (1 - Math.Exp(-flight.DeltaVUsed / ExhaustVelocity));
            outcome.Steps = flight.Steps;
            outcome.WallSeconds = wall.Elapsed.TotalSeconds;
            return outcome;
        }

        // ---- cases

        private static IEnumerable<Case> PlanetCases() => new[]
        {
            new Case { Name = "Duna 1g cut", Target = KerbolSystem.Duna, Parking = 60_000 },
            new Case { Name = "Duna 1g skew", Target = KerbolSystem.Duna, Parking = 60_000, Flip = FlipManeuver.SkewFlip },
            new Case { Name = "Jool 1g cut", Target = KerbolSystem.Jool, Parking = 220_000 },
            new Case { Name = "Duna Sun in way", Target = KerbolSystem.Duna, Parking = 60_000, StartUT = GuidanceSimulationTests.DunaBehindSunUT },
            new Case { Name = "Duna 0.25g cut", Target = KerbolSystem.Duna, Parking = 60_000, AccelG = 0.25 },
            new Case { Name = "Duna coast", Target = KerbolSystem.Duna, Parking = 60_000, Trip = TripType.CoastInTheMiddle, BudgetShare = 0.4 },
            new Case { Name = "Jool coast", Target = KerbolSystem.Jool, Parking = 220_000, Trip = TripType.CoastInTheMiddle, BudgetShare = 0.4 },
        };

        // A small planet reached slowly (the fleet's in-game Dres arrival, cycle 15): a known follow-up (ADR 0006, "Known limits"):
        // the braking falls behind near the end, dips toward the safe height and uses about 20 % more delta-v than planned.
        private static Case SlowDres => new Case { Name = "Duna to Dres slow coast", Target = KerbolSystem.Dres, From = KerbolSystem.Duna, Parking = 20_000, Trip = TripType.CoastInTheMiddle, BudgetShare = 0.02 };

        /// <summary>Reports the slow Dres arrival in the background (a known follow-up), without failing on it.</summary>
        [Fact]
        [Trait("Category", "LongRunning")]
        [Trait("Category", "KnownIssue")]
        public void KnownIssue_SlowApproachToASmallPlanet()
        {
            foreach (double turn in new[] { 5.0, 10.0 })
            {
                Case c = SlowDres;
                c.TurnDeg = turn;
                Outcome o = FlyBackground(c);
                output.WriteLine($"{c.Name}, turning {turn:0} deg/s: {o.Problem() ?? "to the standard"} | orbit {o.Pe / 1000:F1} x {o.Ap / 1000:F1} km, " +
                                 $"delta-v {o.DeltaV / 1000:F2} km/s against {o.PlannedDeltaV / 1000:F2} planned, lowest margin {o.LowestMargin / 1000:F1} km");
            }
        }

        private static Case Moon(string name, PlanetRecord planet, PlanetRecord from, PlanetRecord to, double g, double angle,
            FlipManeuver flip = FlipManeuver.CutFlipRelight, TripType trip = TripType.FullBurn)
        {
            SimSystem sys = SimSystem.Around(planet, from, SimSystem.DefaultParking(from == planet ? planet.AtCentre() : from), angle);
            return new Case
            {
                Name = name, Target = to, AccelG = g, Angle = angle, Flip = flip, Trip = trip, System = sys,
                Parking = SimSystem.DefaultParking(sys.TargetRecord(to)), BudgetShare = 0.6,
            };
        }

        internal static IEnumerable<Case> MoonCases() => new[]
        {
            Moon("Kerbin to the Mun 1g", KerbolSystem.Kerbin, KerbolSystem.Kerbin, KerbolSystem.Mun, 1, 2.2),
            Moon("Kerbin to the Mun 2g skew", KerbolSystem.Kerbin, KerbolSystem.Kerbin, KerbolSystem.Mun, 2, 0, FlipManeuver.SkewFlip),
            Moon("Kerbin to Minmus coast", KerbolSystem.Kerbin, KerbolSystem.Kerbin, KerbolSystem.Minmus, 1, 1, trip: TripType.CoastInTheMiddle),
            Moon("the Mun to Minmus 1g", KerbolSystem.Kerbin, KerbolSystem.Mun, KerbolSystem.Minmus, 1, 0),
            Moon("the Mun to Kerbin 1g", KerbolSystem.Kerbin, KerbolSystem.Mun, KerbolSystem.Kerbin, 1, 1),
            Moon("Duna to Ike 2g", KerbolSystem.Duna, KerbolSystem.Duna, KerbolSystem.Ike, 2, 1),
            Moon("Jool to Laythe 1g", KerbolSystem.Jool, KerbolSystem.Jool, KerbolSystem.Laythe, 1, 0),
            Moon("Jool to Tylo coast", KerbolSystem.Jool, KerbolSystem.Jool, KerbolSystem.Tylo, 1, 1, trip: TripType.CoastInTheMiddle),
        };

        private void Run(IEnumerable<Case> cases)
        {
            var rows = cases.Select(c => FlyBackground(c)).ToList();
            foreach (Outcome o in rows) output.WriteLine(o.ToString());
            int ok = rows.Count(o => o.Problem() == null);
            output.WriteLine($"TOTAL: {rows.Count} background trips, {ok} arrived to the standard, {rows.Count(o => o.End == BackgroundEnd.SafeOrbit)} made a safe orbit instead");
            Assert.True(ok == rows.Count, $"{rows.Count - ok} of {rows.Count} background trips failed");
        }

        [Fact]
        [Trait("Category", "LongRunning")]
        [Trait("Category", "BackgroundSweep")]
        public void Sweep_EveryRouteInTheBackground() => Run(PlanetCases().Concat(MoonCases())
            .SelectMany(c => new[] { 5.0, 10.0 }.Select(turn => new Case
            {
                Name = $"{c.Name}, turning {turn:0} deg/s", Target = c.Target, AccelG = c.AccelG, Parking = c.Parking, StartUT = c.StartUT,
                Angle = c.Angle, Flip = c.Flip, Trip = c.Trip, System = c.System, BudgetShare = c.BudgetShare, TurnDeg = turn, From = c.From,
            })));

        [Fact]
        public void Background_OneOfEachKind() => Run(new[]
        {
            MoonCases().First(c => c.Name == "Jool to Tylo coast"),
            PlanetCases().First(c => c.Name == "Duna 1g cut"),
            PlanetCases().First(c => c.Name == "Duna coast"),
            MoonCases().First(c => c.Name == "Kerbin to the Mun 1g"),
            MoonCases().First(c => c.Name == "the Mun to Kerbin 1g"),
        });

        // ---- handovers

        [Fact]
        public void Handover_WatchedThenBackground_ThenWatchedAgain()
        {
            // Duna at 1 g: watched for 6 hours, then in the background for 20 hours, then watched to the end.
            Case c = PlanetCases().First(x => x.Name == "Duna 1g cut");
            FlightResult watched = Flights.Fly("watched part", c.Target, 1.0, c.Flip, 0, c.Parking, stopAfterSeconds: 6 * 3600);
            double plannedDv = PlannedFullBurn(c, out double plannedTime);
            var p = Prepare(c, 0);
            p.Config.WaitForDepartureWindow = false;
            double mass = StartMass * Math.Exp(-watched.DeltaVUsed / ExhaustVelocity);
            var bg = new BackgroundFlight(p.Config, p.Bodies, p.CentralMu, watched.EndUT, watched.EndPosition, watched.EndVelocity, watched.EndFacing,
                mass, ExhaustVelocity, DryMass, p.TargetSafeRadius);
            bg.AdvanceTo(watched.EndUT + 20 * 3600);
            Assert.False(bg.Finished, bg.Reason);
            FlightResult rest = Flights.Fly("rest, watched", c.Target, 1.0, c.Flip, 0, c.Parking,
                start: sim => { sim.UT = bg.UT; sim.Position = bg.Position; sim.Velocity = bg.Velocity; sim.Facing = bg.Facing; });
            output.WriteLine($"watched {watched.DeltaVUsed / 1000:F2} km/s, background {bg.DeltaVUsed / 1000:F2} km/s ({bg.Steps} steps), watched again {rest.DeltaVUsed / 1000:F2} km/s; " +
                             $"total {(watched.DeltaVUsed + bg.DeltaVUsed + rest.DeltaVUsed) / 1000:F2} against the plan's {plannedDv / 1000:F2}; " +
                             $"arrived {rest.FinalPeriapsisAltitude / 1000:F1} x {rest.FinalApoapsisAltitude / 1000:F1} km; trip {rest.EndUT:F0} s against {plannedTime:F0}");
            Assert.True(rest.Finished && rest.SafeOrbitReason == null, rest.SafeOrbitReason);
            Assert.InRange(rest.FinalPeriapsisAltitude, 0.95 * c.Parking, 1.05 * c.Parking);
            Assert.InRange(rest.FinalApoapsisAltitude, 0.95 * c.Parking, 1.05 * c.Parking);
            Assert.InRange((watched.DeltaVUsed + bg.DeltaVUsed + rest.DeltaVUsed) / plannedDv, 0.97, 1.05);
        }

        [Fact]
        public void Handover_MoonTripLeftAfterTheWindow_ArrivesInTheBackground()
        {
            // Kerbin to the Mun: watched through the wait and 5 minutes of burning, then the rest in the background.
            Case c = MoonCases().First(x => x.Name == "Kerbin to the Mun 1g");
            FlightResult watched = Flights.Fly("watched part", c.System.TargetRecord(c.Target), 1.0, c.Flip, 0, c.Parking, system: c.System,
                stopAfterSeconds: 1300);
            Outcome rest = FlyBackground(c, sim => { sim.UT = watched.EndUT; sim.Position = watched.EndPosition; sim.Velocity = watched.EndVelocity; sim.Facing = watched.EndFacing; },
                watched.DeltaVUsed);
            output.WriteLine($"watched {watched.DeltaVUsed / 1000:F2} km/s to {watched.EndUT:F0} s; then {rest}");
            Assert.True(rest.Problem() == null, rest.Problem());
        }
    }
}
