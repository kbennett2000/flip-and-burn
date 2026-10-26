using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlipAndBurn.Core;

namespace FlipAndBurn
{
    /// <summary>Feeds KSP's own orbits into the trip calculator.</summary>
    /// <remarks>
    /// All vectors come from KSP's Orbit methods, which share one frame. The calculator only adds, subtracts and measures
    /// vectors, so it doesn't matter that KSP's frame is left-handed with Y and Z swapped.
    /// </remarks>
    internal sealed class KspOrbitEphemeris : IEphemeris
    {
        private readonly Orbit orbit;

        public KspOrbitEphemeris(Orbit orbit)
        {
            this.orbit = orbit;
        }

        /// <summary>Position and velocity relative to the orbit's reference body (the Sun, for a planet).</summary>
        public StateVector StateAt(double ut) => new StateVector(ToVec(orbit.getRelativePositionAtUT(ut)), ToVec(orbit.getOrbitalVelocityAtUT(ut)));

        public static Vec3 ToVec(Vector3d v) => new Vec3(v.x, v.y, v.z);
    }

    internal static class KspShip
    {
        /// <summary>The vessel's position and velocity relative to the Sun at <paramref name="ut"/>.</summary>
        public static StateVector HeliocentricState(Vessel vessel, double ut)
        {
            Vec3 pos = KspOrbitEphemeris.ToVec(vessel.orbit.getRelativePositionAtUT(ut));
            Vec3 vel = KspOrbitEphemeris.ToVec(vessel.orbit.getOrbitalVelocityAtUT(ut));
            CelestialBody sun = Planetarium.fetch.Sun;
            for (CelestialBody body = vessel.mainBody; body != null && body != sun; body = body.referenceBody)
            {
                pos += KspOrbitEphemeris.ToVec(body.orbit.getRelativePositionAtUT(ut));
                vel += KspOrbitEphemeris.ToVec(body.orbit.getOrbitalVelocityAtUT(ut));
            }

            return new StateVector(pos, vel);
        }
    }

    /// <summary>
    /// Reads thrust, Isp and fuel straight from the ship's active engines, including KSP Interstellar Extended's fusion
    /// drives.
    /// </summary>
    /// <remarks>
    /// Why not stock's or MechJeb's delta-v: KSPIE's DaedalusEngineController (the Kerbstein drive's module) rewrites the
    /// engine's maxThrust and Isp every physics tick from its upgrade level and TweakScale size, marks the engine
    /// includeinDVCalcs = false so stock leaves it out, and lowers Isp at part throttle: Isp = Isp0 * throttle^0.5
    /// (its ispThrottleExponent). Holding a set acceleration means throttling down, so the usable delta-v depends on the
    /// acceleration chosen. Neither stock nor MechJeb knows that rule. See the PR for the numbers.
    ///
    /// Assumes all active engines burn together from one shared fuel supply (the usual torch-ship layout). Staging is
    /// not modelled.
    /// </remarks>
    internal static class ShipPerformanceReader
    {
        public static ShipPerformance Read(Vessel vessel, out string problem)
        {
            problem = null;
            List<ModuleEngines> engines = vessel.FindPartModulesImplementing<ModuleEngines>()
                .Where(e => e.EngineIgnited && e.isEnabled && !e.flameout)
                .ToList();
            if (engines.Count == 0)
            {
                problem = "No engine is active. Stage or activate your main engine so Flip and Burn can read its thrust.";
                return new ShipPerformance(0, 0, 0, vessel.totalMass * 1000, vessel.totalMass * 1000);
            }

            double thrust = 0, thrustOverIsp = 0, exponentWeighted = 0;
            foreach (ModuleEngines e in engines)
            {
                double f = e.maxThrust * (e.thrustPercentage / 100.0) * 1000.0; // kN -> N
                double isp = e.atmosphereCurve.Evaluate(0f);                     // vacuum
                if (f <= 0 || isp <= 0) continue;
                thrust += f;
                thrustOverIsp += f / isp;
                exponentWeighted += f * IspThrottleExponent(e.part);
            }

            if (thrust <= 0)
            {
                problem = "The active engines report no thrust.";
                return new ShipPerformance(0, 0, 0, vessel.totalMass * 1000, vessel.totalMass * 1000);
            }

            double startMass = vessel.totalMass * 1000.0;                       // t -> kg
            double propellant = UsablePropellantMass(engines) * 1000.0;
            double endMass = Math.Max(1e-6, startMass - propellant);
            return new ShipPerformance(thrust, thrust / thrustOverIsp, exponentWeighted / thrust, startMass, endMass);
        }

        /// <summary>KSPIE fusion drives lower Isp at part throttle. Returns 0 for any other engine.</summary>
        private static double IspThrottleExponent(Part part)
        {
            foreach (PartModule m in part.Modules)
            {
                for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                {
                    if (t.Name != "DaedalusEngineController") continue;
                    FieldInfo field = t.GetField("ispThrottleExponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(double)) return (double)field.GetValue(m);
                    return 0.5; // KSPIE's default
                }
            }

            return 0;
        }

        /// <summary>Tonnes of propellant the active engines can draw, limited by whichever propellant runs out first.</summary>
        private static double UsablePropellantMass(List<ModuleEngines> engines)
        {
            // Use the first engine's propellant mix; with several engines on one fuel supply the mix is the same.
            ModuleEngines engine = engines[0];
            double limit = double.MaxValue, massPerUnitOfMix = 0;
            foreach (Propellant p in engine.propellants)
            {
                if (p.ignoreForIsp || p.ratio <= 0) continue;
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(p.name);
                if (def == null || def.density <= 0) continue;
                engine.part.GetConnectedResourceTotals(def.id, p.GetFlowMode(), out double amount, out double _, true);
                limit = Math.Min(limit, amount / p.ratio);
                massPerUnitOfMix += p.ratio * def.density;
            }

            return limit == double.MaxValue ? 0 : limit * massPerUnitOfMix;
        }
    }
}
