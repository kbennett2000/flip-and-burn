# Developing Flip and Burn

How the mod is built, tested and proven. The player's guide starts at the [README](../README.md); the design decisions
are in the [ADRs](adr/).

## Trip math

`src/FlipAndBurn.Core` estimates a trip: trip time, time to the flip, peak speed, total delta-v (including matching the
planet's velocity and entering the parking orbit), and whether the path passes too close to the Sun. It has no KSP or
Unity dependency, so it is fully unit-tested:

```sh
dotnet test tests/FlipAndBurn.Core.Tests --filter "Category!=LongRunning"  # the everyday suite, about 2 minutes
dotnet test tests/FlipAndBurn.Core.Tests --filter "Category=LongRunning"   # the arrival-watch sweep (1.5 min) and a Duna trip at 0.02 s steps throughout (about 20 min)
```

The steering for the autopilot (`src/FlipAndBurn.Core/Guidance`, [ADR 0003](adr/0003-guidance.md)) is proven
in a simulator in the test project: it flies full trips from a low Kerbin orbit under the Sun's and the planets'
gravity and checks that each ends in the chosen parking orbit. It uses KSP's 0.02 s physics step while burning near the
planets and during the flip, 2 s steps (physics warp) in a full burn's cruise, and big on-rails steps while coasting
with the engine off. The trips are flown in parallel.

The tests use KSP's real planet data, read from the install by the scripts in [tools/planet-data](../tools/planet-data).
How a trip behaves, and the assumptions behind the estimate, are in
[ADR 0002](adr/0002-trip-behaviour.md).

## Requirements for building

- KSP 1.12.5, MechJeb2 2.15.3.1 and Module Manager installed (the build references their DLLs from your install)
- The .NET SDK (6 or newer)

## Building

It builds the .NET Framework 4.8 target on Linux through the
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
3. Snapshots MechJeb's settings folder (`GameData/MechJeb2/Plugins/PluginData/MechJeb2`), KSP's `settings.cfg` and
   BetterTimeWarp's settings file.
4. Launches KSP with `-flipandburn-test`. The harness loads the test save, switches to the ship "Bebop Refueller" in
   Kerbin orbit, logs what stock, MechJeb and Flip and Burn each say about the ship's thrust and delta-v, opens the
   Flip and Burn window and flies trips to Duna on the autopilot:
   - the planner at 2 g;
   - (a) a full burn at 1 g with infinite propellant: the ship overheats and the autopilot steps down, re-plans and says
     so, then Abort;
   - (a) the flip, from the same start, at 1 g with infinite propellant and KSP's "ignore max temperature" cheat (the
     Bebop can't sustain 1 g otherwise): a warp-stability probe, the flip and a few minutes of braking, then Abort;
   - (b) a coast trip at 0.4 g with no cheats, through the burn into the coast under normal warp, then Abort.
   - (r) the player's warp rates and throttle: Start a coast trip at 0.4 g, a minute of burn, Abort; the physics warp rates and
     MechJeb's throttle users must be as before Start, and the ship must follow the player's throttle. Then Start
     again and leave for the Space Center mid-trip, and fly again: the rates must be the player's there too.
   - (resume) a coast trip at 0.4 g that carries on: a minute of burn, Pause, the Space Center and back, a quicksave,
     more burn and a quickload, another ship (with a trip of its own) and back, a save made mid-trip, then Abort, a
     quicksave and a quickload that bring nothing back. Each leave goes through KSP's own code for that button, and
     only when KSP would allow it. This script then launches KSP again to load the save made mid-trip, and once more
     with Flip and Burn removed from GameData (put back straight after) to show the save still loads.

   Later cycles added more proofs, each run only when asked by name: `tripA`, `tripB`, `tripC` (whole trips to
   Duna), `lossplan` (the planner with a measured boil-off), `readercheck` (the delta-v reader against
   `tools/verify/check_reader.py`), `drain` and `coastdrain` (where propellant goes while the engine is off) and
   `docshots` and `docstart` (the pictures in the player's guide).

   It screenshots each stage and logs the acceleration, pointing error, engine throttle, warp, waste heat and hottest
   part every few seconds. It checks MechJeb's settings files before Start and after Abort. Only the harness turns the
   cheats on, only in those runs, and turns them off again. `FLIPANDBURN_PROOFS=rates,resume,overheat,flip,coast` picks which
   proofs to run (`FLIPANDBURN_SHOTS` lists the screenshots expected, or `none`). Then it quits KSP.
   It moves the Flip and Burn window to an empty part of the screen first, because MechJeb opens every window at
   the same spot. The harness reads the finished frame itself; if that comes out blank (it sometimes does on
   Linux/OpenGL), it asks this script to grab the KSP window from outside with `ffmpeg` (x11grab) instead.
   A blank (black) screenshot fails the run.
   It logs each step to `KSP.log` with the prefix `[FlipAndBurn.Test]`. If `KSP.log` stops growing for 2 minutes
   before the harness starts (the known startup lock-up), the script kills KSP and relaunches it (up to 3 times).
   A stall after the harness starts fails the run instead, since relaunching would only repeat it.
5. Removes the harness from GameData and restores both snapshots, even if the run fails or is interrupted.
6. Writes the screenshots (full-resolution JPEG), the harness log lines (`test-run.log`) and a check that the cheat
   left no trace (`cheat-check.log`: `settings.cfg` and every other save fingerprinted before and after) to
   `docs/test-evidence/` and prints `TEST PASSED` or `TEST FAILED`.

`tools/verify/check_planner.py` recomputes the trip numbers independently of the plugin: planets from the game's own
data, the ship from the test save, the drive from its part config. `tools/verify/check_engines.py` recomputes any test
ship's thrust and delta-v from the save, the engines' part configs and the resource definitions, working out the fuel
each engine can reach by walking the part tree.

The harness never affects normal play:

- Plain `./build.sh` never installs it, and it removes any copy an interrupted test run left behind.
- Even if it were installed, it does nothing unless KSP was started with `-flipandburn-test`. Without that argument it
  destroys itself as soon as the main menu loads, before doing or logging anything.

The test save `saves/FlipAndBurn-Test` is a copy of the "North Kerbia" save whose `persistent.sfs` was replaced by
its quicksave (the original `persistent.sfs` is kept beside it as `persistent.sfs.original-from-north-kerbia`). The
original "North Kerbia" save is never touched.


## Releasing

`./build.sh release` builds and writes `release/FlipAndBurn-<version>.zip`, holding exactly what goes in `GameData`
(`GameData/FlipAndBurn/...`). The version comes from `src/FlipAndBurn/FlipAndBurn.csproj`. The install page,
[installing.md](installing.md), describes that zip.
