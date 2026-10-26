using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using MuMech;
using UnityEngine;

namespace FlipAndBurn
{
    /// <summary>
    /// The "Flip and Burn" window in MechJeb's menu: pick a planet, an acceleration, a flip style and a parking orbit,
    /// and see what the trip takes and whether this ship can do it.
    /// </summary>
    /// <remarks>
    /// MechJeb finds this class on its own (MechJebCore.LoadComputerModules scans every loaded assembly; ADR 0001).
    /// unlockParts and unlockTechs are left empty, so it shows in sandbox and career alike.
    /// </remarks>
    public class MechJebModuleFlipAndBurn : DisplayModule
    {
        public const string Title = "Flip and Burn";

        private static readonly string[] FlipStyleNames = { "Cut, flip, relight", "Skew flip" };

        // Remembered across ships and sessions, like MechJeb's own settings.
        [UsedImplicitly]
        [Persistent(pass = (int)Pass.GLOBAL)]
        public readonly EditableDouble AccelerationG = new EditableDouble(1.0);

        [UsedImplicitly]
        [Persistent(pass = (int)Pass.GLOBAL)]
        public int FlipStyleIndex;

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

        // Copy the text boxes into the planner, and load the parking altitude box when the target changes.
        private void SyncInputs()
        {
            Planner.AccelerationG = AccelerationG.Val;
            Planner.Flip = (FlipStyle)Mathf.Clamp(FlipStyleIndex, 0, FlipStyleNames.Length - 1);

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

        protected override void WindowGUI(int windowID)
        {
            wrapStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
            noteStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, normal = { textColor = new Color(1f, 0.85f, 0.35f) } };

            GUILayout.BeginVertical();
            if (Planner.SituationMessage != null)
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
                GUI.enabled = false; // ADR 0002 decision 3; and the autopilot isn't built yet.
                GUILayout.Button("Start");
                GUI.enabled = wasEnabled;
                GUILayout.Label("Autopilot coming soon.", wrapStyle);
            }

            GUILayout.EndVertical();
            base.WindowGUI(windowID);
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
            GUILayout.Label("Flip");
            FlipStyleIndex = GUILayout.Toolbar(Mathf.Clamp(FlipStyleIndex, 0, FlipStyleNames.Length - 1), FlipStyleNames);
            GUILayout.EndHorizontal();
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
