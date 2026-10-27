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

    /// <summary>What the reader found: the engines' combined performance, notes for the window, and the stages below.</summary>
    public sealed class ShipReading
    {
        public ShipEngineReport Report;
        /// <summary>Stages still to fire below the current one (with engines or decouplers in them).</summary>
        public int StagesBelow;
        public List<EngineReading> Engines = new List<EngineReading>();
    }

    /// <summary>
    /// Reads every active engine's thrust, Isp and propellants, the fuel they can reach and the ship's power supply,
    /// and combines them (<see cref="ShipEngines"/>; ADR 0002, "Engines and stages").
    /// </summary>
    /// <remarks>
    /// "Active" means ignited now: a trip burns on the current stage. Each engine is read from its own engine module,
    /// which is what KSP's delta-v readout reads too, so for stock engines (multi-mode, nuclear and electric included)
    /// and for other mods' engines built on the stock modules the numbers are KSP's. Two exceptions, both proven:
    /// - KSP Interstellar Extended's fusion drives (DaedalusEngineController, the Kerbstein drive): stock leaves them out
    ///   (includeinDVCalcs = false) and doesn't know their Isp falls at part throttle, Isp = Isp0 * throttle^0.5.
    /// - Electric engines: stock assumes full thrust; here the power supply limits it.
    /// An engine whose module Flip and Burn doesn't know (a subclass of the stock module, or a part with another mod's
    /// engine controller) is read the same way, but flagged: the window says the figures are estimates.
    /// includeinDVCalcs isn't used to decide: on Kris's install a mod turns it off even for stock engines.
    /// </remarks>
    internal static class ShipPerformanceReader
    {
        // Modules that sit on engine parts without changing what the engine module reports.
        private static readonly HashSet<string> HarmlessModules = new HashSet<string>
        {
            "TCAEngineInfo",          // Throttle Controlled Avionics: reads engines only
            "DaedalusEngineController", // handled below (the Kerbstein drive)
        };

        public static ShipReading Read(Vessel vessel, out string problem)
        {
            problem = null;
            var reading = new ShipReading { StagesBelow = StagesBelow(vessel) };
            double shipMass = vessel.totalMass * 1000.0;   // t -> kg
            List<ModuleEngines> engines = vessel.FindPartModulesImplementing<ModuleEngines>()
                .Where(e => e.EngineIgnited && e.isEnabled && !e.flameout)
                .ToList();
            if (engines.Count == 0)
            {
                problem = "No engine is active. Stage or activate your main engine so Flip and Burn can read its thrust.";
                reading.Report = ShipEngines.Combine(new List<EngineReading>(), null, shipMass);
                return reading;
            }

            var resources = new Dictionary<string, double>();
            bool electric = false;
            foreach (ModuleEngines e in engines)
            {
                var r = new EngineReading
                {
                    Name = EngineName(e),
                    Thrust = e.maxThrust * (e.thrustPercentage / 100.0) * 1000.0,   // kN -> N
                    VacuumIsp = e.atmosphereCurve.Evaluate(0f),
                    IspThrottleExponent = IspThrottleExponent(e.part),
                    Understood = Understood(e),
                };
                // A KSP Interstellar fusion drive rewrites the engine while it fires: the Isp curve becomes the Isp at the
                // throttle it is running at, and the thrust is cut by any power shortfall. Read the drive's full-throttle
                // figures from its controller instead, so the delta-v shown is the same whether the drive is firing or not.
                if (DaedalusFullThrottle(e.part, out double fullThrustKn, out double fullIsp))
                {
                    r.Thrust = fullThrustKn * (e.thrustPercentage / 100.0) * 1000.0;
                    r.VacuumIsp = fullIsp;
                }
                foreach (Propellant p in e.propellants)
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(p.name);
                    if (def == null || p.ratio <= 0) continue;
                    // A propellant KSP leaves out of the Isp sum still gets drawn, but only a massless one (power) fits
                    // the mix arithmetic; heavier ones aren't part of the engine's mass flow.
                    if (p.ignoreForIsp && def.density > 0) continue;
                    r.Propellants.Add(new PropellantUse(p.name, p.ratio, def.density));
                    if (p.name == ShipEngines.ElectricCharge) { electric = true; continue; }
                    if (def.density <= 0) continue;
                    e.part.GetConnectedResourceTotals(def.id, p.GetFlowMode(), out double amount, out double _, true);
                    // Engines sharing tanks see the same total; take the largest any engine can reach.
                    resources[p.name] = Math.Max(resources.TryGetValue(p.name, out double seen) ? seen : 0, amount);
                }

                reading.Engines.Add(r);
            }

            double supply = 0, battery = 0;
            if (electric) PowerSupply(vessel, out supply, out battery);
            reading.Report = ShipEngines.Combine(reading.Engines, resources, shipMass, supply, battery);
            if (reading.Report.NoThrust && reading.Report.Notes.Count == 0)
                problem = "The active engines report no thrust.";
            return reading;
        }

        private static string EngineName(ModuleEngines e)
        {
            string title = e.part.partInfo?.title ?? e.part.name;
            bool multiMode = e.part.Modules.OfType<MultiModeEngine>().Any();
            return multiMode && !string.IsNullOrEmpty(e.engineID) ? $"{title} ({e.engineID} mode)" : title;
        }

        /// <summary>
        /// True when the engine module is the stock one and nothing else on the part rewrites it (other than the
        /// Kerbstein drive's controller, which is handled).
        /// </summary>
        private static bool Understood(ModuleEngines e)
        {
            Type t = e.GetType();
            if (t != typeof(ModuleEngines) && t != typeof(ModuleEnginesFX)) return false;
            // (includeinDVCalcs is no guide: on Kris's install a mod sets it false even on stock engines.)
            foreach (PartModule m in e.part.Modules)
            {
                Type mt = m.GetType();
                if (mt.Assembly == typeof(ModuleEngines).Assembly || HarmlessModules.Contains(mt.Name) || IsDaedalusType(mt)) continue;
                // Another mod's module with an engine in its name on an engine part: it may be driving the engine.
                string n = mt.Name;
                if (n.IndexOf("Engine", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Thrust", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Nozzle", StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
        }

        private static bool IsDaedalusType(Type t)
        {
            for (; t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "DaedalusEngineController") return true;
            return false;
        }

        /// <summary>
        /// A KSPIE fusion drive's thrust (kN) and vacuum Isp (s) at full throttle and full power, with its relativity factor,
        /// from the controller (its MaximumThrust and EngineIsp). False for any other engine, or if they can't be read.
        /// </summary>
        private static bool DaedalusFullThrottle(Part part, out double thrustKn, out double isp)
        {
            thrustKn = isp = double.NaN;
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (PartModule m in part.Modules)
            {
                if (!IsDaedalusType(m.GetType())) continue;
                try
                {
                    double thrust = Number(m, "MaximumThrust"), engineIsp = Number(m, "EngineIsp"), dilation = Number(m, "timeDilation");
                    if (!(dilation > 0 && dilation <= 1)) dilation = 1;
                    if (!(thrust > 0) || !(engineIsp > 0)) return false;
                    thrustKn = dilation * dilation * thrust;
                    isp = dilation * engineIsp;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;

            double Number(PartModule m, string name)
            {
                for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                {
                    PropertyInfo p = t.GetProperty(name, all | BindingFlags.DeclaredOnly);
                    if (p != null) return Convert.ToDouble(p.GetValue(m, null));
                    FieldInfo f = t.GetField(name, all | BindingFlags.DeclaredOnly);
                    if (f != null) return Convert.ToDouble(f.GetValue(m));
                }

                return double.NaN;
            }
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

        /// <summary>
        /// ElectricCharge made per second right now by stock solar panels (and modules built on them), generators such as
        /// RTGs, and running converters such as fuel cells; and the charge stored. Other mods' power plants aren't counted.
        /// </summary>
        private static void PowerSupply(Vessel vessel, out double supply, out double battery)
        {
            supply = 0;
            battery = 0;
            foreach (Part part in vessel.parts)
            {
                PartResource ec = part.Resources.Get(ShipEngines.ElectricCharge);
                if (ec != null) battery += ec.amount;
                foreach (PartModule m in part.Modules)
                {
                    try
                    {
                        switch (m)
                        {
                            case ModuleDeployableSolarPanel panel when panel.resourceName == ShipEngines.ElectricCharge:
                                supply += Math.Max(0, panel.flowRate);
                                break;
                            case ModuleGenerator gen when gen.generatorIsActive || gen.isAlwaysActive:
                                foreach (ModuleResource r in gen.resHandler.outputResources)
                                    if (r.name == ShipEngines.ElectricCharge) supply += r.rate;
                                break;
                            case ModuleResourceConverter conv when conv.IsActivated:
                                foreach (ResourceRatio r in conv.outputList)
                                    if (r.ResourceName == ShipEngines.ElectricCharge) supply += r.Ratio;
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        // A module in an odd state: leave it out.
                    }
                }
            }
        }

        /// <summary>Stages below the current one that still have engines or decouplers in them.</summary>
        private static int StagesBelow(Vessel vessel)
        {
            int current = vessel.currentStage;
            return vessel.parts
                .Where(p => p.inverseStage >= 0 && p.inverseStage < current &&
                            (p.Modules.OfType<ModuleEngines>().Any() || p.Modules.OfType<ModuleDecouple>().Any() ||
                             p.Modules.OfType<ModuleAnchoredDecoupler>().Any()))
                .Select(p => p.inverseStage)
                .Distinct()
                .Count();
        }
    }
}
