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
        public static double MaxPhysicsRate = 25;
        private const double SecondsBetweenWarpChanges = 1.0;   // real seconds

        private FlipAndBurnGuidance guidance;
        private TripStart trip;
        private ConfigNode savedLocal, savedType, savedGlobal;
        private float[] savedPhysicsRates;
        private double startUT, plannedArrivalUT, flipStartUT = double.NaN, offCourseSince = double.NaN, mispointedSince = double.NaN,
            noThrustSince = double.NaN, trim = 1, lastWarpChange;
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
        public double TurnRate { get; private set; }
        /// <summary>Why the last trip stopped, in plain words (null while running or before any trip).</summary>
        public string StopReason { get; private set; }
        public bool Arrived { get; private set; }
        /// <summary>True once the flip has finished (for the test helper).</summary>
        public double FlipSeconds { get; private set; } = double.NaN;
        public string SettingsCheck { get; private set; } = "";

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
            guidance = new FlipAndBurnGuidance(config);
            startUT = Planetarium.GetUniversalTime();
            plannedArrivalUT = startUT + start.PlannedTripTime;
            Last = null;
            StopReason = null;
            Arrived = false;
            FlipSeconds = double.NaN;
            flipStartUT = offCourseSince = mispointedSince = noThrustSince = double.NaN;
            trim = 1;

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
            Vector3d want = ToWorld(cmd.Direction);
            PointingError = Vector3d.Angle(facingWorld, want);

            if (cmd.Phase == GuidancePhase.Done)
            {
                Stop("Arrived: the ship is in its parking orbit.", arrived: true);
                return;
            }

            // Point.
            Core.Attitude.attitudeTo(want, AttitudeReference.INERTIAL, this);

            // Thrust: MechJeb's acceleration limit holds the acceleration; a slow trim makes up for engines whose thrust
            // isn't proportional to throttle.
            bool flipping = cmd.Phase == GuidancePhase.Flip;
            bool thrustWanted = cmd.Acceleration > 0;
            bool pointed = flipping || PointingError <= PointingTolerance;
            MechJebModuleThrustController thrust = Core.Thrust;
            if (thrustWanted && pointed)
            {
                thrust.LimitAcceleration = true;
                thrust.MaxAcceleration.Val = cmd.Acceleration * trim;
                thrust.TargetThrottle = 1f;
                if (!flipping && MeasuredAcceleration > 0.2 * cmd.Acceleration)
                {
                    double dt = TimeWarp.fixedDeltaTime;
                    double error = (cmd.Acceleration - MeasuredAcceleration) / cmd.Acceleration;
                    trim = Math.Max(0.7, Math.Min(1.5, trim * (1 + Math.Max(-0.01, Math.Min(0.01, 0.2 * dt * error)))));
                }
            }
            else
            {
                thrust.TargetThrottle = 0f;
            }

            Watchdog(cmd, ut, thrustWanted, pointed, flipping);
            if (!Running) return;
            Warp(cmd);
        }

        // Stop with a plain reason when something goes wrong.
        private void Watchdog(GuidanceCommand cmd, double ut, bool thrustWanted, bool pointed, bool flipping)
        {
            // Out of fuel: thrust wanted, ship pointed, throttle open, and the engines give almost nothing.
            bool infinite = CheatOptions.InfinitePropellant;
            bool enginesAlive = Vessel.FindPartModulesImplementing<ModuleEngines>().Any(e => e.EngineIgnited && !e.flameout);
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
                if (double.IsNaN(mispointedSince)) mispointedSince = ut;
                if (ut - mispointedSince > Math.Max(60, 3 * halfTurn))
                {
                    Stop($"Stopped: the ship can't hold its direction (still {PointingError:N0} degrees off after {ut - mispointedSince:N0} s).");
                    return;
                }
            }
            else mispointedSince = double.NaN;

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

        // ------------------------------------------------------------------ time warp

        private void Warp(GuidanceCommand cmd)
        {
            TimeWarp tw = TimeWarp.fetch;
            if (tw == null) return;
            WarpDecision d = WarpPolicy.Decide(cmd, PointingError <= 2);
            bool wantPhysics = d.Mode == WarpMode.Physics;
            bool wantRails = d.Mode == WarpMode.Rails;
            float[] rates = wantRails ? tw.warpRates : tw.physicsWarpRates;
            int index = d.Mode == WarpMode.None ? 0 : WarpPolicy.PickIndex(rates, d.MaxRate, wantPhysics ? MaxPhysicsRate : double.MaxValue);
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
                ? $"{kind} x{rate:0.#} ({d.Reason})"
                : $"normal speed ({d.Reason})";
        }

        // ------------------------------------------------------------------ frames

        // The guidance works in KSP's orbit frame (Orbit's vectors, Y and Z swapped relative to world space).
        private static Vec3 ToOrbitFrame(Vector3d world) => new Vec3(world.x, world.z, world.y);
        private static Vector3d ToWorld(Vec3 v) => new Vector3d(v.X, v.Z, v.Y);
    }
}
