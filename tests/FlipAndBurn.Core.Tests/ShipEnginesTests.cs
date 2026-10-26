using System;
using System.Collections.Generic;
using FlipAndBurn.Core;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// The ship's thrust and delta-v from its active engines, one engine type at a time. Engine figures are the part
    /// configs' own (vacuum thrust in kN, vacuum Isp from atmosphereCurve key 0, the PROPELLANT ratios); resource
    /// densities are KSP's (Squad/Resources/ResourcesGeneric.cfg). Each expected value is worked out by hand in the test.
    /// </summary>
    public class ShipEnginesTests
    {
        private const double G0 = 9.80665;
        private const double LfDensity = 0.005, OxDensity = 0.005, XenonDensity = 0.0001;   // t/unit

        private static PropellantUse Lf(double ratio = 0.9) => new PropellantUse("LiquidFuel", ratio, LfDensity);
        private static PropellantUse Ox(double ratio = 1.1) => new PropellantUse("Oxidizer", ratio, OxDensity);

        // ---- stock chemical: LV-TX87 "Bobcat" (Making History), 400 kN, Isp 310 s, LF 0.9 / Ox 1.1
        private static EngineReading Bobcat() => new EngineReading
        {
            Name = "LV-TX87 Bobcat", Thrust = 400_000, VacuumIsp = 310, Propellants = { Lf(), Ox() },
        };

        [Fact]
        public void StockChemical_IsTheRocketEquation()
        {
            // 10 t dry; 360 LF + 440 Ox (the engine's 0.9 : 1.1 mix exactly) = 800 units x 0.005 t = 4 t of fuel.
            var fuel = new Dictionary<string, double> { ["LiquidFuel"] = 360, ["Oxidizer"] = 440 };
            ShipEngineReport r = ShipEngines.Combine(new[] { Bobcat() }, fuel, 14_000);
            ShipPerformance p = r.Performance;

            double dv = 310 * G0 * Math.Log(14_000.0 / 10_000.0);   // = 1022.9 m/s
            Assert.Equal(1022.9, dv, 1);
            Assert.Equal(dv, p.FullThrustDeltaV, 6);
            Assert.Equal(dv, p.DeltaVAtAcceleration(9.81), 6);      // stock Isp doesn't change with throttle
            Assert.Equal(400_000 / 14_000.0, p.MaxAcceleration, 9);
            Assert.False(r.Estimated);
            Assert.Empty(r.Notes);
        }

        [Fact]
        public void StockChemical_StopsWhenOnePropellantRunsOut()
        {
            // 360 LF but only 220 Ox: the mix needs 1.1 Ox per 0.9 LF, so 220 Ox burns with 180 LF; 180 LF is left over.
            var fuel = new Dictionary<string, double> { ["LiquidFuel"] = 360, ["Oxidizer"] = 220 };
            ShipPerformance p = ShipEngines.Combine(new[] { Bobcat() }, fuel, 12_900).Performance;
            double burned = (180 + 220) * 0.005 * 1000;             // 2000 kg
            Assert.Equal(12_900 - burned, p.EndMass, 6);
            Assert.Equal(310 * G0 * Math.Log(12_900 / (12_900 - burned)), p.FullThrustDeltaV, 6);
        }

        // ---- nuclear: LV-N "Nerv", 60 kN, Isp 800 s, LiquidFuel only
        [Fact]
        public void Nuclear_LiquidFuelOnly()
        {
            var nerv = new EngineReading { Name = "LV-N Nerv", Thrust = 60_000, VacuumIsp = 800, Propellants = { Lf() } };
            // 8 t dry + 900 LF (4.5 t)
            ShipPerformance p = ShipEngines.Combine(new[] { nerv }, new Dictionary<string, double> { ["LiquidFuel"] = 900 }, 12_500).Performance;
            double dv = 800 * G0 * Math.Log(12_500.0 / 8_000.0);    // = 3501.3 m/s
            Assert.Equal(3501.3, dv, 1);
            Assert.Equal(dv, p.FullThrustDeltaV, 6);
            Assert.Equal(60_000 / 12_500.0, p.MaxAcceleration, 9);
            Assert.Equal(0, p.DeltaVAtAcceleration(9.81));          // 4.8 m/s^2 is all it has
            Assert.Equal(dv, p.DeltaVAtAcceleration(4.0), 6);
        }

        // ---- other mods' engines on the stock module: Near Future Launch Vehicles RD-704, 1050 kN, Isp 345 s, LF/Ox
        [Fact]
        public void ModEngineOnTheStockModule_IsReadLikeStock()
        {
            var rd704 = new EngineReading { Name = "NFLV RD-704", Thrust = 1_050_000, VacuumIsp = 345, Propellants = { Lf(), Ox() } };
            var fuel = new Dictionary<string, double> { ["LiquidFuel"] = 1800, ["Oxidizer"] = 2200 };   // 20 t
            ShipPerformance p = ShipEngines.Combine(new[] { rd704 }, fuel, 50_000).Performance;
            Assert.Equal(345 * G0 * Math.Log(50_000.0 / 30_000.0), p.FullThrustDeltaV, 6);   // 1728.2 m/s
            Assert.Equal(21.0, p.MaxAcceleration, 9);
        }

        // ---- multi-mode: RAPIER. Air-breathing (IntakeAir 6 / LF 1, 105 kN, Isp 3200) or closed cycle (180 kN, Isp 305).
        [Fact]
        public void MultiMode_AirBreathingModeIsLeftOutWithANote()
        {
            var airBreathing = new EngineReading
            {
                Name = "RAPIER", Thrust = 105_000, VacuumIsp = 3200,
                Propellants = { new PropellantUse("IntakeAir", 6, 0.005), new PropellantUse("LiquidFuel", 1, LfDensity) },
            };
            ShipEngineReport r = ShipEngines.Combine(new[] { airBreathing }, new Dictionary<string, double> { ["LiquidFuel"] = 900 }, 10_000);
            Assert.True(r.NoThrust);
            Assert.Contains(r.Notes, n => n.Contains("air-breathing") && n.Contains("RAPIER"));
        }

        [Fact]
        public void MultiMode_ClosedCycleIsAnOrdinaryRocket()
        {
            var closed = new EngineReading { Name = "RAPIER", Thrust = 180_000, VacuumIsp = 305, Propellants = { Lf(), Ox() } };
            var fuel = new Dictionary<string, double> { ["LiquidFuel"] = 360, ["Oxidizer"] = 440 };
            ShipPerformance p = ShipEngines.Combine(new[] { closed }, fuel, 9_000).Performance;
            Assert.Equal(305 * G0 * Math.Log(9_000.0 / 5_000.0), p.FullThrustDeltaV, 6);      // 1758.0 m/s
            Assert.Equal(20.0, p.MaxAcceleration, 9);
        }

        // ---- electric: IX-6315 "Dawn", 2 kN, Isp 4200 s, XenonGas 0.1 / ElectricCharge 1.8
        private static EngineReading Dawn() => new EngineReading
        {
            Name = "IX-6315 Dawn", Thrust = 2_000, VacuumIsp = 4200,
            Propellants = { new PropellantUse("ElectricCharge", 1.8, 0), new PropellantUse("XenonGas", 0.1, XenonDensity) },
        };

        [Fact]
        public void Electric_FullThrustWhenThePowerKeepsUp()
        {
            // Mass flow 2000 / (4200 x 9.80665) = 0.048557 kg/s. One unit of mix weighs 0.1 x 0.0001 t = 0.01 kg, so
            // 4.8557 units/s: 0.48557 Xenon/s and 4.8558 x 1.8 = 8.7404 EC/s (KSP's Dawn: 0.486 Xe/s, 8.74 EC/s).
            double ecPerSecond = 2000 / (4200 * G0) / (0.1 * XenonDensity * 1000) * 1.8;
            Assert.Equal(8.7404, ecPerSecond, 4);

            // 1 t dry + 1000 Xenon (0.1 t), 10 EC/s of solar panels.
            ShipEngineReport r = ShipEngines.Combine(new[] { Dawn() }, new Dictionary<string, double> { ["XenonGas"] = 1000 }, 1_100, powerSupply: 10);
            Assert.Equal(ecPerSecond, r.ElectricDemand, 6);
            Assert.Equal(1, r.PowerLimit);
            Assert.Empty(r.Notes);
            Assert.Equal(4200 * G0 * Math.Log(1.1), r.Performance.FullThrustDeltaV, 6);        // 3925.4 m/s
            Assert.Equal(2000 / 1100.0, r.Performance.MaxAcceleration, 9);
        }

        [Fact]
        public void Electric_PowerSupplyLimitsThrustAndSaysSo()
        {
            // Half the power it needs: half the thrust, same Isp, so the same delta-v.
            double demand = 2000 / (4200 * G0) / (0.1 * XenonDensity * 1000) * 1.8;
            ShipEngineReport r = ShipEngines.Combine(new[] { Dawn() }, new Dictionary<string, double> { ["XenonGas"] = 1000 }, 1_100,
                powerSupply: demand / 2, batteryCharge: 1000);
            Assert.Equal(0.5, r.PowerLimit, 9);
            Assert.Equal(1000 / 1100.0, r.Performance.MaxAcceleration, 9);
            Assert.Equal(4200 * G0 * Math.Log(1.1), r.Performance.FullThrustDeltaV, 6);
            Assert.Contains(r.Notes, n => n.Contains("power supply") && n.Contains("50%"));
        }

        [Fact]
        public void Electric_NoPowerSupplyMeansNoThrust()
        {
            ShipEngineReport r = ShipEngines.Combine(new[] { Dawn() }, new Dictionary<string, double> { ["XenonGas"] = 1000 }, 1_100, powerSupply: 0);
            Assert.True(r.NoThrust);
            Assert.Contains(r.Notes, n => n.Contains("no power supply"));
        }

        // ---- KSP Interstellar: the Bebop Refueller's Kerbstein drive (the special handling that already exists)
        [Fact]
        public void KspInterstellarFusion_KeepsItsThrottleRule()
        {
            // 152,642.51 kg wet, 11,220.13 kg of LqdDeuterium (0.0001624 t/unit), 4,746,093.26 N, Isp 281,249.97 s, k = 0.5.
            var kerbstein = new EngineReading
            {
                Name = "Kerbstein", Thrust = 4_746_093.26171875, VacuumIsp = 281_249.96875, IspThrottleExponent = 0.5,
                Propellants = { new PropellantUse("LqdDeuterium", 1, 0.0001624) },
            };
            double units = (152_642.51278340816 - 141_422.38260853774) / 1000 / 0.0001624;
            ShipPerformance p = ShipEngines.Combine(new[] { kerbstein }, new Dictionary<string, double> { ["LqdDeuterium"] = units }, 152_642.51278340816).Performance;
            Assert.Equal(141_422.38260853774, p.EndMass, 3);
            Assert.Equal(116_051, p.DeltaVAtAcceleration(9.81), 0);   // the window's "116 km/s at 1.00 g"
            Assert.Equal(210_576, p.FullThrustDeltaV, 0);              // "211 km/s" at full thrust
            Assert.Equal(3.17, p.MaxAcceleration / 9.81, 2);
        }

        // ---- several engine types at once: a Nerv (LF) and a Bobcat (LF + Ox) on one fuel supply
        [Fact]
        public void MixedEngines_BurnTogetherThenTheNervCarriesOn()
        {
            // 20 t dry + 1800 LF (9 t) + 1100 Ox (5.5 t) = 34.5 t.
            var nerv = new EngineReading { Name = "LV-N Nerv", Thrust = 60_000, VacuumIsp = 800, Propellants = { Lf() } };
            var fuel = new Dictionary<string, double> { ["LiquidFuel"] = 1800, ["Oxidizer"] = 1100 };
            ShipPerformance p = ShipEngines.Combine(new[] { nerv, Bobcat() }, fuel, 34_500).Performance;

            // By hand. Mass flows: Nerv 60000 / (800 g0) = 7.6479 kg/s; Bobcat 400000 / (310 g0) = 131.58 kg/s.
            double nervFlow = 60_000 / (800 * G0), bobcatFlow = 400_000 / (310 * G0);
            // Units per second: Nerv's mix is 0.9 LF at 4.5 kg/unit of mix -> LF = flow / 4.5 x 0.9;
            // Bobcat's mix is 0.9 LF + 1.1 Ox at 10 kg/unit -> LF = flow / 10 x 0.9, Ox = flow / 10 x 1.1.
            double nervLf = nervFlow / 4.5 * 0.9, bobcatLf = bobcatFlow / 10 * 0.9, bobcatOx = bobcatFlow / 10 * 1.1;
            // Together the Oxidizer runs out first (1100 / 14.47 = 76 s; LF would last 1800 / 13.37 = 135 s).
            double t1 = 1100 / bobcatOx;
            Assert.True(t1 < 1800 / (nervLf + bobcatLf));
            double m1 = 34_500 - (nervFlow + bobcatFlow) * t1;
            double isp1 = 460_000 / ((nervFlow + bobcatFlow) * G0);
            // Then the Nerv burns the LF that's left.
            double lfLeft = 1800 - (nervLf + bobcatLf) * t1;
            double m2 = m1 - lfLeft * LfDensity * 1000;
            Assert.Equal(20_000, m2, 6);

            Assert.Equal(2, p.Phases.Count);
            Assert.Equal(460_000, p.Phases[0].Thrust, 6);
            Assert.Equal(isp1, p.Phases[0].VacuumIsp, 6);
            Assert.Equal(m1, p.Phases[0].EndMass, 3);
            Assert.Equal(60_000, p.Phases[1].Thrust, 6);
            Assert.Equal(800, p.Phases[1].VacuumIsp, 9);

            double dvBoth = isp1 * G0 * Math.Log(34_500 / m1), dvNerv = 800 * G0 * Math.Log(m1 / m2);
            Assert.Equal(dvBoth + dvNerv, p.FullThrustDeltaV, 6);
            // At 1 g only the first phase counts: the Nerv alone manages 60000 / m1 = 2.5 m/s^2.
            Assert.Equal(dvBoth, p.DeltaVAtAcceleration(9.81), 6);
            // At 0.2 g (1.96 m/s^2) the Nerv can hold it too.
            Assert.Equal(dvBoth + dvNerv, p.DeltaVAtAcceleration(0.2 * 9.81), 6);
        }

        // ---- an engine Flip and Burn doesn't know: KSP's own figures, flagged as estimates
        [Fact]
        public void UnknownEngine_UsesItsOwnFiguresAndSaysTheyAreEstimates()
        {
            var odd = new EngineReading { Name = "Z-Pinch Fusion Airospike", Thrust = 300_000, VacuumIsp = 2500, Propellants = { Lf() }, Understood = false };
            ShipEngineReport r = ShipEngines.Combine(new[] { odd }, new Dictionary<string, double> { ["LiquidFuel"] = 1000 }, 15_000);
            Assert.True(r.Estimated);
            Assert.Contains(r.Notes, n => n.Contains("Z-Pinch Fusion Airospike") && n.Contains("estimates"));
            Assert.Equal(2500 * G0 * Math.Log(15_000.0 / 10_000.0), r.Performance.FullThrustDeltaV, 6);
        }

        [Fact]
        public void NoFuel_ThrustButNoDeltaV()
        {
            ShipEngineReport r = ShipEngines.Combine(new[] { Bobcat() }, new Dictionary<string, double>(), 10_000);
            Assert.False(r.NoThrust);
            Assert.Equal(40.0, r.Performance.MaxAcceleration, 9);
            Assert.Equal(0, r.Performance.FullThrustDeltaV);
        }
    }
}
