using System;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using JetBrains.Annotations;
using MuMech;
using UnityEngine;

namespace FlipAndBurn
{
    /// <summary>What the player chose in the window, for one trip.</summary>
    public sealed class TripStart
    {
        public CelestialBody Target;
        public double Acceleration;        // m/s^2
        public FlipManeuver Flip;
        public TripType Trip;
        public double ParkingAltitude;     // m
        public double DeltaVBudget;        // m/s, coast trips
        public double PlannedTripTime;     // s, from the planner
        public double DeltaVNeeded;        // m/s, from the planner (a coast trip's includes the guidance margin)
        public double StartDeltaV = double.NaN;   // m/s, the ship's delta-v at Start; NaN when fuel isn't counted
    }

    /// <summary>
    /// Flies a trip: calls the guidance every physics step, points the ship with MechJeb's attitude control, sets thrust
    /// with MechJeb's throttle and acceleration limit, and runs time warp (ADR 0003, "Flying").
    /// </summary>
    /// <remarks>
    /// MechJeb finds this module on its own, like the window. It is enabled while a trip is in progress: flying, or paused.
    ///
    /// A trip is saved with the ship (<see cref="TripRecord"/> in this module's node of MechJeb's per-ship settings,
    /// ADR 0004). Leaving the ship any way (another scene, another ship, quitting) pauses it: the engine is cut and
    /// MechJeb's controls are given back, exactly as at the end of a trip. Coming back to the ship, or loading a save made
    /// during the trip, resumes it on its own, re-planned from where the ship is then.
    ///
    /// The player's MechJeb settings for the two controllers it borrows (thrust and attitude) are saved with MechJeb's own
    /// save routine when the trip starts or resumes and loaded back the same way when it ends, pauses or is aborted. Any
    /// save MechJeb makes in between gets the player's values too (<see cref="MechJebModuleFlipAndBurnSaveGuard"/>).
    /// </remarks>
    [UsedImplicitly]
    public class MechJebModuleFlipAndBurnAutopilot : ComputerModule
    {
        /// <summary>Outside the flip, thrust only when pointing within this many degrees of the commanded direction.</summary>
        public const double PointingTolerance = 5;
        /// <summary>Physics warp rates Flip and Burn uses while thrusting (x1 first), and the highest it will use.</summary>
        public static float[] PhysicsRates = { 1, 4, 10, 25 };
        /// <summary>
        /// Highest physics warp used, measured on Kris's machine with the Bebop Refueller (ADR 0003, "Time warp"): 25x was
        /// steady while the ship was cool, but with the ship running hot its Kerbstein drive overheated and exploded at 25x
        /// within 20 s. 10x was steady hot or cool.
        /// </summary>
        public static double MaxPhysicsRate = 10;
        /// <summary>Hottest part, as a share of its temperature limit, above which physics warp is held to 4x / to 1x.</summary>
        public const double WarmPart = 0.6, HotPart = 0.75;
        /// <summary>Above this share of its temperature limit a part is about to fail: re-plan at 80 % of the acceleration.</summary>
        public const double OverheatingPart = 0.9;
        private const double SecondsBetweenWarpChanges = 1.0;   // real seconds
        /// <summary>Real seconds the ship must be loaded, active and off rails before a paused trip resumes (lets the scene settle).</summary>
        public const float ResumeSettleSeconds = 3f;
        /// <summary>The trip's node in this module's part of MechJeb's per-ship settings (ADR 0004).</summary>
        public const string TripNodeName = "FLIPANDBURN_TRIP";

        private FlipAndBurnGuidance guidance;
        private GuidanceConfig baseConfig;
        private double deltaVUsed;   // m/s burned so far (for a coast trip's re-plan)
        private TripStart trip;
        private ConfigNode savedLocal, savedType, savedGlobal;
        private WarpRatesSnapshot playerPhysicsRates;
        private float[] tripPhysicsRates;
        // The trip in progress (flying or paused), as saved with the ship; null when there is none.
        private TripRecord record;
        // The player's MechJeb acceleration limit, the only MechJeb settings the trip changes, and the trip's values while a
        // save swaps the player's in.
        private bool playerLimitAcceleration, tripLimitAcceleration, swappedForSave;
        private double playerMaxAcceleration, tripMaxAcceleration;
        private float settledSince = -1;
        private double startUT, plannedArrivalUT, flipStartUT = double.NaN, offCourseSince = double.NaN, mispointedSince = double.NaN,
            noThrustSince = double.NaN, weakSince = double.NaN, noEngineSince = double.NaN, cooldownUntil = double.NaN, lastWarpChange, bestPointing = double.MaxValue;
        private bool controlling;

        public MechJebModuleFlipAndBurnAutopilot(MechJebCore core) : base(core)
        {
            // MechJeb runs and saves its modules in rising priority: after the thrust controller (200), so this module's
            // OnSave puts the trip's acceleration limit back after the thrust controller has saved the player's
            // (MechJebModuleFlipAndBurnSaveGuard, priority 150, swaps the player's in first).
            Priority = 250;
        }

        public bool Running => controlling && guidance != null;
        /// <summary>A trip is in progress but not flying: the player left the ship, pressed Pause, or the game was just loaded.</summary>
        public bool Paused => record != null && !controlling;
        /// <summary>Paused with the Pause button: it waits for Resume while the player stays with the ship.</summary>
        public bool PausedByPlayer { get; private set; }
        /// <summary>Why the trip is paused, in plain words (shown in the window), or null.</summary>
        public string PauseNotice { get; private set; }
        /// <summary>The trip in progress as it would be saved now (for the window and the test helper), or null.</summary>
        public TripRecord Record => record;
        /// <summary>How many times this trip was resumed in this ship's current session (for the test helper).</summary>
        public int Resumes { get; private set; }
        /// <summary>The last resume's numbers (for the test helper): time away, and arrival before and after the re-plan.</summary>
        public double LastAwaySeconds { get; private set; } = double.NaN;
        public double LastResumeOldArrivalUT { get; private set; } = double.NaN;
        public double LastResumeNewArrivalUT { get; private set; } = double.NaN;
        public GuidanceCommand Last { get; private set; }
        public TripStart Trip => trip;
        /// <summary>Thrust acceleration felt right now (m/s^2).</summary>
        public double MeasuredAcceleration { get; private set; }
        /// <summary>Degrees between where the ship points and where the guidance wants it.</summary>
        public double PointingError { get; private set; }
        public string WarpStatus { get; private set; } = "";
        /// <summary>The hottest part and its temperature as a share of its limit (0 to 1).</summary>
        public string HottestPart { get; private set; } = "";
        public double HottestFraction { get; private set; }
        public double TurnRate { get; private set; }
        /// <summary>Why the last trip stopped, in plain words (null while running or before any trip).</summary>
        public string StopReason { get; private set; }
        public bool Arrived { get; private set; }
        /// <summary>True once the flip has finished (for the test helper).</summary>
        public double FlipSeconds { get; private set; } = double.NaN;
        public string SettingsCheck { get; private set; } = "";
        /// <summary>The acceleration being flown now (lower than chosen if the engines couldn't hold it).</summary>
        public double CurrentAcceleration { get; private set; }
        /// <summary>What changed during the trip, in plain words (shown in the window), or null.</summary>
        public string Notice { get; private set; }
        /// <summary>How many times the trip was re-planned at a lower acceleration.</summary>
        public int Replans { get; private set; }
        /// <summary>The delta-v expected to be left at arrival (m/s), from the last plan; NaN when fuel isn't counted.</summary>
        public double ReserveAtArrival { get; private set; } = double.NaN;
        /// <summary>That as a share of the delta-v the trip started with.</summary>
        public double ReserveShare { get; private set; } = double.NaN;
        /// <summary>Why the expected reserve is below the 10 % kept back, in plain words; null when it isn't.</summary>
        public string ReserveNote { get; private set; }

