using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn
{
    /// <summary>
    /// The "Flip and Burn" window in MechJeb's menu: pick a planet, an acceleration, a trip type, a flip style and a
    /// parking orbit, and see what the trip takes and whether this ship can do it.
    /// </summary>
    /// <remarks>
    /// MechJeb finds this class on its own (MechJebCore.LoadComputerModules scans every loaded assembly; ADR 0001).
    /// unlockParts and unlockTechs are left empty, so it shows in sandbox and career alike.
    /// </remarks>
    public class MechJebModuleFlipAndBurn : DisplayModule
    {
        public const string Title = "Flip and Burn";

        private static readonly string[] FlipStyleNames = { "Cut, flip, relight", "Skew flip" };
        private static readonly string[] TripTypeNames = { "Full burn", "Coast in the middle" };

        // Remembered across ships and sessions, like MechJeb's own settings.
        [UsedImplicitly]
        [Persistent(pass = (int)Pass.GLOBAL)]
        public readonly EditableDouble AccelerationG = new EditableDouble(1.0);

        [UsedImplicitly]
        [Persistent(pass = (int)Pass.GLOBAL)]
        public int FlipStyleIndex;

        [UsedImplicitly]
        [Persistent(pass = (int)Pass.GLOBAL)]
        public int TripTypeIndex;

        private readonly EditableDoubleMult parkingAltitudeKm = new EditableDoubleMult(0, 1000);
        private string parkingAltitudeFor;

        public Planner Planner { get; } = new Planner();

        private GUIStyle wrapStyle, noteStyle;

        public MechJebModuleFlipAndBurn(MechJebCore core) : base(core)
        {
        }

        public override string GetName() => Title;

        public override string IconName() => Title;

        protected override GUILayoutOption[] WindowOptions() => new[] { GUILayout.Width(380) };

        public override void OnUpdate()
        {
            if (!Enabled || Vessel == null) return;
            SyncInputs();
            if (Autopilot != null && Autopilot.Running) return;   // the planner isn't shown while flying
            Planner.Update(Vessel);
        }

        /// <summary>For the test harness: pick a target and plan right away.</summary>
        public bool SelectTarget(string bodyName)
        {
            if (!Planner.SelectTarget(bodyName)) return false;
            SyncInputs();
            Planner.Refresh(Vessel);
            return true;
        }

        /// <summary>For the test harness: pick the trip type and plan right away.</summary>
        public void SelectTripType(TripType trip)
        {
            TripTypeIndex = (int)trip;
            SyncInputs();
            Planner.Refresh(Vessel);
        }

        // Copy the text boxes into the planner, and load the parking altitude box when the target changes.
        private void SyncInputs()
        {
            Planner.AccelerationG = AccelerationG.Val;
            Planner.Flip = (FlipStyle)Mathf.Clamp(FlipStyleIndex, 0, FlipStyleNames.Length - 1);
            Planner.Trip = (TripType)Mathf.Clamp(TripTypeIndex, 0, TripTypeNames.Length - 1);

            CelestialBody target = Planner.Target;
            if (target == null) return;
            if (parkingAltitudeFor != target.bodyName)
            {
                parkingAltitudeFor = target.bodyName;
                parkingAltitudeKm.Val = Planner.ParkingAltitude(target);
            }
            else
            {
                Planner.SetParkingAltitude(target, parkingAltitudeKm.Val);
            }
        }

        public MechJebModuleFlipAndBurnAutopilot Autopilot => Core.GetComputerModule<MechJebModuleFlipAndBurnAutopilot>();

        /// <summary>The Start button (and the test helper): hand the ship to Flip and Burn. Returns a problem, or null.</summary>
        public string PressStart()
        {
            if (!Planner.CanStart) return "The planner says this trip can't be flown as set up.";
            MechJebModuleFlipAndBurnAutopilot pilot = Autopilot;
            if (pilot == null) return "Flip and Burn's autopilot isn't loaded.";
            return pilot.StartTrip(Planner.BuildTripStart());   // null when the trip started
        }

        /// <summary>The Abort button (and the test helper): end the trip for good, saved trip included.</summary>
        public void PressAbort() => Autopilot?.Stop("Aborted: you stopped the trip. The throttle is cut and your MechJeb settings are back.");

        /// <summary>
        /// The Pause button (and the test helper): engine off, trip kept. KSP won't save, switch ships or leave flight while a
        /// ship accelerates at more than 0.1 g, so this is how a player leaves during a burn (ADR 0004).
        /// </summary>
        public void PressPause() => Autopilot?.Pause("you pressed Pause", byPlayer: true);

        /// <summary>The Resume button (and the test helper). Returns a problem, or null.</summary>
        public string PressResume()
        {
            MechJebModuleFlipAndBurnAutopilot pilot = Autopilot;
            return pilot == null ? "Flip and Burn's autopilot isn't loaded." : pilot.PressResume();   // null when the trip carried on
        }

        protected override void WindowGUI(int windowID)
        {
            wrapStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
            noteStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(1f, 0.85f, 0.35f) } };

            GUILayout.BeginVertical();
            MechJebModuleFlipAndBurnAutopilot pilot = Autopilot;
            if (pilot != null && pilot.Running)
            {
                DrawFlying(pilot);
            }
            else if (pilot != null && pilot.Paused)
            {
                DrawPaused(pilot);
            }
            else if (Planner.SituationMessage != null)
            {
                GUILayout.Label(Planner.SituationMessage, wrapStyle);
            }
            else
            {
                DrawInputs();
                DrawRows(Planner.TripRows);
                if (Planner.TripRows.Count > 0 && Planner.ShipRows.Count > 0) GUILayout.Space(6);
                DrawRows(Planner.ShipRows);
                foreach (string note in Planner.Notes)
                {
                    GUILayout.Space(4);
                    GUILayout.Label(note, noteStyle);
                }

                GUILayout.Space(6);
                bool wasEnabled = GUI.enabled;
                GUI.enabled = Planner.CanStart && pilot != null;   // ADR 0002 decision 3
                if (GUILayout.Button("Start"))
                {
                    string problem = PressStart();
                    if (problem != null) ScreenMessages.PostScreenMessage("Flip and Burn: " + problem, 5f);
                }

                GUI.enabled = wasEnabled;
                if (pilot?.StopReason != null)
                {
                    GUILayout.Space(4);
                    GUILayout.Label(pilot.StopReason, noteStyle);
                    if (!string.IsNullOrEmpty(pilot.SettingsCheck)) GUILayout.Label(pilot.SettingsCheck, wrapStyle);
                }

                GUILayout.Label("No trip is running.", wrapStyle);
            }

            GUILayout.EndVertical();
            base.WindowGUI(windowID);
        }

        private void DrawFlying(MechJebModuleFlipAndBurnAutopilot pilot)
        {
            GuidanceCommand c = pilot.Last;
            TripStart t = pilot.Trip;
            GuiUtils.SimpleLabel("Flying to", $"{Planner.BodyName(t.Target)}, {(t.Trip == TripType.CoastInTheMiddle ? "coast in the middle" : "full burn")}");
            GuiUtils.SimpleLabel("Phase", c == null ? "starting" : PhaseName(c));
            if (c != null)
            {
                if (c.Phase == GuidancePhase.Accelerate)
                    GuiUtils.SimpleLabel(t.Trip == TripType.CoastInTheMiddle ? "Coast starts in" : "Flip in", Planner.FormatTime(c.TimeToFlip));
                if (c.Phase == GuidancePhase.Coast)
                    GuiUtils.SimpleLabel("Braking starts in", Planner.FormatTime(c.TimeToBurn));
                GuiUtils.SimpleLabel("Arrival in", Planner.FormatTime(c.TimeToArrival));
                GuiUtils.SimpleLabel("Acceleration", $"{Planner.FormatG(pilot.MeasuredAcceleration / Planner.OneG)} (holding {Planner.FormatG(pilot.CurrentAcceleration / Planner.OneG)})");
                GuiUtils.SimpleLabel("Pointing error", $"{pilot.PointingError:0.0}°");
            }

            DrawReserve(pilot);
            GUILayout.Label("Warp: " + pilot.WarpStatus, wrapStyle);
            if (pilot.Notice != null)
            {
                GUILayout.Space(4);
                GUILayout.Label(pilot.Notice, noteStyle);
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Pause")) PressPause();
            if (GUILayout.Button("Abort")) PressAbort();
            GUILayout.EndHorizontal();
        }

        private void DrawPaused(MechJebModuleFlipAndBurnAutopilot pilot)
        {
            TripRecord r = pilot.Record;
            CelestialBody target = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == r.Target);
            string name = target != null ? Planner.BodyName(target) : r.Target;
            GuiUtils.SimpleLabel("Trip to", $"{name}, {(r.Trip == TripType.CoastInTheMiddle ? "coast in the middle" : "full burn")}");
            GuiUtils.SimpleLabel("Phase", "paused, engine off");
            GuiUtils.SimpleLabel("Acceleration", Planner.FormatG(r.Acceleration / Planner.OneG));
            double toArrival = r.PlannedArrivalUT - Planetarium.GetUniversalTime();
            if (toArrival > 0) GuiUtils.SimpleLabel("Arrival was planned in", Planner.FormatTime(toArrival));
            DrawReserve(pilot);
            if (pilot.PauseNotice != null)
            {
                GUILayout.Space(4);
                GUILayout.Label(pilot.PauseNotice, noteStyle);
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (pilot.PausedByPlayer && GUILayout.Button("Resume"))
            {
                string problem = PressResume();
                if (problem != null) ScreenMessages.PostScreenMessage("Flip and Burn: " + problem, 8f);
            }

            if (GUILayout.Button("Abort")) PressAbort();
            GUILayout.EndHorizontal();
        }

        // The delta-v expected at arrival, and a plain warning when it is below the 10 % reserve.
        private void DrawReserve(MechJebModuleFlipAndBurnAutopilot pilot)
        {
            if (double.IsNaN(pilot.ReserveShare)) return;
            GuiUtils.SimpleLabel("Reserve at arrival", $"about {Planner.FormatSpeed(Math.Max(0, pilot.ReserveAtArrival))} ({pilot.ReserveShare * 100:0.0}%)");
            if (pilot.ReserveNote != null) GUILayout.Label(pilot.ReserveNote, noteStyle);
        }

        private static string PhaseName(GuidanceCommand c)
        {
            switch (c.Phase)
            {
                case GuidancePhase.Accelerate: return c.Departing ? "speeding up (climbing out)" : "speeding up";
                case GuidancePhase.Flip: return "flip";
                case GuidancePhase.Coast: return c.Correcting ? "coasting (course correction)" : "coasting, engine off";
                case GuidancePhase.Decelerate: return "slowing down";
                case GuidancePhase.FinalApproach: return c.Circularizing ? "final approach (settling into orbit)" : "final approach";
                default: return "arrived";
            }
        }

        private void DrawInputs()
        {
            List<CelestialBody> targets = Planner.Targets;
            if (targets.Count == 0)
            {
                GUILayout.Label("No other planets orbit the Sun.", wrapStyle);
                return;
            }

            GUILayout.Label("Fly to");
            string[] names = targets.Select(Planner.BodyName).ToArray();
            int selected = targets.FindIndex(b => b.bodyName == Planner.TargetName);
            int picked = GUILayout.SelectionGrid(selected, names, 4);
            if (picked != selected && picked >= 0) Planner.SelectTarget(targets[picked].bodyName);

            GUILayout.Space(4);
            GuiUtils.SimpleTextBox("Acceleration", AccelerationG, "g", 60);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Trip");
            TripTypeIndex = GUILayout.Toolbar(Mathf.Clamp(TripTypeIndex, 0, TripTypeNames.Length - 1), TripTypeNames);
            GUILayout.EndHorizontal();
            if (TripTypeIndex == (int)TripType.CoastInTheMiddle)
            {
                // The flip happens while coasting with the engine off, so there's nothing to choose.
                GUILayout.Label("Flip: during the coast, engine off (the flip style doesn't matter on a coast trip).", wrapStyle);
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Flip");
                FlipStyleIndex = GUILayout.Toolbar(Mathf.Clamp(FlipStyleIndex, 0, FlipStyleNames.Length - 1), FlipStyleNames);
                GUILayout.EndHorizontal();
            }
            CelestialBody target = Planner.Target;
            if (target != null)
                GuiUtils.SimpleTextBox($"Parking orbit at {Planner.BodyName(target)}", parkingAltitudeKm, "km", 60);
            GUILayout.Space(6);
        }

        private static void DrawRows(List<KeyValuePair<string, string>> rows)
        {
            foreach (KeyValuePair<string, string> row in rows)
                GuiUtils.SimpleLabel(row.Key, row.Value);
        }
    }
}
