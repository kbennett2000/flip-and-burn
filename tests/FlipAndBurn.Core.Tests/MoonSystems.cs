using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>
    /// A planet's own system, for simulating trips among its moons (ADR 0005): the planet stands still at the origin and
    /// its moons orbit it. The ship starts in orbit around the planet or one of its moons.
    /// </summary>
    public sealed class SimSystem
    {
        public PlanetRecord Planet;        // as a record standing at the centre
        public PlanetRecord Departure;     // the body the ship starts in orbit around (the centre record, or a moon)
        public double StartAltitude;
        public double StartAngle;          // rad, where on that orbit the ship starts
        public PlanetRecord[] Others;      // the system's moons

        public double CentralGravParameter => Planet.GravParameter;

        /// <summary>The planet's lowest safe radius: the top of its atmosphere (or its highest terrain) plus 1 km.</summary>
        public double CentralSafeRadius => Planet.Radius + Flights.TargetSafeAltitude(Planet);

        /// <summary>Start in orbit around <paramref name="start"/> (the planet itself, or one of its moons).</summary>
        public static SimSystem Around(PlanetRecord planet, PlanetRecord start, double startAltitude, double startAngle = 0)
        {
            PlanetRecord centre = planet.AtCentre();
            return new SimSystem
            {
                Planet = centre,
                Departure = start == planet ? centre : start,
                StartAltitude = startAltitude,
                StartAngle = startAngle,
                Others = KerbolSystem.MoonsOf(planet),
            };
        }

        /// <summary>The record to fly to: the centre record for the planet itself, or the moon.</summary>
        public PlanetRecord TargetRecord(PlanetRecord body) => body.Name == Planet.Name ? Planet : body;

        public TargetPlanet DepartureFor()
        {
            TargetPlanet d = Departure.AsTarget();
            d.SphereOfInfluence = Departure.SphereOfInfluence;
            d.SafeRadius = Departure.Radius + Flights.TargetSafeAltitude(Departure) + 10_000;
            d.IsCentralBody = Departure.IsCentre;
            return d;
        }

        public void Configure(GuidanceConfig config)
        {
            config.PlanetSystemTrip = true;
            config.WaitForDepartureWindow = true;
            config.SunSafetyDistance = CentralSafeRadius;
            config.DetourClearance = DetourClearance(config.Acceleration);
            config.Obstacles = ObstaclesFor(config.Target.Name);
            // Flying to the planet itself: it is the target, and its pull is the target's (counted once).
            if (config.Target.Name == Planet.Name) config.SunGravParameter = 0;
        }

        public void Configure(TripRequest request)
        {
            request.PlanetSystemTrip = true;
            request.WaitForDepartureWindow = true;
            request.SunSafetyDistance = CentralSafeRadius;
            request.DetourClearance = DetourClearance(request.Acceleration);
            request.Obstacles = ObstaclesFor(request.Target.Name);
            if (request.Target.Name == Planet.Name) request.SunGravParameter = 0;
        }

        /// <summary>As the game does it (ADR 0005): 1.2 times the safe radius, and where the planet pulls at most 10 % of the thrust.</summary>
        public double DetourClearance(double acceleration) =>
            Math.Max(1.2 * CentralSafeRadius, Math.Sqrt(10 * CentralGravParameter / acceleration));

        /// <summary>The moons to keep clear of: all but the target and the one being left.</summary>
        public List<Obstacle> ObstaclesFor(string targetName) => Others
            .Where(m => m.Name != targetName && m.Name != Departure.Name)
            .Select(m => new Obstacle
            {
                Name = m.Name, Ephemeris = m.Ephemeris, GravParameter = m.GravParameter, SphereOfInfluence = m.SphereOfInfluence,
                SafeRadius = m.Radius + Flights.TargetSafeAltitude(m),
            }).ToList();

        /// <summary>The game's default parking altitude for a body (ParkingOrbit), from the simulator's atmosphere tops and terrain.</summary>
        public static double DefaultParking(PlanetRecord body)
        {
            double safe = Flights.TargetSafeAltitude(body) - 1000;
            bool air = body.Name == "Kerbin" || body.Name == "Duna" || body.Name == "Eve" || body.Name == "Jool" || body.Name == "Laythe";
            return ParkingOrbit.DefaultAltitude(body.Radius, air ? safe : 0, air ? 0 : safe,
                body.IsCentre ? double.PositiveInfinity : body.SphereOfInfluence);
        }

        /// <summary>Everything the trip must keep clear of: the planet and every moon but the target.</summary>
        public IEnumerable<PlanetRecord> Obstacles(PlanetRecord target) =>
            new[] { Planet }.Concat(Others).Where(b => b.Name != target.Name);
    }
}
