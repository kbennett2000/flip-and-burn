using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>The trip saved with the ship reads back exactly (ADR 0004).</summary>
    public class TripRecordTests
    {
        private static TripRecord Sample() => new TripRecord
        {
            Target = "Duna",
            Trip = TripType.CoastInTheMiddle,
            Flip = FlipManeuver.SkewFlip,
            ChosenAcceleration = 0.4 * TripCalculator.OneG,
            Acceleration = 0.1 + 0.2,                 // not exactly representable: must still round-trip
            ParkingAltitude = 60_000,
            DeltaVBudget = 148_230.123456789,
            DeltaVUsed = 1e-300,
            StartUT = 18377814390.812345,
            PlannedArrivalUT = 18378245603.999999,
            LastFlownUT = 18377814450.000001,
            Replans = 3,
            StartPlannedTripTime = 431213.98765432101,
            LossAllowance = 2387.9876543,
            CutEngines = new List<uint> { 4294967295, 1, 2_147_483_648 },
        };

        private static void AssertSame(TripRecord a, TripRecord b)
        {
            Assert.Equal(a.Version, b.Version);
            Assert.Equal(a.Target, b.Target);
            Assert.Equal(a.Trip, b.Trip);
            Assert.Equal(a.Flip, b.Flip);
            // Exactly the same doubles, bit for bit.
            Assert.Equal(a.ChosenAcceleration, b.ChosenAcceleration);
            Assert.Equal(a.Acceleration, b.Acceleration);
            Assert.Equal(a.ParkingAltitude, b.ParkingAltitude);
            Assert.Equal(a.DeltaVBudget, b.DeltaVBudget);
            Assert.Equal(a.DeltaVUsed, b.DeltaVUsed);
            Assert.Equal(a.StartUT, b.StartUT);
            Assert.Equal(a.PlannedArrivalUT, b.PlannedArrivalUT);
            Assert.Equal(a.LastFlownUT, b.LastFlownUT);
            Assert.Equal(a.Replans, b.Replans);
            Assert.Equal(a.StartPlannedTripTime, b.StartPlannedTripTime);
            Assert.Equal(a.CutEngines, b.CutEngines);
            Assert.Equal(a.LossAllowance, b.LossAllowance);
        }

        [Fact]
        public void SavedAndReadBack_Exactly()
        {
            TripRecord saved = Sample();
            TripRecord read = TripRecord.FromValues(saved.ToValues(), out string problem);
            Assert.Null(problem);
            Assert.NotNull(read);
            AssertSame(saved, read);
        }

        [Fact]
        public void ReadBackExactly_WhateverTheLanguageSettings()
        {
            // A German Windows writes 0,3 for 0.3 if a number is formatted carelessly.
            CultureInfo was = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                TripRecord saved = Sample();
                List<KeyValuePair<string, string>> values = saved.ToValues();
                Assert.DoesNotContain(values, kv => kv.Value.Contains(",") && kv.Key != "cutEngines");
                Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
                AssertSame(saved, TripRecord.FromValues(values, out _));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = was;
            }
        }

        [Fact]
        public void NoCutEngines_ReadsBackEmpty()
        {
            TripRecord saved = Sample();
            saved.CutEngines.Clear();
            TripRecord read = TripRecord.FromValues(saved.ToValues(), out _);
            Assert.Empty(read.CutEngines);
        }

        [Fact]
        public void MissingValue_NotFlown()
        {
            List<KeyValuePair<string, string>> values = Sample().ToValues().Where(kv => kv.Key != "lastFlownUT").ToList();
            Assert.Null(TripRecord.FromValues(values, out string problem));
            Assert.Contains("lastFlownUT", problem);
        }

        [Fact]
        public void UnreadableValue_NotFlown()
        {
            List<KeyValuePair<string, string>> values = Sample().ToValues()
                .Select(kv => kv.Key == "acceleration" ? new KeyValuePair<string, string>(kv.Key, "lots") : kv).ToList();
            Assert.Null(TripRecord.FromValues(values, out string problem));
            Assert.NotNull(problem);
        }

        [Fact]
        public void FromANewerVersion_NotFlown()
        {
            List<KeyValuePair<string, string>> values = Sample().ToValues()
                .Select(kv => kv.Key == "version" ? new KeyValuePair<string, string>(kv.Key, "99") : kv).ToList();
            Assert.Null(TripRecord.FromValues(values, out string problem));
            Assert.Contains("newer", problem);
        }

        [Fact]
        public void WithoutTheStartPlan_ReadsAsNotSaved()
        {
            // Records written before the arrival report kept the Start plan read fine; the report then falls back.
            List<KeyValuePair<string, string>> values = Sample().ToValues().Where(kv => kv.Key != "startPlannedTripTime").ToList();
            TripRecord read = TripRecord.FromValues(values, out string problem);
            Assert.Null(problem);
            Assert.True(double.IsNaN(read.StartPlannedTripTime));
        }

        [Fact]
        public void WithoutTheLossAllowance_ReadsAsNone()
        {
            List<KeyValuePair<string, string>> values = Sample().ToValues().Where(kv => kv.Key != "lossAllowance").ToList();
            TripRecord read = TripRecord.FromValues(values, out string problem);
            Assert.Null(problem);
            Assert.Equal(0, read.LossAllowance);
        }

        [Fact]
        public void DeltaVLeft_NeverBelowZero()
        {
            TripRecord r = Sample();
            r.DeltaVUsed = r.DeltaVBudget + 10;
            Assert.Equal(0, r.DeltaVLeft);
        }
            // ------------------------------------------------------------------ fleet flights (ADR 0006)

        private static TripRecord FleetSample()
        {
            TripRecord r = Sample();
            r.PausedByPlayer = true;
            r.FleetSinceUT = 18377814400.123456789;
            r.HandoverUT = 18377814450.000001;
            r.HandoverPosition = new Vec3(1.1e10, -2.2e9, 0.1 + 0.2);
            r.HandoverVelocity = new Vec3(-12345.678901, 0.1, 1e-12);
            r.Away = new AwayShip
            {
                TurnRate = 5 * System.Math.PI / 180,
                FullThrust = 1_453_000.123,
                ExhaustVelocity = 1.0e6 / 3,
                IspThrottleExponent = 0.5,
                DryMass = 127_421.987,
                Propellants = new List<KeyValuePair<string, double>> { new KeyValuePair<string, double>("LqdDeuterium", 0.25), new KeyValuePair<string, double>("LqdHe3", 0.75) },
                ChargePerNewton = 0.0123,
                ChargeMade = 12.5,
                ChargeFloor = 100,
                Facing = new Vec3(0.6, 0.8, 0),
            };
            return r;
        }

        [Fact]
        public void FleetFigures_SavedAndReadBack_Exactly()
        {
            TripRecord saved = FleetSample();
            TripRecord read = TripRecord.FromValues(saved.ToValues(), out string problem);
            Assert.Null(problem);
            AssertSame(saved, read);
            Assert.True(read.PausedByPlayer);
            Assert.Equal(saved.FleetSinceUT, read.FleetSinceUT);
            Assert.Equal(saved.HandoverUT, read.HandoverUT);
            Assert.Equal(saved.HandoverPosition, read.HandoverPosition);
            Assert.Equal(saved.HandoverVelocity, read.HandoverVelocity);
            AwayShip a = saved.Away, b = read.Away;
            Assert.NotNull(b);
            Assert.Equal(a.TurnRate, b.TurnRate);
            Assert.Equal(a.FullThrust, b.FullThrust);
            Assert.Equal(a.ExhaustVelocity, b.ExhaustVelocity);
            Assert.Equal(a.IspThrottleExponent, b.IspThrottleExponent);
            Assert.Equal(a.DryMass, b.DryMass);
            Assert.Equal(a.Propellants, b.Propellants);
            Assert.Equal(a.ChargePerNewton, b.ChargePerNewton);
            Assert.Equal(a.ChargeMade, b.ChargeMade);
            Assert.Equal(a.ChargeFloor, b.ChargeFloor);
            Assert.Equal(a.Facing, b.Facing);
        }

        [Fact]
        public void FleetFigures_NoPropellant_ReadsBackAsInfiniteExhaust()
        {
            TripRecord saved = FleetSample();
            saved.Away.ExhaustVelocity = double.PositiveInfinity;
            saved.Away.Propellants.Clear();
            TripRecord read = TripRecord.FromValues(saved.ToValues(), out string problem);
            Assert.Null(problem);
            Assert.True(double.IsPositiveInfinity(read.Away.ExhaustVelocity));
            Assert.Empty(read.Away.Propellants);
        }

        [Fact]
        public void BeforeFleetFlights_ReadsWithoutThem()
        {
            // A trip saved by an earlier version: no away figures, not paused by the player, no handover.
            TripRecord read = TripRecord.FromValues(Sample().ToValues(), out string problem);
            Assert.Null(problem);
            Assert.Null(read.Away);
            Assert.False(read.PausedByPlayer);
            Assert.True(double.IsNaN(read.HandoverUT));
            Assert.True(double.IsNaN(read.FleetSinceUT));
            Assert.Equal(1, read.Version);   // still written in the format earlier versions read
        }

        [Fact]
        public void FleetFigures_Broken_NotFlown()
        {
            List<KeyValuePair<string, string>> values = FleetSample().ToValues()
                .Select(kv => kv.Key == "awayDryMass" ? new KeyValuePair<string, string>(kv.Key, "lots") : kv).ToList();
            Assert.Null(TripRecord.FromValues(values, out string problem));
            Assert.NotNull(problem);
        }

        [Fact]
        public void PropellantShares_ByMass_WeightedByEachEnginesFlow()
        {
            // Two engines with the same exhaust velocity: the one with twice the thrust burns twice the propellant mass.
            var a = new EngineReading { Thrust = 2000, VacuumIsp = 300, Propellants = { new PropellantUse("LiquidFuel", 0.9, 0.005), new PropellantUse("Oxidizer", 1.1, 0.005) } };
            var b = new EngineReading { Thrust = 1000, VacuumIsp = 300, Propellants = { new PropellantUse("XenonGas", 1, 0.0001), new PropellantUse("ElectricCharge", 18, 0) } };
            Dictionary<string, double> shares = AwayShip.MassShares(new[] { a, b }).ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(3, shares.Count);   // electric charge has no mass: not a propellant here
            Assert.Equal(2.0 / 3 * 0.45, shares["LiquidFuel"], 9);
            Assert.Equal(2.0 / 3 * 0.55, shares["Oxidizer"], 9);
            Assert.Equal(1.0 / 3, shares["XenonGas"], 9);
        }
    }
}
