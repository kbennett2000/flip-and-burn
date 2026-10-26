using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FlipAndBurn.Core;
using KSP.Localization;

namespace FlipAndBurn
{
    public enum FlipStyle
    {
        CutFlipRelight = 0,
        SkewFlip = 1,
    }

    /// <summary>
    /// Everything behind the Flip and Burn window except drawing: which targets are offered, the player's inputs, and the
    /// latest trip estimate turned into display text.
    /// </summary>
    public sealed class Planner
    {
        public const double OneG = TripCalculator.OneG;
        private const double RefreshSeconds = 1.0;

        // ---- inputs
        public string TargetName { get; private set; }
        public double AccelerationG { get; set; } = 1.0;
        public FlipStyle Flip { get; set; } = FlipStyle.CutFlipRelight;
        private readonly Dictionary<string, double> parkingAltitudes = new Dictionary<string, double>();

        // ---- outputs, rebuilt by Refresh
        /// <summary>When set, the ship can't plan from where it is; this says what to do instead. Nothing else is shown.</summary>
        public string SituationMessage { get; private set; }
        public List<CelestialBody> Targets { get; } = new List<CelestialBody>();
        public string DepartureName { get; private set; }
        public TripPlan Plan { get; private set; }
        public ShipPerformance Ship { get; private set; }
        public TripReadiness Readiness { get; private set; }
        public double PlannedAtUT { get; private set; }

        /// <summary>Label/value rows for the trip, in display order.</summary>
        public List<KeyValuePair<string, string>> TripRows { get; } = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> ShipRows { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>Plain-sentence notes: shortfalls, the Sun, rough numbers.</summary>
        public List<string> Notes { get; } = new List<string>();
        public bool HasShortfall { get; private set; }

        private double nextRefresh;
        private string lastKey = "";

        public CelestialBody Target => Targets.FirstOrDefault(b => b.bodyName == TargetName);

        public bool SelectTarget(string bodyName)
        {
            CelestialBody body = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == bodyName);
            if (body == null) return false;
            TargetName = bodyName;
            nextRefresh = 0;
            return true;
        }

        public double ParkingAltitude(CelestialBody body)
        {
            if (body == null) return 0;
            if (!parkingAltitudes.TryGetValue(body.bodyName, out double alt))
            {
                alt = DefaultParkingAltitude(body);
                parkingAltitudes[body.bodyName] = alt;
            }

            return alt;
        }

        public void SetParkingAltitude(CelestialBody body, double altitude)
        {
            if (body == null) return;
            parkingAltitudes[body.bodyName] = altitude;
        }

        public static double DefaultParkingAltitude(CelestialBody body)
        {
            double atmosphere = body.atmosphere ? body.atmosphereDepth : 0;
            double terrain = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0;
            return ParkingOrbit.DefaultAltitude(body.Radius, atmosphere, terrain, body.sphereOfInfluence);
        }

        /// <summary>Call every frame; does real work at most once a second, or right away when an input changed.</summary>
        public void Update(Vessel vessel)
        {
            if (vessel == null) return;
            string key = $"{vessel.id}|{TargetName}|{AccelerationG:R}|{(Target != null ? ParkingAltitude(Target) : 0):R}|{Flip}";
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (key == lastKey && now < nextRefresh) return;
            lastKey = key;
            nextRefresh = now + RefreshSeconds;
            Refresh(vessel);
        }