        // The expected reserve from a plan, and why it is short if it is.
        private void SetReserve(double reserve, double startDeltaV, string whyShort)
        {
            ReserveAtArrival = startDeltaV > 0 ? reserve : double.NaN;
            ReserveShare = startDeltaV > 0 ? reserve / startDeltaV : double.NaN;
            ReserveNote = ReserveShare < Planner.Reserve - 1e-6
                ? $"The ship is expected to arrive with {Planner.FormatSpeed(Math.Max(0, reserve))} of delta-v, {ReserveShare * 100:0.0}% of what it " +
                  $"started with: less than the {Planner.Reserve * 100:0}% kept in reserve, because {whyShort}."
                : null;
        }

        // The ship's delta-v when the trip started: the budget is the rest after the reserve.
        private double StartDeltaVOf(TripRecord r) => CheatOptions.InfinitePropellant || !(r.DeltaVBudget > 0) ? double.NaN : r.DeltaVBudget / (1 - Planner.Reserve);

        // ------------------------------------------------------------------ start and stop

        /// <summary>Hand the ship to Flip and Burn. Returns a problem in plain words, or null.</summary>
        public string StartTrip(TripStart start)
        {
            if (Running) return "A trip is already running.";
            if (start?.Target == null) return "No target.";
            Vessel v = Vessel;
            CelestialBody sun = Planetarium.fetch.Sun;
            TurnRate = EstimateTurnRate();
            var config = new GuidanceConfig
            {
                Target = Planner.AsPlanet(start.Target),
                Departure = v.mainBody != sun ? Planner.AsPlanet(v.mainBody) : null,
                Acceleration = start.Acceleration,
                Trip = start.Trip,
                DeltaVBudget = start.DeltaVBudget,
                Flip = start.Flip,
                ParkingAltitude = start.ParkingAltitude,
                TurnRate = TurnRate,
                SunGravParameter = sun.gravParameter,
            };
            if (config.Departure != null)
                config.Departure.SafeRadius = v.mainBody.Radius + (v.mainBody.atmosphere ? v.mainBody.atmosphereDepth : 0) + 10_000;

            trip = start;
            baseConfig = config;
            guidance = new FlipAndBurnGuidance(config);
            Notice = null;
            CurrentAcceleration = start.Acceleration;
            deltaVUsed = 0;
            startUT = Planetarium.GetUniversalTime();
            plannedArrivalUT = startUT + start.PlannedTripTime;
            Last = null;
            StopReason = null;
            Arrived = false;
            Replans = 0;
            FlipSeconds = double.NaN;
            flipStartUT = offCourseSince = mispointedSince = noThrustSince = weakSince = noEngineSince = cooldownUntil = double.NaN;
            bestPointing = double.MaxValue;

            record = new TripRecord
            {
                Target = start.Target.bodyName,
                Trip = start.Trip,
                Flip = start.Flip,
                ChosenAcceleration = start.Acceleration,
                Acceleration = start.Acceleration,
                ParkingAltitude = start.ParkingAltitude,
                DeltaVBudget = start.DeltaVBudget,
                StartUT = startUT,
                PlannedArrivalUT = plannedArrivalUT,
                LastFlownUT = startUT,
            };
            PausedByPlayer = false;
            PauseNotice = null;
            Resumes = 0;
            SetReserve(start.StartDeltaV - start.DeltaVNeeded, start.StartDeltaV, "the plan at Start needs it");
            Engage();
            Debug.Log($"[FlipAndBurn] trip started to {start.Target.bodyName}: {start.Trip}, {start.Acceleration / TripCalculator.OneG:F2} g, " +
                      $"{start.Flip}, parking {start.ParkingAltitude:F0} m, turn rate {TurnRate * 180 / Math.PI:F2} deg/s");
            return null;
        }

        /// <summary>Take the ship: the player's settings and warp rates saved, the trip's in, MechJeb's controls joined.</summary>
        private void Engage()
        {
            SaveSettings();
            MechJebModuleThrustController thrust = Core.Thrust;
            playerLimitAcceleration = tripLimitAcceleration = thrust.LimitAcceleration;
            playerMaxAcceleration = tripMaxAcceleration = thrust.MaxAcceleration.Val;
            swappedForSave = false;

            // The trip uses its own physics warp rates (the stock 4x top rate is far too slow for a trip). The player's are
            // put back exactly at every way a trip can end or pause: Abort, arrival, a self-stop, Pause, leaving the ship.
            playerPhysicsRates = TimeWarp.fetch != null ? WarpRatesSnapshot.Take(TimeWarp.fetch.physicsWarpRates) : null;
            tripPhysicsRates = (float[])PhysicsRates.Clone();
            if (TimeWarp.fetch != null)
            {
                TimeWarp.SetRate(0, true);
                TimeWarp.fetch.physicsWarpRates = tripPhysicsRates;
            }

            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onVesselChange.Add(OnVesselChange);

            thrust.ThrustOff();
            thrust.Tmode = MechJebModuleThrustController.TMode.OFF;
            thrust.Users.Add(this);
            Core.Attitude.Users.Add(this);
            controlling = true;
            Enabled = true;
        }

        /// <summary>Give the ship back: engines cut (left active at zero throttle), MechJeb's controls, warp rates and settings the player's.</summary>
        private void Release()
        {
            controlling = false;
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);   // onVesselChange stays: see OnVesselChange
            if (swappedForSave) AfterThrustSaved();
            // Each step on its own, so one that fails can't leave the others undone.
            Guard("cutting the engines", () =>
            {
                Core.Thrust.ThrustOff();
                CutEngines(Vessel);
                RelightEngines(throttleZero: true);   // back as they were at Start (active), but at zero throttle
            });
            Guard("releasing MechJeb's throttle and steering", () =>
            {
                Core.Thrust.Users.Remove(this);
                Core.Attitude.Users.Remove(this);
                if (Core.Attitude.Users.Count == 0) Core.Attitude.attitudeDeactivate();
            });
            Guard("putting the warp rates back", RestoreWarpRates);
            Guard("putting the MechJeb settings back", RestoreSettings);
            guidance = null;
            WarpStatus = "";
        }

        /// <summary>End the trip for good: release the ship (if flying) and forget the trip, so no save brings it back.</summary>
        public void Stop(string reason, bool arrived = false)
        {
            if (!controlling && record == null) return;
            if (controlling) Release();
            GameEvents.onVesselChange.Remove(OnVesselChange);
            record = null;
            PausedByPlayer = false;
            PauseNotice = null;
            settledSince = -1;
            StopReason = reason;
            Arrived = arrived;
            guidance = null;
            Enabled = false;
            Debug.Log("[FlipAndBurn] trip stopped: " + reason + " " + SettingsCheck);
        }

