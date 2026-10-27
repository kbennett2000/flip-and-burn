using JetBrains.Annotations;
using MuMech;

namespace FlipAndBurn
{
    /// <summary>
    /// Keeps the trip's acceleration limit out of every MechJeb save (ADR 0004).
    /// </summary>
    /// <remarks>
    /// While a trip flies it sets MechJeb's acceleration limit, which MechJeb keeps in its global settings file. MechJeb
    /// writes that file every few seconds, when the player switches ships and when the game quits, and saves its modules
    /// in rising priority. This module (priority 150) runs just before the thrust controller (200) and puts the player's
    /// values in; the autopilot (250) puts the trip's back right after. So every file MechJeb writes, and every saved game,
    /// has the player's settings, even one written mid-trip. It has no settings of its own.
    /// </remarks>
    [UsedImplicitly]
    public class MechJebModuleFlipAndBurnSaveGuard : ComputerModule
    {
        public MechJebModuleFlipAndBurnSaveGuard(MechJebCore core) : base(core)
        {
            Priority = 150;
        }

        public override void OnSave(ConfigNode local, ConfigNode type, ConfigNode global)
        {
            base.OnSave(local, type, global);
            Core.GetComputerModule<MechJebModuleFlipAndBurnAutopilot>()?.BeforeThrustSaves();
        }
    }
}
