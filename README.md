# FlipAndBurn

FlipAndBurn is a Kerbal Space Program 1 plugin for Expanse-style travel between planets. You pick a target planet and an acceleration (1g by default). The ship burns at constant acceleration toward the target, flips at the midpoint, decelerates the rest of the way, and arrives matched to the planet's speed, then hands off to MechJeb for capture. Steering re-aims every physics tick and eases the throttle down as fuel burns so the acceleration holds steady. You choose the flip style: cut the drive, flip, and relight, or a skew flip under thrust. It is for interplanetary trips only, and long burns speed up with BetterTimeWarp. The goal is for it to live inside MechJeb as a new autopilot and window.

## Status

In flight, MechJeb's menu lists "Flip and Burn" on any ship with MechJeb (sandbox and career, no tech needed). The
window is a trip planner:

- **Fly to:** every planet that orbits the Sun, read from the game (Outer Planets Mod's planets appear on their own),
  minus the one you're at. Around a moon, landed or still climbing, the window says what to do instead.
- **Inputs:** acceleration in g (default 1.0), flip style (cut, flip, relight or skew flip) and the parking orbit
  altitude at the target (a sensible default per planet; see ADR 0002).
- **Readouts:** trip time and time to the flip in KSP's own time format, peak speed, delta-v needed, and the ship's own
  delta-v and maximum acceleration. The ship's numbers come from its active engines and understand KSP Interstellar
  Extended's fusion drives, whose Isp drops when throttled down.
- **Start** stays off when the ship can't hold the acceleration or lacks the delta-v, with the shortfall spelled out.
  The autopilot isn't built yet, so Start is always off for now.

See [docs/adr/0001-plug-into-mechjeb-as-addon.md](docs/adr/0001-plug-into-mechjeb-as-addon.md) for how it plugs into
MechJeb.

## Trip math

`src/FlipAndBurn.Core` estimates a trip: trip time, time to the flip, peak speed, total delta-v (including matching the
planet's velocity and entering the parking orbit), and whether the path passes too close to the Sun. It has no KSP or
Unity dependency, so it is fully unit-tested:

```sh
dotnet test tests/FlipAndBurn.Core.Tests
```

The tests use KSP's real planet data, read from the install by the scripts in [tools/planet-data](tools/planet-data).
How a trip behaves, and the assumptions behind the estimate, are in
[docs/adr/0002-trip-behaviour.md](docs/adr/0002-trip-behaviour.md).

## Requirements

- KSP 1.12.5
- MechJeb2 2.15.3.1 or newer (built and tested against 2.15.3.1)
- BetterTimeWarp, for warping through long burns (used in a later cycle)

## Building

You need the .NET SDK (6 or newer). It builds the .NET Framework 4.8 target on Linux through the
`Microsoft.NETFramework.ReferenceAssemblies` package.

1. Copy `local.props.example` to `local.props` and set `KSPDir` to your KSP install. For the Windows build of KSP, change
   `KSPManagedDir` to `$(KSPDir)/KSP_x64_Data/Managed`.
2. Run `./build.sh`. It compiles `FlipAndBurn.dll` and `FlipAndBurn.Core.dll` and copies both to
   `<KSPDir>/GameData/FlipAndBurn/Plugins/`.

The build references `Assembly-CSharp`, the UnityEngine DLLs and `MechJeb2.dll` from your install. None of them are
copied into this repo.

When KSP reaches the main menu, `KSP.log` should contain `[FlipAndBurn] loaded`.

## In-flight test harness (dev only)

`./build.sh test` proves in-flight behaviour without anyone at the keyboard. It:

1. Builds and installs FlipAndBurn, then installs the test harness (`src/FlipAndBurn.TestHarness`) to
   `<KSPDir>/GameData/FlipAndBurnTestHarness/`.
2. Resets the test save `saves/FlipAndBurn-Test/persistent.sfs` from that save's `quicksave.sfs`, so every run starts
   from the same state.
3. Snapshots MechJeb's settings folder (`GameData/MechJeb2/Plugins/PluginData/MechJeb2`) and KSP's `settings.cfg`.
4. Launches KSP with `-flipandburn-test`. The harness loads the test save, switches to the ship "Bebop Refueller" in
   Kerbin orbit, logs what stock, MechJeb and Flip and Burn each say about the ship's thrust and delta-v, opens the
   Flip and Burn window, plans a trip to Duna and screenshots it, does the same for Jool, and quits KSP.
   It moves the Flip and Burn window to an empty part of the screen first, because MechJeb opens every window at
   the same spot. The harness reads the finished frame itself; if that comes out blank (it sometimes does on
   Linux/OpenGL), it asks this script to grab the KSP window from outside with `ffmpeg` (x11grab) instead.
   A blank (black) screenshot fails the run.
   It logs each step to `KSP.log` with the prefix `[FlipAndBurn.Test]`. If `KSP.log` stops growing for 2 minutes
   before the harness starts (the known startup lock-up), the script kills KSP and relaunches it (up to 3 times).
   A stall after the harness starts fails the run instead, since relaunching would only repeat it.
5. Removes the harness from GameData and restores both snapshots, even if the run fails or is interrupted.
6. Writes the screenshots (full-resolution JPEG) and the harness log lines (`test-run.log`) to
   `docs/test-evidence/` and prints `TEST PASSED` or `TEST FAILED`.

`tools/verify/check_planner.py` recomputes the screenshots' numbers independently of the plugin: planets from the
game's own data, the ship from the test save, the drive from its part config.

The harness never affects normal play:

- Plain `./build.sh` never installs it, and it removes any copy an interrupted test run left behind.
- Even if it were installed, it does nothing unless KSP was started with `-flipandburn-test`. Without that argument it
  destroys itself as soon as the main menu loads, before doing or logging anything.

The test save `saves/FlipAndBurn-Test` is a copy of the "North Kerbia" save whose `persistent.sfs` was replaced by
its quicksave (the original `persistent.sfs` is kept beside it as `persistent.sfs.original-from-north-kerbia`). The
original "North Kerbia" save is never touched.

## License

Released into the public domain under the [Unlicense](LICENSE).
