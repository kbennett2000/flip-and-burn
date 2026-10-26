using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlipAndBurn.Core
{
    /// <summary>One propellant an engine burns: its share of the mix and how heavy a unit is.</summary>
    public sealed class PropellantUse
    {
        public string Resource { get; }
        public double Ratio { get; }      // units of this resource per unit of the engine's mix
        public double Density { get; }    // t per unit (0 for massless resources such as ElectricCharge)

        public PropellantUse(string resource, double ratio, double density)
        {
            Resource = resource;
            Ratio = ratio;
            Density = density;
        }
    }

    /// <summary>What one active engine says about itself, read from the game (see the plugin's engine reader).</summary>
    public sealed class EngineReading
    {
        public string Name { get; set; } = "engine";
        /// <summary>Vacuum thrust at 100 %, after the thrust limiter (N).</summary>
        public double Thrust { get; set; }
        /// <summary>Vacuum Isp at full thrust (s).</summary>
        public double VacuumIsp { get; set; }
        /// <summary>Isp = VacuumIsp * throttle^k (KSP Interstellar fusion drives); 0 for everything else.</summary>
        public double IspThrottleExponent { get; set; }
        public List<PropellantUse> Propellants { get; set; } = new List<PropellantUse>();
        /// <summary>
        /// False when Flip and Burn doesn't know the engine's module: its figures are KSP's own (what the engine module
        /// reports, which is what KSP's delta-v readout uses), unchecked, so the results are estimates.
        /// </summary>
        public bool Understood { get; set; } = true;
    }

    /// <summary>The ship's performance from its active engines, and anything the player should be told about it.</summary>
    public sealed class ShipEngineReport
    {
        public ShipPerformance Performance { get; internal set; }
        /// <summary>Plain sentences for the window.</summary>
        public List<string> Notes { get; } = new List<string>();
        /// <summary>Some figures are KSP's own, unchecked (an engine Flip and Burn doesn't know).</summary>
        public bool Estimated { get; internal set; }
        /// <summary>No engine can push the ship: <see cref="Notes"/> says why.</summary>
        public bool NoThrust { get; internal set; }

        /// <summary>Electric engines: ElectricCharge per second at full thrust, the supply, and the share of full thrust it allows.</summary>
        public double ElectricDemand { get; internal set; }
        public double PowerSupply { get; internal set; }
        public double PowerLimit { get; internal set; } = 1;
    }

    /// <summary>
    /// Combines the active engines into the ship's thrust and delta-v (ADR 0002, "Engines and stages").
    /// </summary>
    /// <remarks>
    /// All active engines burn together, each drawing its own propellant mix from the ship's fuel. When one propellant
    /// runs out, the engines that need it stop and the others carry on: each stretch is a <see cref="BurnPhase"/>.
    ///
    /// Each engine's mass flow at full thrust is F / (Isp g0); its mix of propellants is drawn in proportion to their
    /// ratios, so units of resource r per second = mass flow / (sum of ratio x density over the mix) x ratio_r.
    ///
    /// Electric engines burn ElectricCharge, which weighs nothing and comes from the power supply rather than tanks.
    /// If the supply can't keep up with them at full thrust, they are limited to supply / demand of full thrust for the
    /// whole trip (batteries only last minutes against a burn of days).
    ///
    /// Engines in an air-breathing mode (IntakeAir or IntakeAtm) can't run in space and are left out.
    /// </remarks>
    public static class ShipEngines
    {
        public const string ElectricCharge = "ElectricCharge";
        private static readonly string[] AirResources = { "IntakeAir", "IntakeAtm" };
        private const int MaxPhases = 32;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="engines">The active engines.</param>
        /// <param name="resources">Units of each resource the engines can reach.</param>
        /// <param name="shipMass">The whole ship now (kg).</param>
        /// <param name="powerSupply">ElectricCharge the ship makes per second (solar panels, generators).</param>
        /// <param name="batteryCharge">ElectricCharge stored now.</param>
        public static ShipEngineReport Combine(IList<EngineReading> engines, IDictionary<string, double> resources, double shipMass,
            double powerSupply = 0, double batteryCharge = 0)
        {
            var report = new ShipEngineReport();
            var usable = new List<Burner>();

            foreach (EngineReading e in engines ?? new List<EngineReading>())
            {
                if (e.Propellants.Any(p => AirResources.Contains(p.Resource)))
                {
                    report.Notes.Add($"{e.Name} is in an air-breathing mode, which doesn't work in space: switch it to its rocket " +
                                     "(closed cycle) mode. It isn't counted.");
                    continue;
                }

                if (!(e.Thrust > 0) || !(e.VacuumIsp > 0)) continue;
                double mixDensity = e.Propellants.Sum(p => p.Ratio * p.Density) * 1000;   // kg per unit of mix
                if (!(mixDensity > 0))
                {
                    report.Notes.Add($"{e.Name} burns nothing with mass, so Flip and Burn can't work out its delta-v. It isn't counted.");
                    continue;
                }

                if (!e.Understood) report.Estimated = true;
                double massFlow = e.Thrust / (e.VacuumIsp * ShipPerformance.StandardGravity);   // kg/s at full thrust
                double mixPerSecond = massFlow / mixDensity;
                var b = new Burner(e, massFlow);
                foreach (PropellantUse p in e.Propellants)
                {
                    if (p.Resource == ElectricCharge) b.ElectricPerSecond += mixPerSecond * p.Ratio;
                    else if (p.Density > 0) b.Rates[p.Resource] = (b.Rates.TryGetValue(p.Resource, out double r) ? r : 0) + mixPerSecond * p.Ratio;
                }

                usable.Add(b);
            }

            // Electric engines: the power supply limits their sustained thrust.
            double demand = usable.Sum(b => b.ElectricPerSecond);
            report.ElectricDemand = demand;
            report.PowerSupply = powerSupply;
            if (demand > 0)
            {
                double limit = Math.Max(0, Math.Min(1, powerSupply / demand));
                report.PowerLimit = limit;
                if (limit < 1)
                {
                    foreach (Burner b in usable.Where(b => b.ElectricPerSecond > 0)) b.Scale(limit);
                    string battery = batteryCharge > 0 && demand > powerSupply
                        ? $" The batteries ({batteryCharge.ToString("N0", Inv)} EC) would run them at full thrust for only about " +
                          $"{(batteryCharge / (demand - powerSupply)).ToString("N0", Inv)} s."
                        : "";
                    report.Notes.Add(limit > 0
                        ? $"The electric engines need {demand.ToString("N1", Inv)} EC/s at full thrust, but the ship's power supply makes " +
                          $"{powerSupply.ToString("N1", Inv)} EC/s, so they can hold only {(limit * 100).ToString("N0", Inv)}% of full thrust. " +
                          "The numbers here use that." + battery
                        : "The electric engines have no power supply (no working solar panels or generators), so they can't hold any thrust for a trip." + battery);
                }
            }

            usable.RemoveAll(b => !(b.Thrust > 0));
            if (report.Estimated)
            {
                string names = string.Join(", ", usable.Where(b => !b.Engine.Understood).Select(b => b.Engine.Name).Distinct());
                report.Notes.Add($"Flip and Burn doesn't know how {names} works, so it uses KSP's own figures for it. " +
                                 "These numbers are estimates.");
            }

            if (usable.Count == 0)
            {
                report.NoThrust = true;
                report.Performance = new ShipPerformance(0, 0, 0, shipMass, shipMass);
                return report;
            }

            report.Performance = new ShipPerformance(Phases(usable, resources ?? new Dictionary<string, double>(), shipMass));
            return report;
        }

        // Burn every engine at full thrust until the first propellant runs out; drop the engines that need it; repeat.
        private static List<BurnPhase> Phases(List<Burner> burners, IDictionary<string, double> resources, double shipMass)
        {
            var left = new Dictionary<string, double>();
            foreach (Burner b in burners)
            foreach (string r in b.Rates.Keys)
                left[r] = resources.TryGetValue(r, out double amount) ? Math.Max(0, amount) : 0;

            var phases = new List<BurnPhase>();
            double mass = shipMass;
            var active = burners.Where(b => b.Rates.Keys.All(r => left[r] > 0)).ToList();
            if (active.Count == 0)
            {
                // No fuel at all: thrust still counts (for the acceleration readout), delta-v is zero.
                phases.Add(Phase(burners, mass, mass));
                return phases;
            }

            for (int i = 0; i < MaxPhases && active.Count > 0; i++)
            {
                var rates = new Dictionary<string, double>();
                foreach (Burner b in active)
                foreach (KeyValuePair<string, double> kv in b.Rates)
                    rates[kv.Key] = (rates.TryGetValue(kv.Key, out double r) ? r : 0) + kv.Value;

                double time = rates.Where(kv => kv.Value > 0).Min(kv => left[kv.Key] / kv.Value);
                double used = active.Sum(b => b.MassFlow) * time;
                phases.Add(Phase(active, mass, mass - used));
                mass -= used;
                foreach (KeyValuePair<string, double> kv in rates)
                {
                    left[kv.Key] -= kv.Value * time;
                    if (left[kv.Key] <= 1e-9 * Math.Max(1, resources.TryGetValue(kv.Key, out double a) ? a : 0)) left[kv.Key] = 0;
                }

                active = active.Where(b => b.Rates.Keys.All(r => left[r] > 0)).ToList();
            }

            return phases;
        }

        private static BurnPhase Phase(List<Burner> active, double startMass, double endMass)
        {
            double thrust = active.Sum(b => b.Thrust);
            double flow = active.Sum(b => b.MassFlow);
            double k = active.Sum(b => b.Thrust * b.Engine.IspThrottleExponent) / thrust;
            return new BurnPhase(thrust, thrust / (flow * ShipPerformance.StandardGravity), k, startMass, endMass);
        }

        private sealed class Burner
        {
            public readonly EngineReading Engine;
            public double Thrust, MassFlow, ElectricPerSecond;
            public readonly Dictionary<string, double> Rates = new Dictionary<string, double>();   // units/s at full thrust

            public Burner(EngineReading engine, double massFlow)
            {
                Engine = engine;
                Thrust = engine.Thrust;
                MassFlow = massFlow;
            }

            public void Scale(double factor)
            {
                Thrust *= factor;
                MassFlow *= factor;
                ElectricPerSecond *= factor;
                foreach (string r in Rates.Keys.ToList()) Rates[r] *= factor;
            }
        }
    }
}