        /// <summary>
        /// Pause the trip: the ship is given back exactly as at the end of a trip (engine cut, so it coasts), and the trip is
        /// kept, with the ship, to carry on later (ADR 0004).
        /// </summary>
        /// <param name="why">Plain words, e.g. "you went to the Space Center".</param>
        /// <param name="byPlayer">The Pause button: wait for Resume while the player stays with the ship.</param>
        public void Pause(string why, bool byPlayer)
        {
            if (!controlling) return;
            // This also runs while the game shuts down, so each step on its own and nothing may throw.
            double ut = double.NaN;
            Guard("saving the trip", () =>
            {
                ut = Planetarium.GetUniversalTime();
                UpdateRecord(ut);
            });
            Release();
            PausedByPlayer = byPlayer;
            settledSince = -1;
            string target = record.Target;
            Guard("naming the target", () => target = Planner.BodyName(trip.Target));
            PauseNotice = byPlayer
                ? $"The trip to {target} is paused: the engine is off and the ship coasts. Press Resume to carry on. " +
                  "You can save, switch ships or leave now; the trip carries on when you come back to this ship."
                : $"The trip to {target} is paused ({why}): the engine is off and the ship coasts. It carries on when you come back to this ship.";
            Notice = null;
            Guard("telling the player", () => ScreenMessages.PostScreenMessage("Flip and Burn: " + PauseNotice, 10f, ScreenMessageStyle.UPPER_CENTER));
            Debug.Log($"[FlipAndBurn] trip paused ({why}) at UT {ut:F1}: delta-v used {record.DeltaVUsed:F0} m/s, holding " +
                      $"{record.Acceleration / TripCalculator.OneG:F2} g, arrival planned at UT {record.PlannedArrivalUT:F0}; {SettingsCheck}");
        }

        /// <summary>The Resume button: carry on now.</summary>
        public string PressResume()
        {
            if (!Paused) return "No trip is paused.";
            PausedByPlayer = false;
            return Resume();
        }

        // Bring the saved record up to date with the trip as it is now.
        private void UpdateRecord(double ut)
        {
            if (record == null) return;
            record.Acceleration = CurrentAcceleration;
            record.DeltaVUsed = deltaVUsed;
            record.PlannedArrivalUT = ArrivalBeingFlown(ut);
            record.LastFlownUT = ut;
            record.Replans = Replans;
            record.CutEngines = cutEngines.Where(e => e != null && e.part != null).Select(e => e.part.persistentId).Distinct().ToList();
        }

        // The arrival the trip is flying to now, as the guidance has it: a coast trip's fixed arrival, or a full burn's current
        // estimate. (The planner's estimate at Start can differ from it by up to about 1 %.)
        private double ArrivalBeingFlown(double ut)
        {
            if (guidance != null && !double.IsNaN(guidance.CoastArrivalUT)) return guidance.CoastArrivalUT;
            if (Last != null && Last.TimeToArrival > 0 && Last.Phase != GuidancePhase.Done) return ut + Last.TimeToArrival;
            return plannedArrivalUT;
        }

        private static void Guard(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FlipAndBurn] while {what}: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>Normal speed, and the player's own physics warp rates back exactly as they were at Start.</summary>
        private void RestoreWarpRates()
        {
            WarpRatesSnapshot saved = playerPhysicsRates;
            playerPhysicsRates = null;
            tripPhysicsRates = null;
            if (TimeWarp.fetch == null) return;   // the scene's TimeWarp is gone, and the trip's rates with it
            TimeWarp.SetRate(0, true);
            if (saved == null) return;
            TimeWarp.fetch.physicsWarpRates = saved.Restore();
            Debug.Log($"[FlipAndBurn] physics warp rates back to the player's {saved}: " +
                      (saved.Matches(TimeWarp.fetch.physicsWarpRates) ? "same array, same values" : "MISMATCH"));
        }

        // Leaving the flight scene (Space Center, Tracking Station, main menu, a reload): pause first, so the warp rates and
        // MechJeb settings go back while the flight scene is still there. KSP saved the game, trip included, just before.
        private void OnSceneLoadRequested(GameScenes scene)
        {
            if (!controlling) return;
            string where = scene == GameScenes.SPACECENTER ? "you went to the Space Center"
                : scene == GameScenes.TRACKSTATION ? "you went to the Tracking Station"
                : scene == GameScenes.MAINMENU ? "you went to the main menu"
                : scene == GameScenes.FLIGHT ? "the game was reloaded"
                : "the game left the flight scene";
            Pause(where, byPlayer: false);
        }

        // Switching to another ship: this one's trip pauses (only the active ship can fly a trip). A trip paused with the
        // Pause button stops waiting for Resume once the player leaves: it carries on when they come back, like any other.
        private void OnVesselChange(Vessel now)
        {
            if (now == Vessel) return;
            if (controlling) Pause("you switched to " + (now != null ? now.vesselName : "another ship"), byPlayer: false);
            else if (record != null) PausedByPlayer = false;
        }

        protected override void OnModuleDisabled()
        {
            if (controlling || record != null) Stop("Flip and Burn was switched off.");
        }

        public override void OnDestroy()
        {
            // The ship unloading, or the game quitting: pause, so the trip carries on from the last save.
            if (controlling) Pause("the ship was unloaded", byPlayer: false);
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            base.OnDestroy();
        }

        // ------------------------------------------------------------------ saved with the ship (ADR 0004)

        public override void OnStart(PartModule.StartState state)
        {
            base.OnStart(state);
            if (record != null && !controlling) Enabled = true;   // a trip loaded with the ship: watch for the moment to resume
        }

        public override void OnLoad(ConfigNode local, ConfigNode type, ConfigNode global)
        {
            base.OnLoad(local, type, global);
            // Only a fresh module picks a trip up: MechJeb also reloads its own capture of a module's settings.
            if (local == null || !local.HasNode(TripNodeName) || record != null || controlling) return;
            ConfigNode node = local.GetNode(TripNodeName);
            TripRecord r = TripRecord.FromValues(node.values.Cast<ConfigNode.Value>().Select(v => new KeyValuePair<string, string>(v.name, v.value)), out string problem);
            if (r == null)
            {
                StopReason = "Flip and Burn couldn't carry on the trip saved with this ship: " + problem + ". Plan it again here.";
                Debug.LogWarning("[FlipAndBurn] saved trip not read: " + problem);
                return;
            }

            record = r;
            PausedByPlayer = false;
            PauseNotice = $"The trip to {r.Target} was loaded with the ship; it carries on in a moment.";
            settledSince = -1;
            Debug.Log($"[FlipAndBurn] saved trip loaded: to {r.Target}, {r.Trip}, {r.Acceleration / TripCalculator.OneG:F2} g, last flown at UT {r.LastFlownUT:F1}, " +
                      $"delta-v used {r.DeltaVUsed:F0} m/s, arrival planned at UT {r.PlannedArrivalUT:F0}");
        }

