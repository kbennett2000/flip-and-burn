using MuMech;
using UnityEngine;

namespace FlipAndBurn
{
    // The "Flip and Burn" window in MechJeb's menu. MechJeb finds it on its own: MechJebCore.LoadComputerModules scans
    // every loaded assembly for ComputerModule subclasses with a public (MechJebCore) constructor. See docs/adr/0001.
    //
    // unlockParts and unlockTechs are left empty, so DisplayModule.UnlockCheck never hides it: it shows in sandbox and
    // career alike. ShowInFlight defaults to true and ShowInEditor to false, which is what we want.
    public class MechJebModuleFlipAndBurn : DisplayModule
    {
        public const string Title = "Flip and Burn";

        public MechJebModuleFlipAndBurn(MechJebCore core) : base(core)
        {
        }

        public override string GetName()
        {
            return Title;
        }

        public override string IconName()
        {
            return Title;
        }

        protected override GUILayoutOption[] WindowOptions()
        {
            return new[] { GUILayout.Width(240), GUILayout.Height(60) };
        }

        protected override void WindowGUI(int windowID)
        {
            GUILayout.BeginVertical();
            GUILayout.Label("Planner coming next.");
            GUILayout.EndVertical();

            // Draws MechJeb's close button and makes the window draggable.
            base.WindowGUI(windowID);
        }
    }
}
