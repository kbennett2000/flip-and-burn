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
            Assert.Equal(a.CutEngines, b.CutEngines);
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
        public void DeltaVLeft_NeverBelowZero()
        {
            TripRecord r = Sample();
            r.DeltaVUsed = r.DeltaVBudget + 10;
            Assert.Equal(0, r.DeltaVLeft);
        }
    }
}
