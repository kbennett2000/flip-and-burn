# FlipAndBurn

FlipAndBurn is a Kerbal Space Program 1 plugin for Expanse-style travel between planets. You pick a target planet and an acceleration (1g by default). The ship burns at constant acceleration toward the target, flips at the midpoint, decelerates the rest of the way, and arrives matched to the planet's speed, then hands off to MechJeb for capture. Steering re-aims every physics tick and eases the throttle down as fuel burns so the acceleration holds steady. You choose the flip style: cut the drive, flip, and relight, or a skew flip under thrust. It is for interplanetary trips only, and long burns speed up with BetterTimeWarp. The goal is for it to live inside MechJeb as a new autopilot and window.

## Status

In flight, MechJeb's menu lists "Flip and Burn" on any ship with MechJeb (sandbox and career, no tech needed). The
window is a trip planner:

- **Fly to:** every planet that orbits the Sun, read from the game (Outer Planets Mod's planets appear on their own),
  minus the one you're at. Around a moon, landed or still climbing, the window says what to do instead.
- **Inputs:** acceleration in g (default 1.0), trip type ("Full burn" or "Coast in the middle"), flip style (cut,
  flip, relight or skew flip; a coast trip flips while coasting, so it shows a note instead) and the parking orbit
  altitude at the target (a sensible default per planet; see ADR 0002).
- **Readouts, full burn:** trip time and time to the flip in KSP's own time format, peak speed, delta-v needed, and the
  ship's own delta-v and maximum acceleration.
- **Any engine:** the ship's numbers come from the engines firing now (the current stage) and the fuel they can reach:
  stock engines including multi-mode, nuclear and electric ones, other mods' engines built on the stock engine modules
  (Near Future, for example), and KSP Interstellar Extended's fusion drives, whose Isp drops when throttled down.
  Several engine types at once are fine: when one runs dry the others carry on. Electric engines are limited to what
  the ship's power supply can feed, and the window says so. An engine Flip and Burn doesn't know is read from KSP's own
  figures and the window says the numbers are estimates. Stages below the current one aren't counted, and the window
  says so ([ADR 0002](docs/adr/0002-trip-behaviour.md), "Engines and stages").
- **Readouts, coast in the middle:** the fastest burn-coast-burn trip the ship's delta-v allows while keeping 10 % in
  reserve: burn time at each end, coast time, trip time, delta-v needed and the reserve kept.
- **Short on delta-v:** a full burn spells out the shortfall and suggests the coast trip with its trip time; if even a
  coast trip can't be done, it says so.
- **Refusals:** an acceleration too weak to out-pull the target's gravity at the parking orbit, or a coast trip that
  would need to go around the Sun, each get a plain explanation. When the Sun is in the way of a full burn, the numbers
  include the detour around it.
- **Infinite propellant:** with KSP's cheat on, the window says so in one line and shows no delta-v shortfall.
- **Start** is on only when the planner says the trip can be flown (it stays off when the ship can't hold the
  acceleration or lacks the delta-v, with the shortfall spelled out; infinite propellant counts as enough).

Pressing **Start** hands the ship to Flip and Burn ([ADR 0003](docs/adr/0003-guidance.md), "Flying"):

- It steers every physics step, pointing with MechJeb's attitude control and holding the acceleration with MechJeb's
  throttle and acceleration limit as fuel burns off. Both flip styles work, and on a coast trip the engine is off for
  the whole coast.
- It runs time warp: physics warp while the engine fires, normal warp while coasting, back to normal speed in good time
  before the flip, the braking burn and the arrival.
- The window shows the phase (speeding up, flip, coasting, slowing down, final approach, arrived), time to the flip and
  to arrival, the acceleration in g, the pointing error and what warp is doing.
- **Abort** cuts the throttle, stops warp, releases control and puts the player's MechJeb settings back exactly.
- Flip and Burn also stops by itself, and says why, if fuel runs out, the ship can't turn fast enough, or it drifts too
  far off course.
- A trip doesn't carry on after the game is saved and loaded yet; the window says so.

See [docs/adr/0001-plug-into-mechjeb-as-addon.md](docs/adr/0001-plug-into-mechjeb-as-addon.md) for how it plugs into
MechJeb.

## Trip math

`src/FlipAndBurn.Core` estimates a trip: trip time, time to the flip, peak speed, total delta-v (including matching the
planet's velocity and entering the parking orbit), and whether the path passes too close to the Sun. It has no KSP or
Unity dependency, so it is fully unit-tested:

```sh
dotnet test tests/FlipAndBurn.Core.Tests --filter "Category!=LongRunning"  # everything but one long trip, under a minute
dotnet test tests/FlipAndBurn.Core.Tests --filter "Category=LongRunning"   # a Duna trip at 0.02 s steps throughout, ~8 minutes
```

The steering for the autopilot (`src/FlipAndBurn.Core/Guidance`, [ADR 0003](docs/adr/0003-guidance.md)) is proven
in a simulator in the test project: it flies full trips from a low Kerbin orbit under the Sun's and the planets'
gravity and checks that each ends in the chosen parking orbit. It uses KSP's 0.02 s physics step while burning near the
planets and during the flip, 2 s steps (physics warp) in a full burn's cruise, and big on-rails steps while coasting
with the engine off. The trips are flown in parallel.

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

   It screenshots each stage and logs the acceleration, pointing error, engine throttle, warp, waste heat and hottest
   part every few seconds. It checks MechJeb's settings files before Start and after Abort. Only the harness turns the
   cheats on, only in those runs, and turns them off again. `FLIPANDBURN_PROOFS=rates,overheat,flip,coast` picks which
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

## License

Released into the public domain under the [Unlicense](LICENSE).
