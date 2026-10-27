using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
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
        public TripType Trip { get; set; } = TripType.FullBurn;

        /// <summary>Share of the ship's delta-v a coast trip keeps unused (ADR 0002 addendum).</summary>
        public const double Reserve = 0.10;
        private readonly Dictionary<string, double> parkingAltitudes = new Dictionary<string, double>();

        // ---- outputs, rebuilt by Refresh
        /// <summary>When set, the ship can't plan from where it is; this says what to do instead. Nothing else is shown.</summary>
        public string SituationMessage { get; private set; }
        public List<CelestialBody> Targets { get; } = new List<CelestialBody>();
        public string DepartureName { get; private set; }
        public TripPlan Plan { get; private set; }
        public ShipPerformance Ship { get; private set; }
        /// <summary>The engine reader's full result (engines, notes, stages below), as of the last refresh.</summary>
        public ShipReading Reading { get; private set; }
        public TripReadiness Readiness { get; private set; }
        /// <summary>The coast trip: shown when Coast in the middle is chosen, suggested when a full burn is short.</summary>
        public CoastPlan Coast { get; private set; }
        /// <summary>KSP's infinite propellant cheat, as of the last refresh.</summary>
        public bool InfinitePropellant { get; private set; }
        public double PlannedAtUT { get; private set; }

        /// <summary>Label/value rows for the trip, in display order.</summary>
        public List<KeyValuePair<string, string>> TripRows { get; } = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> ShipRows { get; } = new List<KeyValuePair<string, string>>();
        /// <summary>Plain-sentence notes: shortfalls, the Sun, rough numbers.</summary>
        public List<string> Notes { get; } = new List<string>();
        public bool HasShortfall { get; private set; }

        /// <summary>The trip can be flown as planned: Start is enabled (infinite propellant counts as enough delta-v).</summary>
        public bool CanStart => SituationMessage == null && Plan != null && Plan.Feasible && !HasShortfall &&
                                (Trip == TripType.FullBurn || (Coast != null && Coast.Feasible));

        private double lastAvailable;

        /// <summary>This ship's measured propellant loss while the engines are off (kg/s); 0 if none or not measured yet.</summary>
        public Func<double> LossRate { get; set; } = () => 0;

        /// <summary>
        /// Coast trips: delta-v set aside for propellant lost while the engines are off over the trip (m/s), when the loss
        /// measured on this ship is big enough to matter; 0 otherwise.
        /// </summary>
        public double LossAllowance { get; private set; }

        /// <summary>A measured loss counts once it would cost this share of the ship's delta-v over the trip.</summary>
        public const double LossWorthAllowing = 0.001;

        /// <summary>What the autopilot needs to fly the trip shown.</summary>
        public TripStart BuildTripStart() => new TripStart
        {
            Target = Target,
            Acceleration = AccelerationG * OneG,
            Flip = Flip == FlipStyle.SkewFlip ? FlipManeuver.SkewFlip : FlipManeuver.CutFlipRelight,
            Trip = Trip,
            ParkingAltitude = ParkingAltitude(Target),
            DeltaVBudget = (1 - Reserve) * lastAvailable - (Trip == TripType.CoastInTheMiddle ? LossAllowance : 0),
            LossAllowance = Trip == TripType.CoastInTheMiddle ? LossAllowance : 0,
            PlannedTripTime = Trip == TripType.CoastInTheMiddle && Coast != null ? Coast.TripTime : Plan?.TripTime ?? 0,
            DeltaVNeeded = Trip == TripType.CoastInTheMiddle && Coast != null ? Coast.DeltaVNeeded : Plan?.TotalDeltaV ?? 0,
            StartDeltaV = InfinitePropellant ? double.NaN : lastAvailable,
        };

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
            string key = $"{vessel.id}|{TargetName}|{AccelerationG:R}|{(Target != null ? ParkingAltitude(Target) : 0):R}|{Flip}|{Trip}|{CheatOptions.InfinitePropellant}";
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
            Coast = null;
            LossAllowance = 0;
            LossBlocks = false;
            Readiness = null;
            HasShortfall = true;
            InfinitePropellant = CheatOptions.InfinitePropellant;

            SituationMessage = CheckSituation(vessel);
            CollectTargets(vessel);
            if (SituationMessage != null) return;

            if (Target == null)
                TargetName = null;

            ShipReading reading = ShipPerformanceReader.Read(vessel, out string engineProblem);
            Reading = reading;
            Ship = reading.Report.Performance;
            if (engineProblem == null && reading.Report.NoThrust)
                engineProblem = "None of the active engines can push the ship in space.";
            double requested = AccelerationG * OneG;
            double available = 0;
            string estimate = reading.Report.Estimated ? " (estimate)" : "";
            if (engineProblem == null)
            {
                available = Ship.DeltaVAtAcceleration(requested);
                ShipRows.Add(Row("Ship's delta-v", available > 0
                    ? $"{FormatSpeed(available)} at {FormatG(AccelerationG)}{estimate}"
                    : "—"));
                ShipRows.Add(Row("  at full thrust", FormatSpeed(Ship.FullThrustDeltaV) + estimate));
                ShipRows.Add(Row("Ship's max acceleration", FormatMaxG(Ship.MaxAcceleration) + estimate));
            }

            Notes.AddRange(reading.Report.Notes);
            if (engineProblem != null) Notes.Add(engineProblem);
            if (reading.StagesBelow > 0)
            {
                Notes.Add($"This ship has {reading.StagesBelow} more stage{(reading.StagesBelow == 1 ? "" : "s")} below this one. " +
                          "Flip and Burn only counts the engines running now and the fuel they can reach.");
            }

            if (InfinitePropellant)
                Notes.Add("KSP's Infinite Propellant cheat is on, so fuel isn't counted.");

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
            CelestialBody sun = Planetarium.fetch.Sun;
            var request = new TripRequest
            {
                ShipPosition = ship.Position,
                ShipVelocity = ship.Velocity,
                StartTime = ut,
                Target = AsPlanet(target),
                Departure = vessel.mainBody != sun ? AsPlanet(vessel.mainBody) : null,
                Acceleration = requested,
                ParkingAltitude = ParkingAltitude(target),
                SunGravParameter = sun.gravParameter,
            };
            Plan = TripCalculator.Plan(request);

            if (!Plan.Feasible)
            {
                Notes.Insert(0, PlanProblem(Plan, request, target));
                return;
            }

            lastAvailable = available;
            bool canHold = engineProblem == null && Ship.HasThrust && requested <= Ship.MaxAcceleration;
            if (engineProblem == null && !canHold)
            {
                Notes.Add($"This ship can reach only {FormatMaxG(Ship.MaxAcceleration)}, less than the {FormatG(AccelerationG)} you asked for. " +
                          "Lower the acceleration to that or less.");
            }

            if (Trip == TripType.CoastInTheMiddle)
                RefreshCoast(request, target, available, canHold);
            else
                RefreshFullBurn(request, target, available, canHold, engineProblem == null);

            if (Plan.UsesSunDetour)
            {
                Notes.Add($"The Sun is in the way: the straight path passes {FormatDistance(Plan.StraightClosestSunDistance)} from it, closer than the " +
                          $"safe {FormatDistance(request.SunSafetyDistance)}. The trip goes around it in two legs; the numbers above include the detour.");
            }
            else if (Plan.GravityNotNegligible && Trip == TripType.FullBurn)
            {
                Notes.Add("At this acceleration the Sun's pull is too strong to ignore, so these numbers are only rough.");
            }
        }

        private void RefreshFullBurn(TripRequest request, CelestialBody target, double available, bool canHold, bool shipKnown)
        {
            TripRows.Add(Row("Trip time", FormatTime(Plan.TripTime)));
            TripRows.Add(Row("Flip in", FormatTime(Plan.TimeToFlip)));
            TripRows.Add(Row($"Top speed (relative to {DepartureName})", FormatSpeed(Plan.PeakSpeed)));
            TripRows.Add(Row("Delta-v needed", FormatSpeed(Plan.TotalDeltaV)));
            if (!shipKnown) return;

            Readiness = TripReadiness.Check(Plan, Ship, request.Acceleration);
            bool enough = InfinitePropellant || Readiness.HasEnoughDeltaV;
            HasShortfall = !(canHold && enough);
            if (!canHold || enough) return;

            (string needed, string has, string shortBy) = Readout.Shortfall(Readiness.DeltaVNeeded, Readiness.DeltaVAvailable);
            Notes.Add($"Not enough delta-v: the trip needs {needed}, but at {FormatG(AccelerationG)} " +
                      $"this ship has {has}. Short by {shortBy}.");
            if (Plan.UsesSunDetour)
            {
                Notes.Add("A trip with a coast in the middle can't go around the Sun yet, so there's no coast trip to suggest right now.");
                return;
            }

            Coast = CoastPlanFor(request, (1 - Reserve) * available);
            Notes.Add(Coast.Feasible
                ? $"A trip with a coast in the middle fits: about {FormatTime(Coast.TripTime)}, keeping 10% of the delta-v in reserve" +
                  (LossAllowance > 0 ? " and allowing for this ship's boil-off" : "") + ". " +
                  "Choose \"Coast in the middle\" to see it."
                : "Not even a trip with a coast in the middle fits this ship's delta-v.");
        }

        private void RefreshCoast(TripRequest request, CelestialBody target, double available, bool canHold)
        {
            if (Plan.UsesSunDetour)
            {
                Notes.Insert(0, $"A trip with a coast in the middle can't go around the Sun yet, and the Sun is between here and {BodyName(target)}. " +
                                "Choose \"Full burn\", or wait until the Sun is out of the way.");
                return;
            }

            if (!(available > 0))
            {
                Notes.Insert(0, "A coast trip is planned from the ship's delta-v, and Flip and Burn can't read this ship's.");
                return;
            }

            Coast = CoastPlanFor(request, (1 - Reserve) * available);
            if (!Coast.Feasible)
            {
                Notes.Insert(0, LossBlocks
                    ? $"No coast trip fits once this ship's boil-off is counted: its tanks lose about " +
                      $"{FormatMass(Math.Max(0, LossRate?.Invoke() ?? 0) * DayLength)} of fuel a day with the engines off, so it would arrive with " +
                      "less than 10% in reserve. Keep the tanks powered if they need power to stay cold, or choose a higher acceleration for a shorter trip."
                    : $"No coast trip fits either: at {FormatG(AccelerationG)} this ship has {FormatSpeed(available)} of delta-v, and after keeping " +
                      $"10% in reserve that isn't enough to reach {BodyName(target)} and stop there.");
                return;
            }

            HasShortfall = !canHold;
            TripRows.Add(Row("Burn to speed up", FormatTime(Coast.BurnUpTime)));
            TripRows.Add(Row("Coast", FormatTime(Coast.CoastTime)));
            TripRows.Add(Row("Burn to stop", FormatTime(Coast.BurnDownTime)));
            TripRows.Add(Row("Trip time", FormatTime(Coast.TripTime)));
            TripRows.Add(Row("Delta-v needed", FormatSpeed(Coast.DeltaVNeeded)));
            TripRows.Add(Row("Kept in reserve", $"{FormatSpeed(Reserve * available)} (10%)"));
            if (LossAllowance > 0) TripRows.Add(Row("Set aside for boil-off", FormatSpeed(LossAllowance)));
            string lossNote = LossNote();
            if (lossNote != null) Notes.Add(lossNote);
            if (Coast.CoastTime < 1)
                Notes.Add("This ship has the delta-v for a full burn, so there's nothing to coast: \"Full burn\" is the same trip.");
        }

        // A coast plan takes a tenth of a second or so, so reuse it for a while unless an input changed.
        private const double CoastRefreshSeconds = 10.0;
        private CoastPlan cachedCoast;
        private string cachedCoastKey = "";
        private double cachedCoastUntil;

        private double cachedLossDeltaV;
        private bool cachedLossBlocks;

        /// <summary>Coast trips: the trip would fit, but not once this ship's measured boil-off is allowed for.</summary>
        public bool LossBlocks { get; private set; }

        // The coast plan; with a measured loss that matters, the slower plan that leaves room for it (LossAllowance set).
        private CoastPlan CoastPlanFor(TripRequest request, double budget)
        {
            double rate = Math.Max(0, LossRate?.Invoke() ?? 0);
            if (InfinitePropellant || Ship == null) rate = 0;
            string key = $"{lastKey}|{budget:R}|{rate:R}";
            double now = UnityEngine.Time.realtimeSinceStartup;
            if (cachedCoast == null || key != cachedCoastKey || now >= cachedCoastUntil)
            {
                cachedCoast = CoastPlanner.Plan(request, budget);
                cachedLossDeltaV = 0;
                cachedLossBlocks = false;
                if (rate > 0 && cachedCoast.Feasible)
                {
                    // Worth allowing for only if the loss over the plain trip costs a noticeable share of the delta-v.
                    double plainCost = BackgroundLoss.DeltaVCost(Ship, request.Acceleration, rate * cachedCoast.TripTime);
                    if (plainCost >= LossWorthAllowing * (budget / (1 - Reserve)))
                    {
                        cachedCoast = CoastPlanner.PlanAllowingForLoss(request, budget, Ship, rate, out cachedLossDeltaV);
                        cachedLossBlocks = !cachedCoast.Feasible;
                    }
                }

                cachedCoastKey = key;
                cachedCoastUntil = now + CoastRefreshSeconds;
            }

            LossAllowance = cachedCoast.Feasible ? cachedLossDeltaV : 0;
            LossBlocks = cachedLossBlocks;
            return cachedCoast;
        }

        // The planner's note on a measured loss it allows for, in plain words.
        private string LossNote()
        {
            if (!(LossAllowance > 0)) return null;
            double rate = Math.Max(0, LossRate?.Invoke() ?? 0);
            return $"This ship's tanks lose about {FormatMass(rate * DayLength)} of fuel a day with the engines off (boil-off, measured on this " +
                   "ship). The trip allows for it, so it still arrives with 10% in reserve.";
        }

        private string PlanProblem(TripPlan plan, TripRequest request, CelestialBody target)
        {
            switch (plan.Problem)
            {
                case TripProblem.TargetGravityTooStrong:
                    double km = request.ParkingAltitude / 1000;
                    return $"{BodyName(target)}'s gravity at the {km.ToString("N0", CultureInfo.InvariantCulture)} km parking orbit is " +
                           $"{FormatG(plan.TargetGravityAtParking / OneG)}, and the ship has to out-pull it to settle into that orbit. " +
                           $"Choose at least {FormatG(Math.Ceiling(plan.MinimumAcceleration / OneG * 100) / 100)}, or a higher parking orbit.";
                case TripProblem.InvalidInput:
                    return $"Can't plan this trip: {plan.ProblemDetail}.";
                default:
                    return $"At {FormatG(AccelerationG)} there's no way to reach {BodyName(target)} within about {FormatYears(request.MaxTripTime)}.";
            }
        }

        internal static TargetPlanet AsPlanet(CelestialBody body) =>
            new TargetPlanet(body.bodyName, new KspOrbitEphemeris(body.orbit), body.gravParameter, body.Radius)
            {
                SphereOfInfluence = body.sphereOfInfluence,
            };

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

        public static string FormatSpeed(double metresPerSecond) => Readout.Speed(metresPerSecond);

        /// <summary>A game day (s): 6 hours with Kerbin time, 24 with Earth time.</summary>
        public static double DayLength => KSPUtil.dateTimeFormatter?.Day > 0 ? KSPUtil.dateTimeFormatter.Day : 21600;

        public static string FormatMass(double kg) => kg >= 1000
            ? (kg / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " t"
            : kg.ToString(kg >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " kg";

        /// <summary>A ship's most acceleration (m/s^2), rounded down: the window never promises more than the ship can do.</summary>
        public static string FormatMaxG(double metresPerSecondSquared) => FormatG(Math.Floor(metresPerSecondSquared / OneG * 100) / 100);

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
