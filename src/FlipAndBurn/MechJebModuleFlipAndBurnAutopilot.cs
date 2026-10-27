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
    }

    /// <summary>
    /// Flies a trip: calls the guidance every physics step, points the ship with MechJeb's attitude control, sets thrust
    /// with MechJeb's throttle and acceleration limit, and runs time warp (ADR 0003, "Flying").
    /// </summary>
    /// <remarks>
    /// MechJeb finds this module on its own, like the window. It is enabled only while a trip runs; MechJeb doesn't save
    /// that, so after the game is saved and loaded no trip is running (resuming is the next item, ADR 0003).
    ///
    /// The player's MechJeb settings for the two controllers it borrows (thrust and attitude) are saved with MechJeb's own
    /// save routine when the trip starts and loaded back the same way when it ends or is aborted.
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

        private FlipAndBurnGuidance guidance;
        private GuidanceConfig baseConfig;
        private double deltaVUsed;   // m/s burned so far (for a coast trip's re-plan)
        private TripStart trip;
        private ConfigNode savedLocal, savedType, savedGlobal;
        private float[] savedPhysicsRates;
        private double startUT, plannedArrivalUT, flipStartUT = double.NaN, offCourseSince = double.NaN, mispointedSince = double.NaN,
            noThrustSince = double.NaN, weakSince = double.NaN, noEngineSince = double.NaN, cooldownUntil = double.NaN, lastWarpChange, bestPointing = double.MaxValue;
        private bool controlling;

        public MechJebModuleFlipAndBurnAutopilot(MechJebCore core) : base(core)
        {
            Priority = 250;   // before the thrust controller (200) in each physics step
        }

        public bool Running => Enabled && guidance != null;
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

            SaveSettings();
            savedPhysicsRates = TimeWarp.fetch != null ? (float[])TimeWarp.fetch.physicsWarpRates.Clone() : null;
            if (TimeWarp.fetch != null)
            {
                TimeWarp.SetRate(0, true);
                TimeWarp.fetch.physicsWarpRates = (float[])PhysicsRates.Clone();
            }

            Core.Thrust.ThrustOff();
            Core.Thrust.Tmode = MechJebModuleThrustController.TMode.OFF;
            Core.Thrust.Users.Add(this);
            Core.Attitude.Users.Add(this);
            controlling = true;
            Enabled = true;
            Debug.Log($"[FlipAndBurn] trip started to {start.Target.bodyName}: {start.Trip}, {start.Acceleration / TripCalculator.OneG:F2} g, " +
                      $"{start.Flip}, parking {start.ParkingAltitude:F0} m, turn rate {TurnRate * 180 / Math.PI:F2} deg/s");
            return null;
        }

        /// <summary>Cut the throttle, stop warp, release control, put the player's MechJeb settings back.</summary>
        public void Stop(string reason, bool arrived = false)
        {
            if (!controlling) return;
            controlling = false;
            try
            {
                Core.Thrust.ThrustOff();
                // Stop the engines at once: some spool down slowly (the Kerbstein drive still pushed 1 g three seconds
                // after the throttle was cut).
                foreach (ModuleEngines e in Vessel.FindPartModulesImplementing<ModuleEngines>())
                    if (e.EngineIgnited) e.currentThrottle = 0f;
                Core.Thrust.Users.Remove(this);
                Core.Attitude.Users.Remove(this);
                if (Core.Attitude.Users.Count == 0) Core.Attitude.attitudeDeactivate();
                if (TimeWarp.fetch != null)
                {
                    TimeWarp.SetRate(0, true);
                    if (savedPhysicsRates != null) TimeWarp.fetch.physicsWarpRates = savedPhysicsRates;
                }

                RestoreSettings();
            }
            catch (Exception e)
            {
                Debug.LogError("[FlipAndBurn] while stopping: " + e);
            }

            StopReason = reason;
            Arrived = arrived;
            guidance = null;
            WarpStatus = "";
            Enabled = false;
            Debug.Log("[FlipAndBurn] trip stopped: " + reason + " " + SettingsCheck);
        }

        protected override void OnModuleDisabled()
        {
            if (controlling) Stop("Flip and Burn was switched off.");
        }

        public override void OnDestroy()
        {
            if (controlling) Stop("The ship was unloaded.");
            base.OnDestroy();
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
            if (thrustWanted && pointed)
            {
                thrust.LimitAcceleration = true;
                thrust.MaxAcceleration.Val = cmd.Acceleration;
                thrust.TargetThrottle = 1f;
            }
            else
            {
                thrust.TargetThrottle = 0f;
                // Cut means cut: some engines spool down slowly (the Kerbstein drive kept pushing about 1 g through a
                // "cut, flip, relight" flip while its throttle wound down), so stop them at once.
                if (!thrustWanted)
                    foreach (ModuleEngines e in v.FindPartModulesImplementing<ModuleEngines>())
                        if (e.EngineIgnited) e.currentThrottle = 0f;
            }

            Watchdog(cmd, ut, thrustWanted, pointed, flipping);
            if (!Running) return;
            Warp(cmd);
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

            // Can the rest of the trip be flown at that acceleration?
            Vessel v = Vessel;
            CelestialBody sun = Planetarium.fetch.Sun;
            StateVector state = KspShip.HeliocentricState(v, ut);
            TargetPlanet departure = v.mainBody != sun && v.mainBody != trip.Target ? Planner.AsPlanet(v.mainBody) : null;
            var request = new TripRequest
            {
                ShipPosition = state.Position,
                ShipVelocity = state.Velocity,
                StartTime = ut,
                Target = baseConfig.Target,
                Departure = departure,
                Acceleration = now,
                ParkingAltitude = trip.ParkingAltitude,
                SunGravParameter = sun.gravParameter,
            };
            TripPlan plan = TripCalculator.Plan(request);
            double budget = Math.Max(0, trip.DeltaVBudget - deltaVUsed);
            CoastPlan coast = trip.Trip == TripType.CoastInTheMiddle ? CoastPlanner.Plan(request, budget) : null;
            string g = Planner.FormatG(now / TripCalculator.OneG), gWas = Planner.FormatG(was / TripCalculator.OneG);
            if (!plan.Feasible)
            {
                string reason = plan.Problem == TripProblem.TargetGravityTooStrong
                    ? $"{Planner.BodyName(trip.Target)}'s gravity at the parking orbit needs at least {Planner.FormatG(plan.MinimumAcceleration / TripCalculator.OneG)}"
                    : "there's no way to reach the target from here at that acceleration";
                Stop($"Stopped: the engines could only hold {g} ({why}), and the trip can't be finished at {g}: {reason}.");
                return;
            }

            if (coast != null && !coast.Feasible)
            {
                Stop($"Stopped: the engines could only hold {g} ({why}), and the delta-v left isn't enough for a coast trip at {g}.");
                return;
            }

            double remaining = coast != null ? coast.TripTime : plan.TripTime;
            double newArrival = ut + remaining;
            double change = newArrival - plannedArrivalUT;
            GuidanceConfig c = Copy(baseConfig);
            c.Acceleration = now;
            c.Departure = departure;
            c.DeltaVBudget = budget;
            guidance = new FlipAndBurnGuidance(c);
            CurrentAcceleration = now;
            plannedArrivalUT = newArrival;
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
            CorrectionThreshold = c.CorrectionThreshold, MinThrustFraction = c.MinThrustFraction,
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
