using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// The ship's electric power, as the planner needs it for engines that burn only electric charge (ADR 0002, cycle 13
    /// addendum). Charge is measured in KSP's units ("electric charge"); rates in charge per second.
    /// </summary>
    public sealed class ShipPower
    {
        /// <summary>Share of the battery capacity a trip never uses: the probe core and steering need it.</summary>
        public const double FloorShare = 0.10;

        /// <summary>Electric charge stored now.</summary>
        public double Stored { get; set; }
        /// <summary>Electric charge the batteries hold when full.</summary>
        public double Capacity { get; set; }
        /// <summary>Charge made per second whatever the distance from the Sun: generators, RTGs, reactors (incl. KSP Interstellar's).</summary>
        public double Steady { get; set; }
        /// <summary>Charge the solar panels make per second at <see cref="SolarDistance"/> from the Sun, in full sunlight.</summary>
        public double Solar { get; set; }
        /// <summary>Distance from the Sun (m) <see cref="Solar"/> applies at; NaN or 0 if unknown (no distance scaling).</summary>
        public double SolarDistance { get; set; } = double.NaN;
        /// <summary>Fuel cells: charge per second while they run, and for how long their fuel lasts (s).</summary>
        public double FuelCellRate { get; set; }
        public double FuelCellSeconds { get; set; }

        /// <summary>Charge the trip never touches.</summary>
        public double Floor => FloorShare * Math.Max(0, Capacity);

        /// <summary>Charge the fuel cells can still make before their fuel runs out.</summary>
        public double FuelCellEnergy => Math.Max(0, FuelCellRate) * Math.Max(0, FuelCellSeconds);

        /// <summary>Charge a trip may use: the batteries above the floor, plus what the fuel cells can still make.</summary>
        public double Usable => Math.Max(0, Stored - Floor) + FuelCellEnergy;

        /// <summary>The solar panels' output at <paramref name="sunDistance"/> (m): it falls with the square of the distance.</summary>
        public double SolarAt(double sunDistance)
        {
            if (!(Solar > 0)) return 0;
            if (!(sunDistance > 0) || !(SolarDistance > 0)) return Solar;
            double ratio = SolarDistance / sunDistance;
            return Solar * ratio * ratio;
        }

        /// <summary>Charge made per second that lasts the whole trip, with the panels at <paramref name="sunDistance"/> (fuel cells aren't steady).</summary>
        public double SupplyAt(double sunDistance) => Math.Max(0, Steady) + SolarAt(sunDistance);

        /// <summary>Power parts that are switched off (or folded, for solar panels), so they aren't counted.</summary>
        public List<PowerSource> SwitchedOff { get; private set; } = new List<PowerSource>();

        public ShipPower Copy()
        {
            var c = (ShipPower)MemberwiseClone();
            c.SwitchedOff = new List<PowerSource>(SwitchedOff);
            return c;
        }

        /// <summary>The ship's power with every switched-off part whose output is known turned on.</summary>
        public ShipPower WithSwitchedOffOn()
        {
            ShipPower c = Copy();
            double energy = c.FuelCellEnergy, rate = c.FuelCellRate;
            foreach (PowerSource s in SwitchedOff)
            {
                if (!(s.Rate > 0)) continue;
                if (s.Kind == PowerSourceKind.SolarPanel) c.Solar += s.Rate;
                else if (double.IsPositiveInfinity(s.Seconds)) c.Steady += s.Rate;
                else
                {
                    energy += s.Rate * Math.Max(0, s.Seconds);
                    rate += s.Rate;
                }
            }

            c.FuelCellRate = rate;
            c.FuelCellSeconds = rate > 0 ? energy / rate : 0;
            c.SwitchedOff = SwitchedOff.Where(s => !(s.Rate > 0)).ToList();
            return c;
        }
    }

    public enum PowerSourceKind { Reactor, Generator, FuelCell, SolarPanel }

    /// <summary>A power part that is switched off: what it would make, if known.</summary>
    public sealed class PowerSource
    {
        public PowerSourceKind Kind { get; set; }
        /// <summary>Charge per second when on (solar: in full sunlight at <see cref="ShipPower.SolarDistance"/>); NaN if not known.</summary>
        public double Rate { get; set; } = double.NaN;
        /// <summary>How long its fuel would last (s); infinite for a part that burns nothing.</summary>
        public double Seconds { get; set; } = double.PositiveInfinity;
    }

    /// <summary>What switching on the ship's switched-off power parts would change, in plain words (ADR 0002, cycle 13).</summary>
    public static class PowerAdvice
    {
        /// <summary>
        /// A note for the planner, or null when nothing is switched off or turning it on wouldn't change the plan at
        /// <paramref name="acceleration"/> (m/s^2) for a ship of <paramref name="mass"/> (kg).
        /// </summary>
        public static string SwitchedOffNote(ElectricDrive drive, double mass, double acceleration)
        {
            ShipPower power = drive?.Power;
            if (power == null || power.SwitchedOff.Count == 0 || !(acceleration > 0) || !(mass > 0)) return null;
            double now = drive.DeltaVAt(acceleration, mass);
            if (double.IsPositiveInfinity(now)) return null;

            double need = drive.ChargeFor(acceleration * mass);
            ElectricDrive on = drive.WithPower(power.WithSwitchedOffOn());
            double then = on.DeltaVAt(acceleration, mass);
            bool unknown = power.SwitchedOff.Any(s => !(s.Rate > 0));
            int count = power.SwitchedOff.Count;
            string what = Describe(power.SwitchedOff), them = count == 1 ? "it" : "them";
            string g = (acceleration / TripCalculator.OneG).ToString("0.00", CultureInfo.InvariantCulture) + " g";
            if (double.IsPositiveInfinity(then))
                return $"{what}. Turn {them} on and this trip needs no battery limit: the ship would make {Readout.ChargeRate(on.Supply)} " +
                       $"of electric charge, and the engines need {Readout.ChargeRate(need)} at {g}.";
            if (unknown)
                return $"{what}. Turn {them} on: if the ship then makes {Readout.ChargeRate(need)} of electric charge or more, this trip " +
                       "needs no battery limit.";
            if (then > now * 1.001)
                return $"{what}. Turn {them} on and the ship's delta-v at {g} goes from {Readout.Speed(now)} to {Readout.Speed(then)}.";
            return null;
        }

        // "Your reactor is switched off", "Your 2 generators are switched off and your solar panel is folded".
        private static string Describe(IEnumerable<PowerSource> off)
        {
            var parts = new List<string>();
            foreach (IGrouping<PowerSourceKind, PowerSource> k in off.GroupBy(s => s.Kind).OrderBy(k => k.Key))
            {
                int n = k.Count();
                string noun = k.Key switch
                {
                    PowerSourceKind.Reactor => "reactor",
                    PowerSourceKind.Generator => "generator",
                    PowerSourceKind.FuelCell => "fuel cell",
                    _ => "solar panel",
                };
                string state = k.Key == PowerSourceKind.SolarPanel ? "folded" : "switched off";
                parts.Add(n == 1 ? $"your {noun} is {state}" : $"your {n} {noun}s are {state}");
            }

            string text = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }
    }

    /// <summary>
    /// Engines that burn only electric charge: no mass leaves the ship, so the trip is limited by electricity, not fuel.
    /// </summary>
    /// <remarks>
    /// KSP draws a massless propellant at "mass flow x ratio": an engine of thrust F and Isp I asks for
    /// F / (I g0) x ratio units a second (F in kN), so the charge it needs is proportional to the thrust it makes.
    /// The Jibar Side Thruster GIANT: 3000 kN, Isp 320, ratio 200000, so 63.7 charge per second per kN.
    /// </remarks>
    public sealed class ElectricDrive
    {
        /// <summary>The electric engines at 100 % (N).</summary>
        public double FullThrust { get; }
        /// <summary>Electric charge per second at <see cref="FullThrust"/>.</summary>
        public double ChargeAtFull { get; }
        public ShipPower Power { get; }
        /// <summary>
        /// The farthest the trip goes from the Sun (m): where the solar panels are weakest. NaN: where the ship is now.
        /// </summary>
        public double WeakestSunDistance { get; set; } = double.NaN;

        public ElectricDrive(double fullThrust, double chargeAtFull, ShipPower power)
        {
            FullThrust = fullThrust;
            ChargeAtFull = chargeAtFull;
            Power = power ?? new ShipPower();
        }

        /// <summary>Electric charge per second the engines need to make <paramref name="thrust"/> (N).</summary>
        public double ChargeFor(double thrust) => FullThrust > 0 ? ChargeAtFull * Math.Max(0, thrust) / FullThrust : 0;

        /// <summary>Charge made per second through the whole trip (solar at its weakest point).</summary>
        public double Supply => Power.SupplyAt(WeakestSunDistance);

        /// <summary>
        /// Seconds of burning at <paramref name="thrust"/> (N) the power allows, never touching the floor: infinite when the
        /// supply keeps up by itself.
        /// </summary>
        public double BurnTime(double thrust)
        {
            double deficit = ChargeFor(thrust) - Supply;
            if (!(deficit > 0)) return double.PositiveInfinity;
            return Power.Usable / deficit;
        }

        /// <summary>
        /// Delta-v at <paramref name="acceleration"/> (m/s^2) for a ship of <paramref name="mass"/> (kg): no mass is burned,
        /// so it's the acceleration times the burn time. Infinite when the supply keeps up; 0 beyond the engines' thrust.
        /// </summary>
        public double DeltaVAt(double acceleration, double mass)
        {
            if (!(acceleration > 0) || !(mass > 0) || !(FullThrust > 0)) return 0;
            double thrust = acceleration * mass;
            if (thrust > FullThrust * (1 + 1e-12)) return 0;
            double t = BurnTime(thrust);
            return double.IsPositiveInfinity(t) ? double.PositiveInfinity : acceleration * t;
        }

        /// <summary>The highest acceleration (m/s^2) the supply sustains by itself, for a ship of <paramref name="mass"/> (kg).</summary>
        public double SustainableAcceleration(double mass)
        {
            if (!(mass > 0) || !(ChargeAtFull > 0)) return 0;
            double thrust = Math.Min(FullThrust, Supply / ChargeAtFull * FullThrust);
            return thrust / mass;
        }

        public ElectricDrive WithPower(ShipPower power) => new ElectricDrive(FullThrust, ChargeAtFull, power) { WeakestSunDistance = WeakestSunDistance };
    }
}
