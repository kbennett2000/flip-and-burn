using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FlipAndBurn.Core.Guidance;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// A trip in progress, as saved with the ship (ADR 0004): enough to pick the trip up again after the game is saved and
    /// loaded, or after the player comes back to the ship. The plan itself isn't saved: a resumed trip is re-planned from
    /// where the ship is then.
    /// </summary>
    /// <remarks>
    /// Stored as plain name = value pairs so it fits a KSP ConfigNode. Numbers use the invariant culture and round-trip
    /// format, so a value reads back exactly whatever the player's language settings.
    /// </remarks>
    public sealed class TripRecord
    {
        /// <summary>The format written now. A record with a higher version came from a newer Flip and Burn and isn't read.</summary>
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        /// <summary>The target's internal name (KSP's bodyName).</summary>
        public string Target { get; set; } = "";
        public TripType Trip { get; set; }
        public FlipManeuver Flip { get; set; }
        /// <summary>The acceleration the player chose at Start (m/s^2).</summary>
        public double ChosenAcceleration { get; set; }
        /// <summary>The acceleration being flown (m/s^2): lower than chosen after an overheating re-plan.</summary>
        public double Acceleration { get; set; }
        public double ParkingAltitude { get; set; }
        /// <summary>Coast trips: the delta-v the burns may use, set at Start (m/s).</summary>
        public double DeltaVBudget { get; set; }
        /// <summary>Delta-v burned so far (m/s).</summary>
        public double DeltaVUsed { get; set; }
        public double StartUT { get; set; }
        /// <summary>When the trip is planned to arrive (UT), as last planned.</summary>
        public double PlannedArrivalUT { get; set; }
        /// <summary>The last moment the trip was flying (UT): when it was saved while flying, or when it was paused.</summary>
        public double LastFlownUT { get; set; }
        public int Replans { get; set; }
        /// <summary>The trip time the planner showed at Start (s), for the arrival report. Optional: NaN if not saved.</summary>
        public double StartPlannedTripTime { get; set; } = double.NaN;
        /// <summary>
        /// Coast trips: delta-v set aside at Start for propellant lost while the engines are off (boil-off), measured on this
        /// ship (m/s); not part of <see cref="DeltaVBudget"/>. Optional: 0 if not saved.
        /// </summary>
        public double LossAllowance { get; set; }
        /// <summary>Engines Flip and Burn shut down (a cut, the coast), by part persistentId, to relight when it resumes.</summary>
        public List<uint> CutEngines { get; set; } = new List<uint>();

        /// <summary>The coast budget not yet used (m/s), never below zero.</summary>
        public double DeltaVLeft => Math.Max(0, DeltaVBudget - DeltaVUsed);

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public List<KeyValuePair<string, string>> ToValues()
        {
            var v = new List<KeyValuePair<string, string>>();
            void Add(string name, string value) => v.Add(new KeyValuePair<string, string>(name, value));
            Add("version", Version.ToString(Inv));
            Add("target", Target ?? "");
            Add("trip", Trip.ToString());
            Add("flip", Flip.ToString());
            Add("chosenAcceleration", ChosenAcceleration.ToString("R", Inv));
            Add("acceleration", Acceleration.ToString("R", Inv));
            Add("parkingAltitude", ParkingAltitude.ToString("R", Inv));
            Add("deltaVBudget", DeltaVBudget.ToString("R", Inv));
            Add("deltaVUsed", DeltaVUsed.ToString("R", Inv));
            Add("startUT", StartUT.ToString("R", Inv));
            Add("plannedArrivalUT", PlannedArrivalUT.ToString("R", Inv));
            Add("lastFlownUT", LastFlownUT.ToString("R", Inv));
            Add("replans", Replans.ToString(Inv));
            if (!double.IsNaN(StartPlannedTripTime)) Add("startPlannedTripTime", StartPlannedTripTime.ToString("R", Inv));
            if (LossAllowance > 0) Add("lossAllowance", LossAllowance.ToString("R", Inv));
            Add("cutEngines", string.Join(",", CutEngines.Select(id => id.ToString(Inv))));
            return v;
        }

        /// <summary>
        /// Read a record back. Returns null, with the reason in <paramref name="problem"/>, if it is from a newer version or
        /// anything is missing or unreadable: a trip that can't be read exactly isn't flown.
        /// </summary>
        public static TripRecord FromValues(IEnumerable<KeyValuePair<string, string>> values, out string problem)
        {
            var map = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in values ?? Enumerable.Empty<KeyValuePair<string, string>>())
                map[kv.Key] = kv.Value;

            problem = null;
            string missing = null;
            string Get(string name)
            {
                if (map.TryGetValue(name, out string s)) return s;
                missing ??= name;
                return null;
            }

            if (!int.TryParse(Get("version"), NumberStyles.Integer, Inv, out int version))
            {
                problem = "the saved trip has no readable version";
                return null;
            }

            if (version > CurrentVersion)
            {
                problem = $"the saved trip was written by a newer Flip and Burn (format {version}; this one reads up to {CurrentVersion})";
                return null;
            }

            var r = new TripRecord { Version = version, Target = Get("target") ?? "" };
            bool ok = Enum.TryParse(Get("trip") ?? "", out TripType trip) & Enum.TryParse(Get("flip") ?? "", out FlipManeuver flip);
            r.Trip = trip;
            r.Flip = flip;
            ok &= Num("chosenAcceleration", x => r.ChosenAcceleration = x) & Num("acceleration", x => r.Acceleration = x) &
                  Num("parkingAltitude", x => r.ParkingAltitude = x) & Num("deltaVBudget", x => r.DeltaVBudget = x) &
                  Num("deltaVUsed", x => r.DeltaVUsed = x) & Num("startUT", x => r.StartUT = x) &
                  Num("plannedArrivalUT", x => r.PlannedArrivalUT = x) & Num("lastFlownUT", x => r.LastFlownUT = x);
            ok &= int.TryParse(Get("replans"), NumberStyles.Integer, Inv, out int replans);
            r.Replans = replans;
            // Optional (added after the first records were written): absent reads as NaN.
            if (map.TryGetValue("startPlannedTripTime", out string planned))
            {
                if (double.TryParse(planned, NumberStyles.Float, Inv, out double t)) r.StartPlannedTripTime = t;
                else ok = false;
            }

            if (map.TryGetValue("lossAllowance", out string allowance))
            {
                if (double.TryParse(allowance, NumberStyles.Float, Inv, out double a) && a >= 0 && !double.IsInfinity(a)) r.LossAllowance = a;
                else ok = false;
            }

            string cut = Get("cutEngines");
            if (cut != null)
            {
                foreach (string part in cut.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(part.Trim(), NumberStyles.Integer, Inv, out uint id)) r.CutEngines.Add(id);
                    else ok = false;
                }
            }

            if (missing != null)
            {
                problem = $"the saved trip is missing '{missing}'";
                return null;
            }

            if (!ok || string.IsNullOrEmpty(r.Target) || !(r.Acceleration > 0))
            {
                problem = "the saved trip has a value that can't be read";
                return null;
            }

            return r;

            bool Num(string name, Action<double> set)
            {
                string s = Get(name);
                if (s == null || !double.TryParse(s, NumberStyles.Float, Inv, out double x) || double.IsNaN(x) || double.IsInfinity(x)) return false;
                set(x);
                return true;
            }
        }
    }
}
