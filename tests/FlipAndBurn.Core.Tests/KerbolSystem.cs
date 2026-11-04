using System;
using System.Linq;
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

        // ---- Stock moons (sharedassets9.assets, same extraction). Their orbits are around their planet.
        public static readonly PlanetRecord Mun = new PlanetRecord("Mun", radius: 200000.0, geeAsl: 0.1660567000983534,
            sma: 12000000.0, ecc: 0.0, incDeg: 0.0, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 1.70000004768372, epoch: 0.0, parent: Kerbin);
        public static readonly PlanetRecord Minmus = new PlanetRecord("Minmus", radius: 60000.0, geeAsl: 0.05001708099187999,
            sma: 47000000.0, ecc: 0.0, incDeg: 6.0, lanDeg: 78.0,
            argPeDeg: 38.0, meanAnomalyAtEpochRad: 0.899999976158142, epoch: 0.0, parent: Kerbin);
        public static readonly PlanetRecord Gilly = new PlanetRecord("Gilly", radius: 13000.0, geeAsl: 0.00500170791285985,
            sma: 31500000.0, ecc: 0.550000011920929, incDeg: 12.0, lanDeg: 80.0,
            argPeDeg: 10.0, meanAnomalyAtEpochRad: 0.899999976158142, epoch: 0.0, parent: Eve);
        public static readonly PlanetRecord Ike = new PlanetRecord("Ike", radius: 130000.0, geeAsl: 0.11203826321056089,
            sma: 3200000.0, ecc: 0.0299999993294477, incDeg: 0.200000002980232, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 1.70000004768372, epoch: 0.0, parent: Duna);
        public static readonly PlanetRecord Laythe = new PlanetRecord("Laythe", radius: 500000.0, geeAsl: 0.8002732958700793,
            sma: 27184000.0, ecc: 0.0, incDeg: 0.0, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0, parent: Jool);
        public static readonly PlanetRecord Vall = new PlanetRecord("Vall", radius: 300000.0, geeAsl: 0.23508027656261707,
            sma: 43152000.0, ecc: 0.0, incDeg: 0.0, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 0.899999976158142, epoch: 0.0, parent: Jool);
        public static readonly PlanetRecord Tylo = new PlanetRecord("Tylo", radius: 600000.0, geeAsl: 0.8002732958700793,
            sma: 68500000.0, ecc: 0.0, incDeg: 0.025000000372529, lanDeg: 0.0,
            argPeDeg: 0.0, meanAnomalyAtEpochRad: 3.14000010490417, epoch: 0.0, parent: Jool);
        public static readonly PlanetRecord Bop = new PlanetRecord("Bop", radius: 65000.0, geeAsl: 0.06002049495431823,
            sma: 128500000.0, ecc: 0.234999999403954, incDeg: 15.0, lanDeg: 10.0,
            argPeDeg: 25.0, meanAnomalyAtEpochRad: 0.899999976158142, epoch: 0.0, parent: Jool);
        public static readonly PlanetRecord Pol = new PlanetRecord("Pol", radius: 44000.0, geeAsl: 0.03801298098739121,
            sma: 179890000.0, ecc: 0.17085, incDeg: 4.25, lanDeg: 2.0,
            argPeDeg: 15.0, meanAnomalyAtEpochRad: 0.899999976158142, epoch: 0.0, parent: Jool);

        /// <summary>Each stock planet's moons.</summary>
        public static PlanetRecord[] MoonsOf(PlanetRecord planet) => new[] { Mun, Minmus, Gilly, Ike, Laythe, Vall, Tylo, Bop, Pol }
            .Where(m => m.Parent == planet).ToArray();

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

        /// <summary>The planet a moon orbits; null for a planet (it orbits the Sun).</summary>
        public PlanetRecord Parent { get; }

        /// <summary>
        /// Where the body is, relative to the centre of the frame a trip is flown in: its orbit, or the origin for a planet
        /// standing at the centre of its own system (<see cref="AtCentre"/>).
        /// </summary>
        public IEphemeris Ephemeris { get; }

        /// <summary>True for a planet standing at the centre of its own system.</summary>
        public bool IsCentre { get; }

        public PlanetRecord(string name, double radius, double geeAsl, double sma, double ecc, double incDeg, double lanDeg,
            double argPeDeg, double meanAnomalyAtEpochRad, double epoch, PlanetRecord parent = null)
        {
            Name = name;
            Radius = radius;
            GeeAsl = geeAsl;
            GravParameter = KerbolSystem.KspGravParameter(radius, geeAsl);
            Parent = parent;
            Orbit = new KeplerOrbit(sma, ecc, incDeg, lanDeg, argPeDeg, meanAnomalyAtEpochRad, epoch,
                parent?.GravParameter ?? KerbolSystem.SunGravParameter);
            Ephemeris = Orbit;
        }

        private PlanetRecord(PlanetRecord planet)
        {
            Name = planet.Name;
            Radius = planet.Radius;
            GeeAsl = planet.GeeAsl;
            GravParameter = planet.GravParameter;
            Orbit = planet.Orbit;
            Ephemeris = new FixedEphemeris(Vec3.Zero);
            IsCentre = true;
        }

        /// <summary>The same planet standing still at the origin: the centre of its own system, for trips among its moons.</summary>
        public PlanetRecord AtCentre() => new PlanetRecord(this);

        public TargetPlanet AsTarget() => new TargetPlanet(Name, Ephemeris, GravParameter, Radius);

        /// <summary>The same planet frozen where it is at <paramref name="ut"/>.</summary>
        public TargetPlanet AsStillTarget(double ut) => new TargetPlanet(Name, new FixedEphemeris(Orbit.StateAt(ut).Position), GravParameter, Radius);

        /// <summary>KSP's sphere of influence formula: a * (mu / muParent)^0.4. A planet at the centre of its system: everywhere.</summary>
        public double SphereOfInfluence => IsCentre ? double.PositiveInfinity
            : Orbit.SemiMajorAxis * Math.Pow(GravParameter / (Parent?.GravParameter ?? KerbolSystem.SunGravParameter), 0.4);
    }
}