        public override void OnSave(ConfigNode local, ConfigNode type, ConfigNode global)
        {
            base.OnSave(local, type, global);
            // The thrust controller has saved by now (it runs first); give the trip its acceleration limit back.
            if (swappedForSave) AfterThrustSaved();
            if (local == null || record == null) return;
            if (controlling) UpdateRecord(Planetarium.GetUniversalTime());
            ConfigNode node = local.AddNode(TripNodeName);
            foreach (KeyValuePair<string, string> kv in record.ToValues()) node.AddValue(kv.Key, kv.Value);
        }

        /// <summary>
        /// Just before MechJeb saves the thrust controller: put the player's acceleration limit in, so no MechJeb settings
        /// file or saved game ever records the trip's (the only MechJeb settings the trip changes).
        /// </summary>
        internal void BeforeThrustSaves()
        {
            if (!controlling || swappedForSave) return;
            MechJebModuleThrustController thrust = Core.Thrust;
            tripLimitAcceleration = thrust.LimitAcceleration;
            tripMaxAcceleration = thrust.MaxAcceleration.Val;
            thrust.LimitAcceleration = playerLimitAcceleration;
            thrust.MaxAcceleration.Val = playerMaxAcceleration;
            swappedForSave = true;
        }

        private void AfterThrustSaved()
        {
            if (!swappedForSave) return;
            swappedForSave = false;
            MechJebModuleThrustController thrust = Core.Thrust;
            thrust.LimitAcceleration = tripLimitAcceleration;
            thrust.MaxAcceleration.Val = tripMaxAcceleration;
        }

        // The two MechJeb controllers Flip and Burn borrows, saved and loaded with MechJeb's own routine.
        private IEnumerable<ComputerModule> Borrowed() => new ComputerModule[] { Core.Thrust, Core.Attitude };

        private void SaveSettings()
        {
            savedLocal = new ConfigNode("local");
            savedType = new ConfigNode("type");
            savedGlobal = new ConfigNode("global");
            foreach (ComputerModule m in Borrowed())
            {
                string n = m.GetType().Name;
                m.OnSave(savedLocal.AddNode(n), savedType.AddNode(n), savedGlobal.AddNode(n));
            }
        }

        private void RestoreSettings()
        {
            if (savedLocal == null) return;
            foreach (ComputerModule m in Borrowed())
            {
                string n = m.GetType().Name;
                m.OnLoad(savedLocal.GetNode(n), savedType.GetNode(n), savedGlobal.GetNode(n));
            }

            // Check: saving again gives exactly what was saved at the start.
            ConfigNode l = new ConfigNode("local"), t = new ConfigNode("type"), g = new ConfigNode("global");
            foreach (ComputerModule m in Borrowed())
            {
                string n = m.GetType().Name;
                m.OnSave(l.AddNode(n), t.AddNode(n), g.AddNode(n));
            }

            bool same = l.ToString() == savedLocal.ToString() && t.ToString() == savedType.ToString() && g.ToString() == savedGlobal.ToString();
            SettingsCheck = same ? "MechJeb settings restored: identical to before the trip." : "MechJeb settings restored, but they differ from before the trip!";
        }

        /// <summary>
        /// Average turn rate for a half turn, from MechJeb's torque and moment of inertia: a bang-bang half turn with angular
        /// acceleration alpha takes 2 sqrt(pi / alpha), so the average rate is sqrt(pi alpha) / 2. Capped at 10 deg/s.
        /// </summary>
        private double EstimateTurnRate()
        {
            VesselState vs = Core.VesselState;
            double alpha = double.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                if (i == 1) continue;   // roll axis: turning doesn't need it
                double moi = vs.MoI[i], torque = vs.torqueAvailable[i];
                if (moi > 0 && torque > 0) alpha = Math.Min(alpha, torque / moi);
            }

