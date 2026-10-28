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
        /// <summary>The ship's electric power (read when an engine burns electric charge).</summary>
        public ShipPower Power = new ShipPower();
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

            ShipPower power = electric ? ReadPower(vessel) : new ShipPower();
            reading.Power = power;
            reading.Report = ShipEngines.Combine(reading.Engines, resources, shipMass, power: power);
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
        /// The ship's electric power (ShipPower): charge stored and the batteries' capacity; charge made a second by
        /// generators, RTGs and reactors (stock ModuleGenerator, which Kris's Jibar reactors use, and KSP Interstellar's own
        /// generators); solar panels, rated at Kerbin's distance from the Sun so the planner can scale them; and fuel cells
        /// (running converters that burn fuel), with how long their fuel lasts.
        /// </summary>
        public static ShipPower ReadPower(Vessel vessel)
        {
            var power = new ShipPower();
            double home = FlightGlobals.GetHomeBody()?.orbit?.semiMajorAxis ?? double.NaN;
            power.SolarDistance = home;
            double fuelCellEnergy = 0;
            foreach (Part part in vessel.parts)
            {
                PartResource ec = part.Resources.Get(ShipEngines.ElectricCharge);
                if (ec != null)
                {
                    power.Stored += ec.amount;
                    power.Capacity += ec.maxAmount;
                }

                foreach (PartModule m in part.Modules)
                {
                    try
                    {
                        switch (m)
                        {
                            case ModuleDeployableSolarPanel panel when panel.resourceName == ShipEngines.ElectricCharge:
                                // Rated output in full sunlight at Kerbin's distance (KSP: rate x sun angle x efficiency x flux / flux at home).
                                double rated = panel.chargeRate * Math.Max(0, panel.efficiencyMult);
                                if (panel.deployState == ModuleDeployablePart.DeployState.EXTENDED || !panel.useAnimation)
                                    power.Solar += rated;
                                else if (panel.deployState == ModuleDeployablePart.DeployState.RETRACTED && rated > 0)
                                    power.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.SolarPanel, Rate = rated });
                                break;
                            case ModuleGenerator gen:
                            {
                                double rate = gen.resHandler.outputResources.Where(r => r.name == ShipEngines.ElectricCharge).Sum(r => r.rate);
                                if (!(rate > 0)) break;
                                double lasts = FuelSeconds(part, gen.resHandler.inputResources.Select(r => (r.name, r.rate)));
                                if (gen.generatorIsActive || gen.isAlwaysActive)
                                {
                                    if (double.IsPositiveInfinity(lasts)) power.Steady += rate;
                                    else fuelCellEnergy += rate * lasts;
                                }
                                else
                                {
                                    power.SwitchedOff.Add(new PowerSource
                                    {
                                        Kind = part.partInfo.title.IndexOf("reactor", StringComparison.OrdinalIgnoreCase) >= 0
                                            ? PowerSourceKind.Reactor : PowerSourceKind.Generator,
                                        Rate = rate, Seconds = lasts,
                                    });
                                }

                                break;
                            }
                            case BaseConverter conv:
                            {
                                double make = conv.outputList.Where(r => r.ResourceName == ShipEngines.ElectricCharge).Sum(r => r.Ratio);
                                if (!(make > 0)) break;
                                // Burns fuel (has inputs with mass): a fuel cell, counted only while its fuel lasts.
                                double seconds = FuelSeconds(part, conv.inputList.Select(r => (r.ResourceName, r.Ratio)));
                                if (conv.IsActivated || conv.AlwaysActive)
                                {
                                    if (double.IsPositiveInfinity(seconds)) power.Steady += make;
                                    else fuelCellEnergy += make * seconds;
                                }
                                else
                                {
                                    power.SwitchedOff.Add(new PowerSource
                                    {
                                        Kind = double.IsPositiveInfinity(seconds) ? PowerSourceKind.Generator : PowerSourceKind.FuelCell,
                                        Rate = make, Seconds = seconds,
                                    });
                                }

                                break;
                            }
                            default:
                                power.Steady += KspiGeneratorOutput(m);
                                // A KSP Interstellar reactor that's off: its generator reads nothing, and what it would make
                                // depends on the reactor's heat, so it's named without a figure.
                                if (IsKspiReactor(m) && !KspiReactorOn(m))
                                    power.SwitchedOff.Add(new PowerSource { Kind = PowerSourceKind.Reactor });
                                break;
                        }
                    }
                    catch (Exception)
                    {
                        // A module in an odd state: leave it out.
                    }
                }
            }

            // Fuel cells as one: their energy, delivered at their combined rate.
            double fuelCellRate = vessel.parts.SelectMany(p => p.Modules.OfType<BaseConverter>())
                .Where(c => (c.IsActivated || c.AlwaysActive) && c.inputList.Any(i => (PartResourceLibrary.Instance.GetDefinition(i.ResourceName)?.density ?? 0) > 0))
                .Sum(c => c.outputList.Where(r => r.ResourceName == ShipEngines.ElectricCharge).Sum(r => r.Ratio))
                + vessel.parts.SelectMany(p => p.Modules.OfType<ModuleGenerator>())
                    .Where(g => (g.generatorIsActive || g.isAlwaysActive) &&
                                g.resHandler.inputResources.Any(i => (PartResourceLibrary.Instance.GetDefinition(i.name)?.density ?? 0) > 0))
                    .Sum(g => g.resHandler.outputResources.Where(r => r.name == ShipEngines.ElectricCharge).Sum(r => r.rate));
            power.FuelCellRate = fuelCellRate;
            power.FuelCellSeconds = fuelCellRate > 0 ? fuelCellEnergy / fuelCellRate : 0;
            return power;
        }

        // How long the fuel (inputs with mass) of a part that makes electric charge lasts (s); infinite if it burns nothing.
        private static double FuelSeconds(Part part, IEnumerable<(string name, double rate)> inputs)
        {
            double seconds = double.PositiveInfinity;
            foreach ((string name, double rate) in inputs)
            {
                PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(name);
                if (def == null || !(def.density > 0) || !(rate > 0)) continue;
                part.GetConnectedResourceTotals(def.id, out double amount, out double _, true);
                seconds = Math.Min(seconds, amount / rate);
            }

            return seconds;
        }

        // The reactor's own IsEnabled field, read by reflection: KSP's field list finds names without regard to case, and
        // every part module has an "isEnabled" of its own.
        private static bool KspiReactorOn(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
            {
                FieldInfo f = t.GetField("IsEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(m) is true;
            }

            return true;
        }

        private static bool IsKspiReactor(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "InterstellarReactor") return true;
            return false;
        }

        // A KSP Interstellar generator's electric output (charge a second): its maximumElectricPower, in megawatts (one
        // megawatt is 1,000 electric charge a second). 0 for any other module.
        private static double KspiGeneratorOutput(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
            {
                if (t.Name != "FNGenerator") continue;
                FieldInfo f = t.GetField("maximumElectricPower", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f == null) return 0;
                double mw = Convert.ToDouble(f.GetValue(m));
                return mw > 0 ? mw * 1000 : 0;
            }

            return 0;
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
