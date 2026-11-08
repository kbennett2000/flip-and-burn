using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// What a ship needs to fly its trip while the player is away (ADR 0006), read from the live ship while it is flown:
    /// the ship can't be read once it is unloaded. Saved with the trip record.
    /// </summary>
    public sealed class AwayShip
    {
        /// <summary>The value whose presence marks a record that carries these figures.</summary>
        public const string Key = "awayFullThrust";

        /// <summary>The ship's turn rate (rad/s), as the live autopilot estimated it.</summary>
        public double TurnRate { get; set; }
        /// <summary>Full thrust of the active engines (N).</summary>
        public double FullThrust { get; set; }
        /// <summary>Exhaust velocity at full thrust (m/s); infinity when no propellant is used (powered electric engines).</summary>
        public double ExhaustVelocity { get; set; } = double.PositiveInfinity;
        /// <summary>Isp = Isp at full thrust * throttle^k (KSP Interstellar fusion drives); 0 otherwise.</summary>
        public double IspThrottleExponent { get; set; }
        /// <summary>The ship's mass with all the propellant its engines reach burned (kg).</summary>
        public double DryMass { get; set; }
        /// <summary>The propellants burned, by resource name, with each one's share of the propellant mass (adding to 1).</summary>
        public List<KeyValuePair<string, double>> Propellants { get; set; } = new List<KeyValuePair<string, double>>();
        /// <summary>Electric engines: electric charge a second per newton of thrust, what the ship makes a second, and the floor.</summary>
        public double ChargePerNewton { get; set; }
        public double ChargeMade { get; set; }
        public double ChargeFloor { get; set; }
        /// <summary>The way the ship pointed (relative to the trip's centre, unit length).</summary>
        public Vec3 Facing { get; set; }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Each propellant's share of the propellant mass, from the engines' mixes (massless ones left out).</summary>
        public static List<KeyValuePair<string, double>> MassShares(IEnumerable<EngineReading> engines)
        {
            var mass = new Dictionary<string, double>();
            foreach (EngineReading e in engines ?? Enumerable.Empty<EngineReading>())
            {
                // Each engine's mix in mass, weighted by its share of the thrust (mass flow is thrust / exhaust velocity).
                double mixMass = e.Propellants.Where(p => p.Density > 0).Sum(p => p.Ratio * p.Density);
                double ve = e.VacuumIsp * TripCalculator.OneG;
                if (!(mixMass > 0) || !(ve > 0) || !(e.Thrust > 0)) continue;
                double flow = e.Thrust / ve;
                foreach (PropellantUse p in e.Propellants.Where(p => p.Density > 0))
                    mass[p.Resource] = (mass.TryGetValue(p.Resource, out double m) ? m : 0) + flow * p.Ratio * p.Density / mixMass;
            }

            double total = mass.Values.Sum();
            return total > 0 ? mass.Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value / total)).ToList()
                : new List<KeyValuePair<string, double>>();
        }

        public List<KeyValuePair<string, string>> ToValues()
        {
            var v = new List<KeyValuePair<string, string>>();
            void Add(string name, double value) => v.Add(new KeyValuePair<string, string>(name, value.ToString("R", Inv)));
            Add(Key, FullThrust);
            Add("awayTurnRate", TurnRate);
            Add("awayExhaustVelocity", double.IsPositiveInfinity(ExhaustVelocity) ? 0 : ExhaustVelocity);
            Add("awayIspThrottleExponent", IspThrottleExponent);
            Add("awayDryMass", DryMass);
            v.Add(new KeyValuePair<string, string>("awayPropellants",
                string.Join(";", Propellants.Select(p => p.Key + ":" + p.Value.ToString("R", Inv)))));
            Add("awayChargePerNewton", ChargePerNewton);
            Add("awayChargeMade", ChargeMade);
            Add("awayChargeFloor", ChargeFloor);
            v.Add(new KeyValuePair<string, string>("awayFacing", TripRecord.VecText(Facing)));
            return v;
        }

        /// <summary>Read the figures back, or null if any is missing or unreadable.</summary>
        public static AwayShip FromValues(Dictionary<string, string> map)
        {
            var a = new AwayShip();
            bool ok = Num(Key, x => a.FullThrust = x) & Num("awayTurnRate", x => a.TurnRate = x) &
                      Num("awayExhaustVelocity", x => a.ExhaustVelocity = x > 0 ? x : double.PositiveInfinity) &
                      Num("awayIspThrottleExponent", x => a.IspThrottleExponent = x) & Num("awayDryMass", x => a.DryMass = x) &
                      Num("awayChargePerNewton", x => a.ChargePerNewton = x) & Num("awayChargeMade", x => a.ChargeMade = x) &
                      Num("awayChargeFloor", x => a.ChargeFloor = x) & TripRecord.TryVec(map, "awayFacing", out Vec3 facing);
            a.Facing = facing;
            if (!map.TryGetValue("awayPropellants", out string list)) return null;
            foreach (string item in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = item.LastIndexOf(':');
                if (colon <= 0 || !double.TryParse(item.Substring(colon + 1), NumberStyles.Float, Inv, out double share) || !(share >= 0)) return null;
                a.Propellants.Add(new KeyValuePair<string, double>(item.Substring(0, colon), share));
            }

            return ok && a.FullThrust > 0 && a.TurnRate > 0 ? a : null;

            bool Num(string name, Action<double> set)
            {
                if (!map.TryGetValue(name, out string s) || !double.TryParse(s, NumberStyles.Float, Inv, out double x) || double.IsNaN(x) || double.IsInfinity(x))
                    return false;
                set(x);
                return true;
            }
        }
    }
}
