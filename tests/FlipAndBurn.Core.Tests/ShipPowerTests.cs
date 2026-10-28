using System.Collections.Generic;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>Engines that burn only electric charge, and the ship's power (ADR 0002, cycle 13 addendum).</summary>
    public class ShipPowerTests
    {
        // Kris's Jibar Side Thruster GIANT: 3000 kN, Isp 320 in vacuum, ElectricCharge ratio 200000.
        private static EngineReading Giant() => new EngineReading
        {
            Name = "Jibar Side Thruster GIANT", Thrust = 3_000_000, VacuumIsp = 320,
            Propellants = new List<PropellantUse> { new PropellantUse(ShipEngines.ElectricCharge, 200_000, 0) },
        };

        private const double PerKn = 200_000 / (320 * 9.80665);   // 63.73 charge per second per kN

        [Fact]
        public void Giant_ChargeAtFullThrust_AsKspDrawsIt()
        {
            ShipEngineReport r = ShipEngines.Combine(new[] { Giant() }, new Dictionary<string, double>(), 20_000,
                power: new ShipPower { Stored = 1e6, Capacity = 1e6 });
            Assert.True(r.ElectricOnly);
            Assert.False(r.NoThrust);
            Assert.NotNull(r.Performance.Electric);
            Assert.Equal(3000 * PerKn, r.Performance.Electric.ChargeAtFull, 6);   // 191,194 a second
            Assert.Equal(3000 * PerKn, r.ElectricDemand, 6);
            Assert.Equal(20_000, r.Performance.StartMass);
            Assert.Equal(20_000, r.Performance.EndMass);                        // nothing with mass is burned
        }

        [Fact]
        public void Charge_IsProportionalToThrust()
        {
            var drive = new ElectricDrive(3_000_000, 3000 * PerKn, new ShipPower());
            Assert.Equal(PerKn, drive.ChargeFor(1000), 9);           // 1 kN
            Assert.Equal(300 * PerKn, drive.ChargeFor(300_000), 6);   // part throttle: 10 %
        }

        [Fact]
        public void Generators_KeepUp_DeltaVUnlimited()
        {
            // 20 t at 1 g: 196 kN, 12,500 charge a second; an 80,000 a second generator keeps up.
            var power = new ShipPower { Stored = 100_000, Capacity = 100_000, Steady = 80_000 };
            var ship = new ShipPerformance(new ElectricDrive(3_000_000, 3000 * PerKn, power), 20_000);
            Assert.True(double.IsPositiveInfinity(ship.DeltaVAtAcceleration(TripCalculator.OneG)));
            Assert.Equal(3_000_000 / 20_000.0, ship.MaxAcceleration, 9);
            // At full thrust (191,000 a second) the generator doesn't: the batteries limit it.
            Assert.False(double.IsPositiveInfinity(ship.FullThrustDeltaV));
        }

        [Fact]
        public void Batteries_LimitTheBurn_AboveTheFloor()
        {
            // No generator: 1,000,000 stored of 1,000,000; the last 10 % (100,000) is never used.
            var power = new ShipPower { Stored = 1_000_000, Capacity = 1_000_000 };
            var drive = new ElectricDrive(3_000_000, 3000 * PerKn, power);
            double a = TripCalculator.OneG, mass = 20_000;
            double demand = drive.ChargeFor(a * mass);
            Assert.Equal(900_000 / demand, drive.BurnTime(a * mass), 6);
            Assert.Equal(a * 900_000 / demand, drive.DeltaVAt(a, mass), 6);
            Assert.Equal(100_000, power.Floor);
            // Already at the floor: nothing to burn.
            Assert.Equal(0, new ElectricDrive(3_000_000, 3000 * PerKn, new ShipPower { Stored = 100_000, Capacity = 1_000_000 }).DeltaVAt(a, mass));
        }

        [Fact]
        public void GeneratorsAndBatteries_TheBatteriesCoverTheDifference()
        {
            var power = new ShipPower { Stored = 1_000_000, Capacity = 1_000_000, Steady = 5_000 };
            var drive = new ElectricDrive(3_000_000, 3000 * PerKn, power);
            double thrust = 196_133;   // 20 t at 1 g
            Assert.Equal(900_000 / (drive.ChargeFor(thrust) - 5_000), drive.BurnTime(thrust), 6);
            // What the generator sustains by itself: 5,000 a second buys 5,000 / 63.73 kN.
            Assert.Equal(5_000 / PerKn * 1000 / 20_000, drive.SustainableAcceleration(20_000), 6);
        }

        [Fact]
        public void Solar_FallsWithTheSquareOfTheDistance_PlannedAtTheWeakestPoint()
        {
            double kerbin = 13_599_840_256, duna = 20_726_155_264;
            var power = new ShipPower { Solar = 1_000, SolarDistance = kerbin };
            Assert.Equal(1_000, power.SolarAt(kerbin), 6);
            Assert.Equal(1_000 * (kerbin / duna) * (kerbin / duna), power.SolarAt(duna), 6);   // about 430
            var drive = new ElectricDrive(1000, 1_000, power) { WeakestSunDistance = duna };
            Assert.Equal(power.SolarAt(duna), drive.Supply, 9);
        }

        [Fact]
        public void FuelCells_CountOnlyWhileTheirFuelLasts()
        {
            // 100 a second for 600 s: 60,000 charge, and nothing after that.
            var power = new ShipPower { Stored = 100_000, Capacity = 100_000, FuelCellRate = 100, FuelCellSeconds = 600 };
            Assert.Equal(60_000, power.FuelCellEnergy, 9);
            Assert.Equal(90_000 + 60_000, power.Usable, 9);
            Assert.Equal(0, power.SupplyAt(double.NaN));   // not steady
        }

        [Fact]
        public void Mixed_ElectricEnginesCountAtWhatTheGeneratorsSustain()
        {
            // A fuel engine plus a GIANT: the GIANT counts only at the share of full thrust the generators keep up with.
            var fuel = new EngineReading
            {
                Name = "Nerv", Thrust = 60_000, VacuumIsp = 800,
                Propellants = new List<PropellantUse> { new PropellantUse("LiquidFuel", 1, 0.005) },
            };
            var power = new ShipPower { Stored = 1e6, Capacity = 1e6, Steady = 3000 * PerKn / 10 };   // 10 % of the GIANT's need
            ShipEngineReport r = ShipEngines.Combine(new[] { fuel, Giant() }, new Dictionary<string, double> { ["LiquidFuel"] = 1000 },
                20_000, power: power);
            Assert.False(r.ElectricOnly);
            Assert.Null(r.Performance.Electric);
            Assert.Equal(0.10, r.PowerLimit, 6);
            Assert.Equal(60_000 + 300_000, r.Performance.FullThrust, 3);   // the GIANT at 10 %
            Assert.True(r.Performance.VacuumIsp > 800);                     // its thrust costs no fuel
            Assert.True(r.Performance.FullThrustDeltaV > 0);
        }

        [Fact]
        public void Mixed_FuelGone_ElectricEnginesAloneArentCounted()
        {
            var fuel = new EngineReading
            {
                Name = "Nerv", Thrust = 60_000, VacuumIsp = 800,
                Propellants = new List<PropellantUse> { new PropellantUse("LiquidFuel", 1, 0.005) },
            };
            ShipEngineReport r = ShipEngines.Combine(new[] { fuel, Giant() }, new Dictionary<string, double> { ["LiquidFuel"] = 0 },
                20_000, power: new ShipPower { Steady = 1e9 });
            Assert.Equal(0, r.Performance.FullThrustDeltaV);
        }
            // ---- switched-off power parts (the Nuclear Deep Space Probe's reactor was off in Kris's save)

        private static ElectricDrive Drive(ShipPower p) => new ElectricDrive(9_000_000, 9000 * PerKn, p);

        [Fact]
        public void SwitchedOff_GeneratorThatWouldKeepUp_SaysNoBatteryLimit()
        {
            var p = new ShipPower { Stored = 9.6e6, Capacity = 9.6e6 };
            p.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.Generator, Rate = 80_000 });
            string note = PowerAdvice.SwitchedOffNote(Drive(p), 37_090, TripCalculator.OneG);   // needs 23,187 a second
            Assert.Equal("Your generator is switched off. Turn it on and this trip needs no battery limit: the ship would make " +
                         "80,000 a second of electric charge, and the engines need 23,189 a second at 1.00 g.", note);
        }

        [Fact]
        public void SwitchedOff_ReactorOfUnknownOutput_SaysWhatItWouldNeedToMake()
        {
            var p = new ShipPower { Stored = 9.6e6, Capacity = 9.6e6 };
            p.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.Reactor });
            string note = PowerAdvice.SwitchedOffNote(Drive(p), 37_090, TripCalculator.OneG);
            Assert.Equal("Your reactor is switched off. Turn it on: if the ship then makes 23,189 a second of electric charge or more, " +
                         "this trip needs no battery limit.", note);
        }

        [Fact]
        public void SwitchedOff_SolarThatHelpsButIsntEnough_GivesTheDeltaV()
        {
            var p = new ShipPower { Stored = 9.6e6, Capacity = 9.6e6, SolarDistance = 13_599_840_256 };
            p.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.SolarPanel, Rate = 10_000 });
            p.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.SolarPanel, Rate = 10_000 });
            p.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.FuelCell, Rate = 100, Seconds = 600 });
            ElectricDrive d = Drive(p);
            string note = PowerAdvice.SwitchedOffNote(d, 37_090, TripCalculator.OneG);
            Assert.StartsWith("Your fuel cell is switched off and your 2 solar panels are folded. Turn them on and the ship's delta-v at 1.00 g goes from ", note);
            // 20,000 of 23,189 a second covered: the batteries cover the rest for longer.
            ShipPower on = p.WithSwitchedOffOn();
            Assert.Equal(20_000, on.Solar, 6);
            Assert.Equal(60_000, on.FuelCellEnergy, 6);
            Assert.Empty(on.SwitchedOff);
        }

        [Fact]
        public void SwitchedOff_NothingToGain_NoNote()
        {
            var powered = new ShipPower { Stored = 1e6, Capacity = 1e6, Steady = 1e6 };
            powered.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.Reactor });
            Assert.Null(PowerAdvice.SwitchedOffNote(Drive(powered), 37_090, TripCalculator.OneG));   // already powered all the way
            Assert.Null(PowerAdvice.SwitchedOffNote(Drive(new ShipPower { Stored = 1e6, Capacity = 1e6 }), 37_090, TripCalculator.OneG));
        }
    }
}