        public void Refresh(Vessel vessel)
        {
            TripRows.Clear();
            ShipRows.Clear();
            Notes.Clear();
            Plan = null;
            Readiness = null;
            HasShortfall = true;

            SituationMessage = CheckSituation(vessel);
            CollectTargets(vessel);
            if (SituationMessage != null) return;

            if (Target == null)
                TargetName = null;

            Ship = ShipPerformanceReader.Read(vessel, out string engineProblem);
            double requested = AccelerationG * OneG;
            if (engineProblem == null)
            {
                double atChosen = Ship.DeltaVAtAcceleration(requested);
                ShipRows.Add(Row("Ship's delta-v", atChosen > 0
                    ? $"{FormatSpeed(atChosen)} at {FormatG(AccelerationG)}"
                    : "—"));
                ShipRows.Add(Row("  at full thrust", FormatSpeed(Ship.FullThrustDeltaV)));
                ShipRows.Add(Row("Ship's max acceleration", FormatG(Ship.MaxAcceleration / OneG)));
            }
            else
            {
                Notes.Add(engineProblem);
            }

            if (Target == null)
            {
                Notes.Insert(0, "Pick a planet to fly to.");
                return;
            }

            if (!(AccelerationG > 0))
            {
                Notes.Insert(0, "Set an acceleration above 0 g.");
                return;
            }

            CelestialBody target = Target;
            double ut = Planetarium.GetUniversalTime();
            PlannedAtUT = ut;
            StateVector ship = KspShip.HeliocentricState(vessel, ut);
            var request = new TripRequest
            {
                ShipPosition = ship.Position,
                ShipVelocity = ship.Velocity,
                StartTime = ut,
                Target = new TargetPlanet(target.bodyName, new KspOrbitEphemeris(target.orbit), target.gravParameter, target.Radius),
                Acceleration = requested,
                ParkingAltitude = ParkingAltitude(target),
                SunGravParameter = Planetarium.fetch.Sun.gravParameter,
            };
            Plan = TripCalculator.Plan(request);

            if (!Plan.Feasible)
            {
                Notes.Insert(0, Plan.Problem == TripProblem.InvalidInput
                    ? $"Can't plan this trip: {Plan.ProblemDetail}."
                    : $"At {FormatG(AccelerationG)} there's no way to reach {BodyName(target)} within about {FormatYears(request.MaxTripTime)}.");
                return;
            }

            TripRows.Add(Row("Trip time", FormatTime(Plan.TripTime)));
            TripRows.Add(Row("Flip in", FormatTime(Plan.TimeToFlip)));
            TripRows.Add(Row($"Peak speed (vs. {DepartureName})", FormatSpeed(Plan.PeakSpeed)));
            TripRows.Add(Row("Delta-v needed", FormatSpeed(Plan.TotalDeltaV)));

            if (engineProblem == null)
            {
                Readiness = TripReadiness.Check(Plan, Ship, requested);
                HasShortfall = !Readiness.Ready;
                if (!Readiness.CanHoldAcceleration)
                {
                    Notes.Add($"This ship can reach only {FormatG(Ship.MaxAcceleration / OneG)}, less than the {FormatG(AccelerationG)} you asked for. " +
                              $"Lower the acceleration to {FormatG(Math.Floor(Ship.MaxAcceleration / OneG * 100) / 100)} or less.");
                }
                else if (!Readiness.HasEnoughDeltaV)
                {
                    Notes.Add($"Not enough delta-v: the trip needs {FormatSpeed(Readiness.DeltaVNeeded)}, but at {FormatG(AccelerationG)} " +
                              $"this ship has {FormatSpeed(Readiness.DeltaVAvailable)}. Short by {FormatSpeed(Readiness.DeltaVShortfall)}.");
                }
            }

            if (Plan.PassesNearSun)
            {
                Notes.Add($"The path passes {FormatDistance(Plan.ClosestSunDistance)} from the Sun, inside the " +
                          $"{FormatDistance(request.SunSafetyDistance)} safety limit. Flip and Burn can't route around the Sun yet; " +
                          $"try again when {BodyName(target)} isn't behind it.");
            }
            else if (Plan.GravityNotNegligible)
            {
                Notes.Add("At this acceleration the Sun's pull is too strong to ignore, so these numbers are only rough.");
            }
        }

        private string CheckSituation(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            CelestialBody sun = Planetarium.fetch.Sun;
            DepartureName = body == sun ? "start" : BodyName(body);
            string name = BodyName(body);

            switch (vessel.situation)
            {
                case Vessel.Situations.LANDED:
                case Vessel.Situations.SPLASHED:
                case Vessel.Situations.PRELAUNCH:
                    return $"You're on the surface of {name}. Get into orbit first: Flip and Burn plans trips from orbit.";
                case Vessel.Situations.FLYING:
                    return $"You're flying in {name}'s atmosphere. Get into a stable orbit first.";
                case Vessel.Situations.SUB_ORBITAL:
                    return $"Your orbit around {name} isn't stable yet: it dips into the {(body.atmosphere ? "atmosphere" : "ground")}. " +
                           "Finish getting into orbit first.";
            }

            if (body != sun && body.referenceBody != sun)
            {
                string parent = BodyName(body.referenceBody);
                return $"You're orbiting {name}, a moon of {parent}. Flip and Burn flies between planets: " +
                       $"get into orbit around {parent} first, or leave it for an orbit around the Sun.";
            }

            return null;
        }

        private void CollectTargets(Vessel vessel)
        {
            Targets.Clear();
            CelestialBody sun = Planetarium.fetch.Sun;
            // Every planet orbiting the Sun, read at runtime, so Outer Planets Mod's planets appear on their own.
            Targets.AddRange(FlightGlobals.Bodies
                .Where(b => b != sun && b.referenceBody == sun && b != vessel.mainBody)
                .OrderBy(b => b.orbit.semiMajorAxis));
        }

        private static KeyValuePair<string, string> Row(string label, string value) => new KeyValuePair<string, string>(label, value);

        public static string BodyName(CelestialBody body) => body.displayName.LocalizeRemoveGender();

        // ---- formatting

        /// <summary>KSP's own compact time format, which follows the game's Kerbin/Earth time setting.</summary>
        public static string FormatTime(double seconds) => KSPUtil.PrintDateDeltaCompact(seconds, true, false);

        public static string FormatSpeed(double metresPerSecond)
        {
            double kms = metresPerSecond / 1000.0;
            if (metresPerSecond < 1000) return metresPerSecond.ToString("N0", CultureInfo.InvariantCulture) + " m/s";
            if (kms < 10) return kms.ToString("N2", CultureInfo.InvariantCulture) + " km/s";
            if (kms < 100) return kms.ToString("N1", CultureInfo.InvariantCulture) + " km/s";
            return kms.ToString("N0", CultureInfo.InvariantCulture) + " km/s";
        }

        public static string FormatG(double g) => g.ToString("0.00", CultureInfo.InvariantCulture) + " g";

        public static string FormatDistance(double metres)
        {
            double km = metres / 1000.0;
            if (km >= 1e6) return (km / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " million km";
            return km.ToString("N0", CultureInfo.InvariantCulture) + " km";
        }

        private static string FormatYears(double seconds)
        {
            // Year length follows the game's time setting (Kerbin: 426 days of 6 hours).
            double year = KSPUtil.dateTimeFormatter.Year;
            return (seconds / year).ToString("N0", CultureInfo.InvariantCulture) + " years";
        }
    }
}
