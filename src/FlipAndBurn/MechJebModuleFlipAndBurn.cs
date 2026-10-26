using MuMech;

namespace FlipAndBurn
{
    // The "Flip and Burn" window. MechJeb finds it on its own: MechJebCore.LoadComputerModules scans every loaded
    // assembly for ComputerModule subclasses with a (MechJebCore) constructor. See docs/adr/0001.
    // Empty for now; showing it in flight is the next cycle.
    public class MechJebModuleFlipAndBurn : DisplayModule
    {
        public MechJebModuleFlipAndBurn(MechJebCore core) : base(core)
        {
        }

        public override string GetName()
        {
            return "Flip and Burn";
        }
    }
}
