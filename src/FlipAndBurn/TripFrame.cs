using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn
{
    /// <summary>
    /// Which body a trip is planned and flown around (ADR 0005): the Sun for a trip between planets, or the planet for a
    /// trip inside its own system (to one of its moons, from a moon back to it, or between its moons). Everything the
    /// planner and the autopilot hand the core is relative to that centre.
    /// </summary>
    internal sealed class TripFrame
    {
        public CelestialBody Centre { get; }
        public CelestialBody Target { get; }

        /// <summary>A trip among a planet's moons: the centre is that planet.</summary>
        public bool PlanetSystem => Centre != Planetarium.fetch.Sun;

        private TripFrame(CelestialBody centre, CelestialBody target)
        {
            Centre = centre;
            Target = target;
        }

        /// <summary>The planet a body belongs to: itself for a planet (or the Sun), its planet for a moon.</summary>
        public static CelestialBody SystemPlanet(CelestialBody body)
        {
            CelestialBody sun = Planetarium.fetch.Sun;
            while (body != null && body != sun && body.referenceBody != null && body.referenceBody != sun) body = body.referenceBody;
            return body;
        }

        /// <summary>
        /// The frame for a trip from <paramref name="from"/> (the body the ship orbits) to <paramref name="target"/>, or null
        /// when Flip and Burn doesn't fly that trip (a moon to another planet's system).
        /// </summary>
        public static TripFrame For(CelestialBody from, CelestialBody target)
        {
            CelestialBody sun = Planetarium.fetch.Sun;
            if (from == null || target == null || target == sun || target == from) return null;
            CelestialBody home = SystemPlanet(from), targetPlanet = SystemPlanet(target);
            if (home != sun && home == targetPlanet) return new TripFrame(home, target);       // inside one planet's system
            if (target.referenceBody == sun && from == home) return new TripFrame(sun, target); // between planets
            return null;
        }

        /// <summary>The frame a saved trip was flown in (its centre's name; empty for the Sun).</summary>
        public static TripFrame Saved(string centreName, CelestialBody target)
        {
            CelestialBody centre = string.IsNullOrEmpty(centreName)
                ? Planetarium.fetch.Sun
                : FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == centreName);
            return centre == null || target == null ? null : new TripFrame(centre, target);
        }

        /// <summary>For the saved trip: the centre's name, empty for the Sun.</summary>
        public string CentreName => PlanetSystem ? Centre.bodyName : "";

        /// <summary>The vessel's position and velocity relative to the centre at <paramref name="ut"/>.</summary>
        public StateVector ShipState(Vessel vessel, double ut) => KspShip.StateRelativeTo(vessel, ut, Centre);

        /// <summary>The lowest safe radius over a body: the top of its atmosphere, or its highest terrain, plus 1 km.</summary>
        public static double SafeRadius(CelestialBody body) =>
            Math.Max(body.Radius + (body.atmosphere ? body.atmosphereDepth : 0),
                body.pqsController != null ? body.pqsController.radiusMax : body.Radius) + 1000;

        /// <summary>A body as the core sees it: the centre standing still at the origin, anything else on its own orbit.</summary>
        public TargetPlanet Body(CelestialBody body)
        {
            if (body == Centre)
            {
                return new TargetPlanet(body.bodyName, new FixedEphemeris(Vec3.Zero), body.gravParameter, body.Radius)
                {
                    SphereOfInfluence = body.sphereOfInfluence,
                    IsCentralBody = true,
                };
            }

            return Planner.AsPlanet(body);
        }

        /// <summary>The body the ship is leaving (inside its sphere of influence), or null.</summary>
        public TargetPlanet Departure(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            if (body == Planetarium.fetch.Sun || body == Target) return null;
            TargetPlanet departure = Body(body);
            departure.SafeRadius = PlanetSystem
                ? SafeRadius(body) + 10_000
                : body.Radius + (body.atmosphere ? body.atmosphereDepth : 0) + 10_000;
            return departure;
        }

        /// <summary>Moon trips: the planet's other moons, to keep clear of.</summary>
        public List<Obstacle> Obstacles(Vessel vessel)
        {
            var list = new List<Obstacle>();
            if (!PlanetSystem) return list;
            foreach (CelestialBody m in FlightGlobals.Bodies.Where(b => b.referenceBody == Centre && b != Centre && b != Target && b != vessel.mainBody))
            {
                list.Add(new Obstacle
                {
                    Name = Planner.BodyName(m), Ephemeris = new KspOrbitEphemeris(m.orbit), SafeRadius = SafeRadius(m),
                    GravParameter = m.gravParameter, SphereOfInfluence = m.sphereOfInfluence,
                });
            }

            return list;
        }

        /// <summary>The detour distance (m): 1.2 times the planet's safe radius, and where its pull is at most 10 % of the thrust.</summary>
        public double DetourClearance(double acceleration) =>
            PlanetSystem ? Math.Max(1.2 * SafeRadius(Centre), Math.Sqrt(10 * Centre.gravParameter / acceleration)) : 0;

        /// <summary>The trip request from where the vessel is now.</summary>
        /// <param name="starting">True when the trip is about to start (not under way): a moon trip leaving the planet waits for its window.</param>
        public TripRequest Request(Vessel vessel, double ut, double acceleration, double parkingAltitude, bool starting)
        {
            StateVector ship = ShipState(vessel, ut);
            TargetPlanet departure = Departure(vessel);
            var request = new TripRequest
            {
                ShipPosition = ship.Position,
                ShipVelocity = ship.Velocity,
                StartTime = ut,
                Target = Body(Target),
                Departure = departure,
                Acceleration = acceleration,
                ParkingAltitude = parkingAltitude,
                SunGravParameter = Target == Centre ? 0 : Centre.gravParameter,
            };
            if (PlanetSystem)
            {
                request.PlanetSystemTrip = true;
                request.SunSafetyDistance = SafeRadius(Centre);
                request.DetourClearance = DetourClearance(acceleration);
                request.Obstacles = Obstacles(vessel);
                request.WaitForDepartureWindow = starting && departure != null;
            }

            return request;
        }

        /// <summary>Copy this frame's settings into a guidance configuration.</summary>
        public void Configure(GuidanceConfig config, Vessel vessel, bool starting)
        {
            config.Target = Body(Target);
            config.Departure = Departure(vessel);
            config.SunGravParameter = Target == Centre ? 0 : Centre.gravParameter;
            if (!PlanetSystem) return;
            config.PlanetSystemTrip = true;
            config.SunSafetyDistance = SafeRadius(Centre);
            config.DetourClearance = DetourClearance(config.Acceleration);
            config.Obstacles = Obstacles(vessel);
            config.WaitForDepartureWindow = starting && config.Departure != null;
        }
    }
}
