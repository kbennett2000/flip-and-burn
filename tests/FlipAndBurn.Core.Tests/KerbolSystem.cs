using System;
using FlipAndBurn.Core;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// Planet data as KSP 1.12.5 uses it, read from the install (not from memory).
    /// </summary>
    /// <remarks>
    /// Sources:
    /// <list type="bullet">
    /// <item>Stock bodies: KSP_Data/sharedassets9.assets, the PSystem prefab. Radius and GeeASL come from each body's
    /// CelestialBody component; orbital elements from its OrbitDriver.orbit. Decoded by
    /// tools/planet-data/extract_stock_planets.py. Values are copied at full precision (several are float32 in the
    /// asset, hence digits like 0.200000002980232).</item>
    /// <item>Gravitational parameter: KSP computes it at load as Radius^2 * GeeASL * 9.80665
    /// (CelestialBody.SetupConstants; 9.80665 is PhysicsGlobals.GravitationalAcceleration). The stored value in the
    /// asset is 0, so <see cref="PlanetRecord.GravParameter"/> repeats that formula.</item>
    /// <item>Outer Planets Mod bodies: GameData/OPM/KopernicusConfigs/OuterPlanets/{Sarnus,Urlum,Neidon,Plock}.cfg
    /// (Properties radius and geeASL; Orbit node). Kopernicus reads meanAnomalyAtEpoch in radians.</item>
    /// <item>Cross-check: every Sun-orbiting body's sphere of influence, a * (mu / muSun)^0.4, matches the runtime value
    /// Kopernicus logged in Logs/Kopernicus/Kopernicus.log to within 1e-15 (see tools/planet-data).</item>
    /// </list>
    /// With OPM installed, Eeloo becomes a moon of Sarnus (GameData/OPM/KopernicusConfigs/SarnusMoons/Eeloo.cfg), so it
    /// is only a valid target in a stock game. In Kris's install the Sun-orbiting planets are Moho, Eve, Kerbin, Duna,
    /// Dres, Jool, Sarnus, Urlum, Neidon and Plock. In the game, targets will be read at runtime from the bodies whose
    /// reference body is the Sun, so OPM's planets need no special handling.
    /// </remarks>
    public static class KerbolSystem
    {
        public const double KspStandardGravity = 9.80665;

        public static double KspGravParameter(double radius, double geeAsl) => radius * radius * geeAsl * KspStandardGravity;

        public const double SunRadius = 261600000.0;
        public const double SunGeeAsl = 1.7468465610013681;
        public static readonly double SunGravParameter = KspGravParameter(SunRadius, SunGeeAsl);

        // ---- Stock (sharedassets9.assets) ----
        public static readonly PlanetRecord Moho = new PlanetRecord("Moho", radius: 250000.0, geeAsl: 0.2750939473186207,
            sma: 5263138304.0, ecc: 0.200000002980232, incDeg: 7.0, lanDeg: 70.0,
            argPeDeg: 15.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);
        public static readonly PlanetRecord Eve = new PlanetRecord("Eve", radius: 700000.0, geeAsl: 1.7005807760832998,
            sma: 9832684544.0, ecc: 0.00999999977648258, incDeg: 2.09999990463257, lanDeg: 15.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);
        public static readonly PlanetRecord Kerbin = new PlanetRecord("Kerbin", radius: 600000.0, geeAsl: 1.0003416049313476,
            sma: 13599840256.0, ecc: 0.0, incDeg: 0.0, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);
        public static readonly PlanetRecord Duna = new PlanetRecord("Duna", radius: 320000.0, geeAsl: 0.30010249340440553,
            sma: 20726155264.0, ecc: 0.0509999990463257, incDeg: 0.0599999986588955, lanDeg: 135.5,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);
        public static readonly PlanetRecord Dres = new PlanetRecord("Dres", radius: 138000.0, geeAsl: 0.11503928456710498,
            sma: 40839348203.0, ecc: 0.145, incDeg: 5.0, lanDeg: 280.0,
            argPeDeg: 90.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);
        public static readonly PlanetRecord Jool = new PlanetRecord("Jool", radius: 6000000.0, geeAsl: 0.8002732958700793,
            sma: 68773560320.0, ecc: 0.0500000007450581, incDeg: 1.30400002002716, lanDeg: 52.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 0.100000001490116, epoch: 0.0);
        /// <summary>Stock only. Under OPM, Eeloo orbits Sarnus and is not a target.</summary>
        public static readonly PlanetRecord Eeloo = new PlanetRecord("Eeloo", radius: 210000.0, geeAsl: 0.1720587618914422,
            sma: 90118820000.0, ecc: 0.26, incDeg: 6.15, lanDeg: 50.0,
            argPeDeg: 260.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0);

        // ---- Outer Planets Mod (GameData/OPM/KopernicusConfigs/OuterPlanets/*.cfg) ----
        public static readonly PlanetRecord Sarnus = new PlanetRecord("Sarnus", radius: 5300000, geeAsl: 0.298,
            sma: 125798522368, ecc: 0.0534, incDeg: 2.02, lanDeg: 184,
            argPeDeg: 0, meanAnomalyAtEpochRad: 2.88114666938782, epoch: 359.279999999964);
        public static readonly PlanetRecord Urlum = new PlanetRecord("Urlum", radius: 2177000, geeAsl: 0.257,
            sma: 254317012787, ecc: 0.045214674, incDeg: 0.64, lanDeg: 61,
            argPeDeg: 0, meanAnomalyAtEpochRad: 5.59607362747192, epoch: 422.539999999906);
        public static readonly PlanetRecord Neidon = new PlanetRecord("Neidon", radius: 2145000, geeAsl: 0.314,
            sma: 409355191706, ecc: 0.0127567, incDeg: 1.27, lanDeg: 259,
            argPeDeg: 0, meanAnomalyAtEpochRad: 2.27167344093323, epoch: 99.6799999999973);
        public static readonly PlanetRecord Plock = new PlanetRecord("Plock", radius: 189000, geeAsl: 0.148,
            sma: 535833706086, ecc: 0.26, incDeg: 6.15, lanDeg: 260,
            argPeDeg: 50, meanAnomalyAtEpochRad: 0, epoch: 213);

        public static readonly PlanetRecord[] StockSunPlanets = { Moho, Eve, Kerbin, Duna, Dres, Jool, Eeloo };
        public static readonly PlanetRecord[] OpmSunPlanets = { Moho, Eve, Kerbin, Duna, Dres, Jool, Sarnus, Urlum, Neidon, Plock };
    }

    public sealed class PlanetRecord
    {
        public string Name { get; }
        public double Radius { get; }
        public double GeeAsl { get; }
        public double GravParameter { get; }
        public KeplerOrbit Orbit { get; }

        public PlanetRecord(string name, double radius, double geeAsl, double sma, double ecc, double incDeg, double lanDeg,
            double argPeDeg, double meanAnomalyAtEpochRad, double epoch)
        {
            Name = name;
            Radius = radius;
            GeeAsl = geeAsl;
            GravParameter = KerbolSystem.KspGravParameter(radius, geeAsl);
            Orbit = new KeplerOrbit(sma, ecc, incDeg, lanDeg, argPeDeg, meanAnomalyAtEpochRad, epoch, KerbolSystem.SunGravParameter);
        }

        public TargetPlanet AsTarget() => new TargetPlanet(Name, Orbit, GravParameter, Radius);

        /// <summary>The same planet frozen where it is at <paramref name="ut"/>.</summary>
        public TargetPlanet AsStillTarget(double ut) => new TargetPlanet(Name, new FixedEphemeris(Orbit.StateAt(ut).Position), GravParameter, Radius);

        /// <summary>KSP's sphere of influence formula: a * (mu / muSun)^0.4.</summary>
        public double SphereOfInfluence => Orbit.SemiMajorAxis * Math.Pow(GravParameter / KerbolSystem.SunGravParameter, 0.4);
    }
}