            if (alpha == double.MaxValue || !(alpha > 0)) return 1 * Math.PI / 180;
            return Math.Min(10 * Math.PI / 180, Math.Sqrt(Math.PI * alpha) / 2);
        }

        // ------------------------------------------------------------------ every physics step

        public override void OnFixedUpdate()
        {
            if (swappedForSave) AfterThrustSaved();   // safety net: a save that never reached this module's OnSave
            if (!controlling)
            {
                if (record != null) WatchToResume();
                return;
            }

            if (!Running) return;
            Vessel v = Vessel;
            double ut = Planetarium.GetUniversalTime();
            StateVector state = KspShip.HeliocentricState(v, ut);
            Vector3d facingWorld = v.GetTransform().up;
            var input = new GuidanceInput
            {
                UT = ut,
                Position = state.Position,
                Velocity = state.Velocity,
                Facing = ToOrbitFrame(facingWorld),
            };

            GuidanceCommand cmd;
            try
            {
                cmd = guidance.Update(input);
            }
            catch (Exception e)
            {
                Debug.LogError("[FlipAndBurn] guidance failed: " + e);
                Stop("Flip and Burn hit an internal error and stopped. The details are in KSP.log.");
                return;
            }

            Last = cmd;
            MeasuredAcceleration = v.geeForce_immediate * PhysicsGlobals.GravitationalAcceleration;
            MeasureHottestPart(v);
            Vector3d want = ToWorld(cmd.Direction);
            PointingError = Vector3d.Angle(facingWorld, want);
            if (announceResume) AnnounceResume(ut, cmd);

            if (cmd.Phase == GuidancePhase.Done)
            {
                Stop("Arrived: the ship is in its parking orbit.", arrived: true);
                return;
            }

            // Point.
            Core.Attitude.attitudeTo(want, AttitudeReference.INERTIAL, this);

            // Thrust: MechJeb's acceleration limit holds the acceleration. It reads each engine's live maximum thrust, which
            // KSP Interstellar lowers when power or cooling falls short, so the throttle follows without any extra trim.
            bool flipping = cmd.Phase == GuidancePhase.Flip;
            bool thrustWanted = cmd.Acceleration > 0;
            bool pointed = flipping || PointingError <= PointingTolerance;
            MechJebModuleThrustController thrust = Core.Thrust;
            if (Vessel.ctrlState.mainThrottle > 0) deltaVUsed += MeasuredAcceleration * TimeWarp.fixedDeltaTime;
            EngineCommand engines = EngineCommand.Decide(cmd.Acceleration, flipping, PointingError, PointingTolerance, FullThrustAcceleration(v));
            switch (engines.Action)
            {
                case EngineAction.Burn:
                    thrust.LimitAcceleration = true;
                    thrust.MaxAcceleration.Val = cmd.Acceleration;
                    thrust.TargetThrottle = 1f;
                    SetEngineThrottles(v, engines.Throttle);
                    break;
                case EngineAction.HoldAtZero:   // turning to the burn direction: no thrust until pointed
                    thrust.TargetThrottle = 0f;
                    SetEngineThrottles(v, 0f);
                    break;
                default:
                    thrust.TargetThrottle = 0f;
                    CutEngines(v);
                    break;
            }

            if (thrustWanted) RelightEngines();

            Watchdog(cmd, ut, thrustWanted, pointed, flipping);
            if (!Running) return;
            Warp(cmd);
        }

        // ------------------------------------------------------------------ resuming (ADR 0004)

        // A paused trip on the ship the player is flying: once the ship has settled, carry on.
        private void WatchToResume()
        {
            Vessel v = Vessel;
            bool here = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && v != null && v == FlightGlobals.ActiveVessel && v.loaded && !v.packed &&
                        TimeWarp.fetch != null;
            if (!here || PausedByPlayer)
            {
                settledSince = -1;
                return;
            }

            // Engines at zero until the trip has re-planned and the ship points where it should: a save made during a burn
            // keeps the ship's throttle, and KSP gives it back when the ship loads.
            HoldEnginesAtZero(v);
            if (settledSince < 0)
            {
                settledSince = Time.realtimeSinceStartup;
                RelightSavedCutEngines(v);
                return;
            }

            if (Time.realtimeSinceStartup - settledSince < ResumeSettleSeconds) return;
            settledSince = -1;
            Resume();
        }

        private static void HoldEnginesAtZero(Vessel v)
        {
            v.ctrlState.mainThrottle = 0;
            if (v == FlightGlobals.ActiveVessel) FlightInputHandler.state.mainThrottle = 0;
            SetEngineThrottles(v, 0f);
        }

        // Engines the trip had shut down when it was saved (a cut, the coast): relight them at zero throttle, as at Start.
        private void RelightSavedCutEngines(Vessel v)
        {
            if (record == null || record.CutEngines.Count == 0) return;
            foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
                if (e.part != null && record.CutEngines.Contains(e.part.persistentId) && !cutEngines.Contains(e)) cutEngines.Add(e);
            RelightEngines(throttleZero: true);
        }

        /// <summary>
        /// Carry a paused trip on: re-plan from where the ship is now with the tests the planner applies before Start, and
        /// either fly on, telling the player how long they were away and what that did to the arrival, or stop with the
        /// reason and nothing fired. Returns the stop reason, or null.
        /// </summary>
        private string Resume()
        {
            if (record == null || controlling) return "No trip to resume.";
            Vessel v = Vessel;
            double ut = Planetarium.GetUniversalTime();
            CelestialBody target = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == record.Target);
            if (target == null)
            {
                Stop($"Flip and Burn couldn't carry on the trip saved with this ship: {record.Target} isn't in this game. Plan a new trip here.");
                return StopReason;
            }

            string name = Planner.BodyName(target);
            double away = Math.Max(0, ut - record.LastFlownUT);
            ShipPerformance ship = null;
            if (!CheatOptions.InfinitePropellant)
            {
                ShipReading reading = ShipPerformanceReader.Read(v, out string engineProblem);
                if (engineProblem == null && reading.Report.NoThrust) engineProblem = "none of the active engines can push the ship in space";
                if (engineProblem != null)
                {
                    RefuseResume(name, engineProblem.TrimEnd('.'));
                    return StopReason;
                }

                ship = reading.Report.Performance;
            }

            TargetPlanet departure = DepartureFor(v, target);
            TripRequest request = RequestFrom(v, ut, target, departure, record.Acceleration, record.ParkingAltitude);
            double keep = record.Trip == TripType.CoastInTheMiddle ? record.PlannedArrivalUT : double.NaN;
            double startDeltaV = StartDeltaVOf(record);
            ReplanResult check = TripReplanner.Check(request, record.Trip, record.DeltaVLeft, ship, Planner.Reserve, keep, startDeltaV);
            Debug.Log($"[FlipAndBurn] resume check: away {away:F1} s; {record.Trip} to {record.Target} at {record.Acceleration / TripCalculator.OneG:F2} g; " +
                      $"{check.Problem}; {(check.KeptArrival ? "keeps the arrival it was flying to" : "fastest plan from here")}; remaining {check.RemainingTime:F0} s; delta-v needed {check.DeltaV:F0} m/s, available {check.DeltaVAvailable:F0} m/s; " +
                      $"budget left {record.DeltaVLeft:F0} m/s, used {record.DeltaVUsed:F0} m/s; expected reserve at arrival {check.ReserveAtArrival:F0} m/s " +
                      $"({check.ReserveShare * 100:F2}% of {startDeltaV:F0}; if the arrival were kept {check.ReserveIfKept:F0} m/s); infinite propellant {CheatOptions.InfinitePropellant}");
            if (!check.Feasible)
            {
                RefuseResume(name, ProblemText(check, target, record.Acceleration, record.Trip));
                return StopReason;
            }

            TurnRate = EstimateTurnRate();
            trip = new TripStart
            {
                Target = target, Acceleration = record.ChosenAcceleration, Flip = record.Flip, Trip = record.Trip,
                ParkingAltitude = record.ParkingAltitude, DeltaVBudget = record.DeltaVBudget, PlannedTripTime = record.PlannedArrivalUT - record.StartUT,
            };
            var config = new GuidanceConfig
            {
                Target = request.Target,
                Departure = departure,
                Acceleration = record.Acceleration,
                Trip = record.Trip,
                DeltaVBudget = record.Trip == TripType.CoastInTheMiddle ? check.DeltaVAvailable : record.DeltaVLeft,
                Flip = record.Flip,
                ParkingAltitude = record.ParkingAltitude,
                TurnRate = TurnRate,
                SunGravParameter = Planetarium.fetch.Sun.gravParameter,
                // Fly exactly what the re-plan found: the arrival kept, or the fastest from here.
                CoastArrivalUT = check.Coast != null ? check.Coast.ArrivalUT : double.NaN,
            };

            baseConfig = config;
            guidance = new FlipAndBurnGuidance(config);
            CurrentAcceleration = record.Acceleration;
            deltaVUsed = record.DeltaVUsed;
            startUT = record.StartUT;
            Replans = record.Replans;
            double oldArrival = record.PlannedArrivalUT;
            plannedArrivalUT = ut + check.RemainingTime;
            Last = null;
            StopReason = null;
            Arrived = false;
            FlipSeconds = double.NaN;
            flipStartUT = offCourseSince = mispointedSince = noThrustSince = weakSince = noEngineSince = cooldownUntil = double.NaN;
            bestPointing = double.MaxValue;
            PausedByPlayer = false;
            PauseNotice = null;

            SetReserve(check.ReserveAtArrival, startDeltaV, check.KeptArrival
                ? "keeping the arrival it was flying to after the pause uses a sliver of it"
                : "the ship has less delta-v left than the trip planned for");
            Engage();
            Resumes++;
            LastAwaySeconds = away;
            LastResumeOldArrivalUT = oldArrival;
            LastResumeNewArrivalUT = double.NaN;
            // Told on the first guidance step, so the new arrival is the guidance's own, as the old one was.
            announceResume = true;
            resumeKeptArrival = check.KeptArrival;
            Notice = $"Trip resumed after {AwayText(away)} away; re-planning from where the ship is now.";
            MechJebModuleFlipAndBurn window = Core.GetComputerModule<MechJebModuleFlipAndBurn>();
            if (window != null && !window.Enabled) window.Enabled = true;   // show the player the trip is back
            Debug.Log($"[FlipAndBurn] trip resumed to {record.Target} after {away:F1} s away: re-planned {check.RemainingTime:F0} s to go " +
                      $"({(check.KeptArrival ? "keeping the arrival it was flying to" : "the fastest from here")}); {record.Trip} at " +
                      $"{record.Acceleration / TripCalculator.OneG:F2} g, delta-v used {deltaVUsed:F0} m/s, budget for the rest {config.DeltaVBudget:F0} m/s; " +
                      $"turn rate {TurnRate * 180 / Math.PI:F2} deg/s");
            return null;
        }

        private bool announceResume, resumeKeptArrival;

        private static string AwayText(double away) => away < 60 ? $"{away:0} s" : Planner.FormatTime(away);

        // The resume message, on the first guidance step after a resume: how long the player was away, and what that did to
        // the arrival, in plain words.
        private void AnnounceResume(double ut, GuidanceCommand cmd)
        {
            announceResume = false;
            double newArrival = ArrivalBeingFlown(ut), oldArrival = LastResumeOldArrivalUT, away = LastAwaySeconds;
            plannedArrivalUT = newArrival;
            LastResumeNewArrivalUT = newArrival;
            UpdateRecord(ut);
            double change = newArrival - oldArrival;
            string why = away >= 60 ? $": the engine was off for {AwayText(away)}" : ": the new plan from where the ship is now needs it";
            string changeText = Math.Abs(change) < 60
                ? "about when it was planned before"
                : $"{Planner.FormatTime(Math.Abs(change))} {(change > 0 ? "later" : "earlier")} than planned before{(change > 0 ? why : "")}";
            Notice = $"Trip resumed after {AwayText(away)} away. Arrival in {Planner.FormatTime(newArrival - ut)}, {changeText}. " +
                     "The ship turns to its burn direction before the engine fires.";
            ScreenMessages.PostScreenMessage("Flip and Burn: " + Notice, 15f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[FlipAndBurn] resumed trip's arrival: UT {newArrival:F0}, was {oldArrival:F0} ({change:+0;-0} s), " +
                      $"{(resumeKeptArrival ? "kept" : "re-planned")}; away {away:F1} s; first command {cmd.Phase}, pointing error {PointingError:F1} deg");
        }

        private void RefuseResume(string target, string why)
        {
            Stop($"Flip and Burn couldn't carry on the trip to {target}: {why}. Nothing was fired. Plan a new trip here: the window shows what this " +
                 "ship can still do from where it is (a lower acceleration or a coast trip may still work).");
            ScreenMessages.PostScreenMessage("Flip and Burn: " + StopReason, 15f, ScreenMessageStyle.UPPER_CENTER);
        }

        // The planet the ship is leaving, if it is inside one's sphere of influence (not the target's), as at Start.
        private static TargetPlanet DepartureFor(Vessel v, CelestialBody target)
        {
            CelestialBody body = v.mainBody;
            if (body == Planetarium.fetch.Sun || body == target) return null;
            TargetPlanet departure = Planner.AsPlanet(body);
            departure.SafeRadius = body.Radius + (body.atmosphere ? body.atmosphereDepth : 0) + 10_000;
            return departure;
        }

        private static TripRequest RequestFrom(Vessel v, double ut, CelestialBody target, TargetPlanet departure, double acceleration, double parkingAltitude)
        {
            StateVector state = KspShip.HeliocentricState(v, ut);
            return new TripRequest
            {
                ShipPosition = state.Position,
                ShipVelocity = state.Velocity,
                StartTime = ut,
                Target = Planner.AsPlanet(target),
                Departure = departure,
                Acceleration = acceleration,
                ParkingAltitude = parkingAltitude,
                SunGravParameter = Planetarium.fetch.Sun.gravParameter,
            };
        }

        // A re-plan's problem in plain words.
        private static string ProblemText(ReplanResult r, CelestialBody target, double acceleration, TripType tripType)
        {
            string g = Planner.FormatG(acceleration / TripCalculator.OneG), name = Planner.BodyName(target);
            switch (r.Problem)
            {
                case ReplanProblem.TargetGravityTooStrong:
                    return $"{name}'s gravity at the parking orbit needs at least {Planner.FormatG(r.MinimumAcceleration / TripCalculator.OneG)}, more than {g}";
                case ReplanProblem.CantHoldAcceleration:
                    return $"the engines can now reach only {Planner.FormatG(r.MaxAcceleration / TripCalculator.OneG)}, less than {g}";
                case ReplanProblem.NotEnoughDeltaV:
                    return tripType == TripType.CoastInTheMiddle
                        ? $"the delta-v left ({Planner.FormatSpeed(r.DeltaVAvailable)}, keeping the reserve) isn't enough for a coast trip to {name} from here at {g}"
                        : $"the rest of the trip needs {Planner.FormatSpeed(r.DeltaV)} of delta-v and the ship has {Planner.FormatSpeed(r.DeltaVAvailable)} at {g}";
                case ReplanProblem.SunInTheWay:
                    return $"the Sun is now between the ship and {name}, and a coast trip can't go around it yet";
                default:
                    return $"there's no way to reach {name} from here at {g}";
            }
        }

        // Stop with a plain reason when something goes wrong.
        private void Watchdog(GuidanceCommand cmd, double ut, bool thrustWanted, bool pointed, bool flipping)
        {
            // No engine at all (one was destroyed, or shut down): stop, whatever the cheats.
            bool enginesAlive = Vessel.FindPartModulesImplementing<ModuleEngines>().Any(e => e.EngineIgnited && !e.flameout);
            if (thrustWanted && !enginesAlive)
            {
                if (double.IsNaN(noEngineSince)) noEngineSince = ut;
                if (ut - noEngineSince > 5)
                {
                    Stop("Stopped: no engine is running. If an engine was destroyed (overheating?), check the ship before flying on.");
                    return;
                }
            }
            else noEngineSince = double.NaN;

            // A part close to its temperature limit: ease off before it fails.
            if (thrustWanted && !flipping && HottestFraction > OverheatingPart && double.IsNaN(cooldownUntil))
            {
                cooldownUntil = ut + 60;
                ReplanAtLowerAcceleration(ut, 0.8 * CurrentAcceleration,
                    $"{HottestPart} reached {HottestFraction * 100:N0}% of its temperature limit");
                return;
            }

            if (!double.IsNaN(cooldownUntil) && ut > cooldownUntil) cooldownUntil = double.NaN;

            // Out of fuel: thrust wanted, ship pointed, throttle open, and the engines give almost nothing.
            bool infinite = CheatOptions.InfinitePropellant;
            if (thrustWanted && pointed && !flipping && (!enginesAlive || MeasuredAcceleration < 0.1 * cmd.Acceleration) && !infinite)
            {
                if (double.IsNaN(noThrustSince)) noThrustSince = ut;
                if (ut - noThrustSince > 10)
                {
                    Stop(enginesAlive
                        ? "Stopped: the engines stopped producing thrust (out of fuel?)."
                        : "Stopped: out of fuel (no engine is running).");
                    return;
                }
            }
            else noThrustSince = double.NaN;

            // Can't hold the acceleration: at full throttle the engines give clearly less than asked, for a minute, and it
            // isn't a fuel problem (that's caught above). KSP Interstellar drives lose thrust when the ship overheats: the
            // Bebop Refueller settles at 0.80 g when asked for 1 g. Drop to what the engines can sustain and re-plan (ADR 0002).
            bool fullThrottle = Vessel.ctrlState.mainThrottle >= 0.99f;
            if (thrustWanted && pointed && !flipping && fullThrottle && MeasuredAcceleration < 0.95 * cmd.Acceleration &&
                MeasuredAcceleration >= 0.1 * cmd.Acceleration)
            {
                if (double.IsNaN(weakSince)) weakSince = ut;
                if (ut - weakSince > 60)
                {
                    double heat = ResourceFill("WasteHeat");
                    ReplanAtLowerAcceleration(ut, 0.95 * MeasuredAcceleration, heat > 0.9
                        ? $"the ship is overheating (waste heat at {heat * 100:N0}%; KSP Interstellar drives lose thrust above 90%)"
                        : "they give less thrust than expected");
                    return;
                }
            }
            else weakSince = double.NaN;

            // Turning: the flip, or holding the direction, takes far longer than the ship's turn rate allows.
            double halfTurn = Math.PI / Math.Max(1e-3, TurnRate);
            if (flipping)
            {
                if (double.IsNaN(flipStartUT)) flipStartUT = ut;
                if (ut - flipStartUT > Math.Max(120, 4 * halfTurn))
                {
                    Stop($"Stopped: the ship can't turn fast enough. The flip has taken {ut - flipStartUT:N0} s; " +
                         $"it should take about {halfTurn:N0} s.");
                    return;
                }
            }
            else if (!double.IsNaN(flipStartUT) && double.IsNaN(FlipSeconds))
            {
                FlipSeconds = ut - flipStartUT;
                Debug.Log($"[FlipAndBurn] flip done in {FlipSeconds:F1} s (planned turn {halfTurn:F1} s)");
            }

            if (thrustWanted && !flipping && PointingError > 2 * PointingTolerance)
            {
                // Only while it isn't getting any closer: a heavy ship turning slowly but steadily is fine.
                if (double.IsNaN(mispointedSince) || PointingError < bestPointing - 1)
                {
                    mispointedSince = ut;
                    bestPointing = PointingError;
                }

                if (ut - mispointedSince > Math.Max(60, 3 * halfTurn))
                {
                    Stop($"Stopped: the ship can't hold its direction (still {PointingError:N0} degrees off, no closer for {ut - mispointedSince:N0} s).");
                    return;
                }
            }
            else
            {
                mispointedSince = double.NaN;
                bestPointing = double.MaxValue;
            }

            // Off course: no way to the target from here, or the arrival keeps slipping.
            double arrival = ut + cmd.TimeToArrival;
            bool slipping = cmd.Phase != GuidancePhase.Coast && cmd.TimeToArrival > 0 && cmd.Leg == cmd.LegCount &&
                            arrival - plannedArrivalUT > Math.Max(3600, 0.1 * (plannedArrivalUT - startUT));
            if (cmd.NoSolution || slipping)
            {
                if (double.IsNaN(offCourseSince)) offCourseSince = ut;
                if (ut - offCourseSince > 10)
                {
                    Stop(cmd.NoSolution
                        ? "Stopped: the ship has drifted too far off course; Flip and Burn can't find a way to the target from here."
                        : $"Stopped: the ship has drifted too far off course; arrival has slipped by {Planner.FormatTime(arrival - plannedArrivalUT)}.");
                }
            }
            else offCourseSince = double.NaN;
        }

        /// <summary>
        /// The engines can't hold the acceleration: fly the rest of the trip at what they can sustain (95 % of what they give
        /// at full throttle, rounded down to 0.01 g), re-planned from here, and tell the player. Stops only if the trip can't
        /// be finished at that acceleration.
        /// </summary>
        private void ReplanAtLowerAcceleration(double ut, double sustainable, string why)
        {
            double heat = ResourceFill("WasteHeat");
            double was = CurrentAcceleration;
            double now = Math.Floor(sustainable / TripCalculator.OneG * 100) / 100 * TripCalculator.OneG;
            weakSince = double.NaN;
            if (!(now > 0))
            {
                Stop($"Stopped: the engines can't hold any useful acceleration ({why}).");
                return;
            }

            // Can the rest of the trip be flown at that acceleration? (The fuel isn't re-checked here: the trip was checked at
            // Start, and a lower acceleration needs less of it.)
            Vessel v = Vessel;
            TargetPlanet departure = DepartureFor(v, trip.Target);
            TripRequest request = RequestFrom(v, ut, trip.Target, departure, now, trip.ParkingAltitude);
            double budget = Math.Max(0, trip.DeltaVBudget - deltaVUsed);
            double startDeltaV = record != null ? StartDeltaVOf(record) : double.NaN;
            ReplanResult check = TripReplanner.Check(request, trip.Trip, budget, null, Planner.Reserve, double.NaN, startDeltaV);
            string g = Planner.FormatG(now / TripCalculator.OneG), gWas = Planner.FormatG(was / TripCalculator.OneG);
            if (!check.Feasible)
            {
                Stop($"Stopped: the engines could only hold {g} ({why}), and the trip can't be finished at {g}: {ProblemText(check, trip.Target, now, trip.Trip)}.");
                return;
            }

            double remaining = check.RemainingTime;
            double newArrival = ut + remaining;
            double change = newArrival - plannedArrivalUT;
            GuidanceConfig c = Copy(baseConfig);
            c.Acceleration = now;
            c.Departure = departure;
            c.DeltaVBudget = budget;
            c.CoastArrivalUT = check.Coast != null ? check.Coast.ArrivalUT : double.NaN;
            if (record != null) record.Acceleration = now;
            guidance = new FlipAndBurnGuidance(c);
            CurrentAcceleration = now;
            plannedArrivalUT = newArrival;
            SetReserve(check.ReserveAtArrival, startDeltaV, "the rest of the trip at the lower acceleration needs more of it");
            Replans++;
            Notice = $"The engines couldn't safely hold {gWas}: {why}. Flip and Burn is now holding {g} and has re-planned the rest " +
                     $"of the trip: arrival is {Planner.FormatTime(Math.Abs(change))} {(change >= 0 ? "later" : "earlier")} " +
                     $"({Planner.FormatTime(remaining)} from now).";
            ScreenMessages.PostScreenMessage("Flip and Burn: " + Notice, 15f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log($"[FlipAndBurn] re-planned at {now / TripCalculator.OneG:F2} g (was {was / TripCalculator.OneG:F2} g, measured " +
                      $"{MeasuredAcceleration / TripCalculator.OneG:F4} g at full throttle, waste heat {heat * 100:F1}%): trip {remaining:F0} s from now, " +
                      $"arrival {change:+0;-0} s; delta-v used {deltaVUsed:F0} m/s. {Notice}");
        }

        private static GuidanceConfig Copy(GuidanceConfig c) => new GuidanceConfig
        {
            Target = c.Target, Departure = c.Departure, Acceleration = c.Acceleration, Trip = c.Trip, DeltaVBudget = c.DeltaVBudget,
            Flip = c.Flip, ParkingAltitude = c.ParkingAltitude, TurnRate = c.TurnRate, SunGravParameter = c.SunGravParameter,
            SunSafetyDistance = c.SunSafetyDistance, EquatorNormal = c.EquatorNormal, RelightToleranceDeg = c.RelightToleranceDeg,
            ArrivalCutoff = c.ArrivalCutoff, DetourMargin = c.DetourMargin, BrakingFraction = c.BrakingFraction,
            CorrectionThreshold = c.CorrectionThreshold, MinThrustFraction = c.MinThrustFraction, CoastArrivalUT = c.CoastArrivalUT,
        };

        private void MeasureHottestPart(Vessel v)
        {
            double hottest = 0;
            string name = "";
            if (CheatOptions.IgnoreMaxTemperature)
            {
                // KSP's "ignore max temperature" cheat: no part can fail from heat, so heat needn't hold anything back.
                HottestFraction = 0;
                HottestPart = "";
                return;
            }

            foreach (Part p in v.parts)
            {
                double f = Math.Max(p.maxTemp > 0 ? p.temperature / p.maxTemp : 0, p.skinMaxTemp > 0 ? p.skinTemperature / p.skinMaxTemp : 0);
                if (f > hottest)
                {
                    hottest = f;
                    name = p.partInfo?.title ?? p.name;
                }
            }

            HottestFraction = hottest;
            HottestPart = name;
        }

        // Engines Flip and Burn shut down for a cut, to relight afterwards.
        private readonly List<ModuleEngines> cutEngines = new List<ModuleEngines>();

        /// <summary>
        /// Cut means cut: zero each running engine's throttle and shut it down. In the game the Kerbstein drive kept
        /// pushing 1 g through a whole "cut" flip, and after an Abort, with the ship's throttle at zero: its requested
        /// throttle stayed at the old value after a long warped cruise. Shutting the engine down is what KSP Interstellar's
        /// own safety cut-off does.
        /// </summary>
        private void CutEngines(Vessel v)
        {
            foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited) continue;
                e.requestedThrottle = 0f;
                e.currentThrottle = 0f;
                try
                {
                    e.Shutdown();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[FlipAndBurn] couldn't shut down " + e.part.partInfo?.title + ": " + ex.Message);
                }

                if (!cutEngines.Contains(e)) cutEngines.Add(e);
            }
        }

        /// <summary>
        /// Set each running engine's own throttle to what MechJeb's acceleration limit asks for (commanded acceleration over
        /// the full-thrust acceleration). Normally the engine copies the ship's throttle into its own each step, but in the
        /// game the Kerbstein drive stopped doing that after a long warped cruise and after a relight, and kept its old
        /// throttle: so Flip and Burn sets it directly as well.
        /// </summary>
        private static List<ModuleEngines> RunningEngines(Vessel v) =>
            v.FindPartModulesImplementing<ModuleEngines>().Where(e => e.EngineIgnited && !e.flameout).ToList();

        /// <summary>All running engines at 100 % over the ship's mass (m/s^2).</summary>
        private static double FullThrustAcceleration(Vessel v)
        {
            double mass = v.totalMass * 1000;
            double full = RunningEngines(v).Sum(e => e.maxThrust * e.thrustPercentage / 100.0) * 1000;
            return mass > 0 ? full / mass : 0;
        }

        private static void SetEngineThrottles(Vessel v, float throttle)
        {
            foreach (ModuleEngines e in RunningEngines(v))
            {
                e.requestedThrottle = throttle;
                e.currentThrottle = throttle;
            }
        }

        /// <summary>Relight the engines Flip and Burn cut.</summary>
        private void RelightEngines(bool throttleZero = false)
        {
            if (cutEngines.Count == 0) return;
            foreach (ModuleEngines e in cutEngines)
            {
                if (e == null || e.part == null || e.EngineIgnited) continue;
                try
                {
                    e.Activate();
                    e.part.force_activate();   // as KSP Interstellar does, so the part runs again
                    if (throttleZero)
                    {
                        e.requestedThrottle = 0f;
                        e.currentThrottle = 0f;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[FlipAndBurn] couldn't relight " + e.part.partInfo?.title + ": " + ex.Message);
                }
            }

            cutEngines.Clear();
        }

        /// <summary>How full the ship's store of a resource is, 0 to 1 (0 if it has none).</summary>
        private double ResourceFill(string resource)
        {
            double amount = 0, max = 0;
            foreach (Part p in Vessel.parts)
            {
                PartResource r = p.Resources.Get(resource);
                if (r == null) continue;
                amount += r.amount;
                max += r.maxAmount;
            }

            return max > 0 ? amount / max : 0;
        }

        // ------------------------------------------------------------------ time warp

        private void Warp(GuidanceCommand cmd)
        {
            TimeWarp tw = TimeWarp.fetch;
            if (tw == null) return;
            if (tripPhysicsRates != null && !ReferenceEquals(tw.physicsWarpRates, tripPhysicsRates))
            {
                // Something else handed the game new physics rates during the trip (BetterTimeWarp sets its preset when a
                // scene's GUI is ready, or the player picks another): those are now the player's to get back.
                playerPhysicsRates = WarpRatesSnapshot.Take(tw.physicsWarpRates);
                if (TimeWarp.CurrentRateIndex != 0) TimeWarp.SetRate(0, true);
                tw.physicsWarpRates = tripPhysicsRates;
                Debug.Log($"[FlipAndBurn] physics warp rates were replaced during the trip; the player's are now {playerPhysicsRates}");
            }

            WarpDecision d = WarpPolicy.Decide(cmd, PointingError <= 2);
            bool wantPhysics = d.Mode == WarpMode.Physics;
            bool wantRails = d.Mode == WarpMode.Rails;
            float[] rates = wantRails ? tw.warpRates : tw.physicsWarpRates;
            double cap = wantPhysics ? MaxPhysicsRate : double.MaxValue;
            string heatNote = "";
            if (wantPhysics && HottestFraction > HotPart)
            {
                cap = 1;
                heatNote = $"; held at normal speed: {HottestPart} at {HottestFraction * 100:N0}% of its temperature limit";
            }
            else if (wantPhysics && HottestFraction > WarmPart)
            {
                cap = Math.Min(cap, 4);
                heatNote = $"; held to x4: {HottestPart} at {HottestFraction * 100:N0}% of its temperature limit";
            }

            int index = d.Mode == WarpMode.None ? 0 : WarpPolicy.PickIndex(rates, d.MaxRate, cap);
            TimeWarp.Modes mode = wantRails ? TimeWarp.Modes.HIGH : TimeWarp.Modes.LOW;

            int current = TimeWarp.CurrentRateIndex;
            bool modeOk = current == 0 || tw.Mode == mode;
            double now = Time.realtimeSinceStartup;
            bool slowingDown = !modeOk || index < current;   // always act at once when warp must come down
            if (slowingDown || now - lastWarpChange > SecondsBetweenWarpChanges)
            {
                if (!modeOk || index == 0)
                {
                    if (current != 0) TimeWarp.SetRate(0, true);
                }
                else if (index != current)
                {
                    if (current == 0 && tw.Mode != mode) tw.Mode = mode;
                    TimeWarp.SetRate(index, index < current);
                }

                lastWarpChange = now;
            }

            string kind = d.Mode == WarpMode.Physics ? "physics warp" : d.Mode == WarpMode.Rails ? "warp" : "no warp";
            float rate = TimeWarp.CurrentRate;
            WarpStatus = rate > 1.01f
                ? $"{kind} x{rate:0.#} ({d.Reason}{heatNote})"
                : $"normal speed ({d.Reason}{heatNote})";
        }

        // ------------------------------------------------------------------ frames

        // The guidance works in KSP's orbit frame (Orbit's vectors, Y and Z swapped relative to world space).
        private static Vec3 ToOrbitFrame(Vector3d world) => new Vec3(world.x, world.z, world.y);
        private static Vector3d ToWorld(Vec3 v) => new Vector3d(v.X, v.Z, v.Y);
    }
}
