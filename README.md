# FlipAndBurn

FlipAndBurn is a Kerbal Space Program 1 plugin for Expanse-style travel between planets. You pick a target planet and an acceleration (1g by default). The ship burns at constant acceleration toward the target, flips at the midpoint, decelerates the rest of the way, and arrives matched to the planet's speed, then hands off to MechJeb for capture. Steering re-aims every physics tick and eases the throttle down as fuel burns so the acceleration holds steady. You choose the flip style: cut the drive, flip, and relight, or a skew flip under thrust. It is for interplanetary trips only, and long burns ride Time Control's hyper warp. The goal is for it to live inside MechJeb as a new autopilot and window.

## Status

Early skeleton. The DLL loads in KSP and defines an empty "Flip and Burn" MechJeb module. No flight logic yet.
See [docs/adr/0001-plug-into-mechjeb-as-addon.md](docs/adr/0001-plug-into-mechjeb-as-addon.md) for how it plugs into
MechJeb.

## Requirements

- KSP 1.12.5
- MechJeb2 (built and tested against 2.9.1)

## Building

You need the .NET SDK (6 or newer). It builds the .NET Framework 4.7.1 target on Linux through the
`Microsoft.NETFramework.ReferenceAssemblies` package.

1. Copy `local.props.example` to `local.props` and set `KSPDir` to your KSP install. For the Windows build of KSP, change
   `KSPManagedDir` to `$(KSPDir)/KSP_x64_Data/Managed`.
2. Run `./build.sh`. It compiles the DLL and copies it to `<KSPDir>/GameData/FlipAndBurn/Plugins/`.

The build references `Assembly-CSharp`, the UnityEngine DLLs and `MechJeb2.dll` from your install. None of them are
copied into this repo.

When KSP reaches the main menu, `KSP.log` should contain `[FlipAndBurn] loaded`.
