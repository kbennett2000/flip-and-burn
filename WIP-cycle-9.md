# Cycle 9 (autopilot) — work in progress, paused for Kris's meeting (2026-10-26, 12:50)

**Done and pushed (branch cycle-9-autopilot):**
- Start is enabled only when the planner says the trip can be flown (infinite propellant counts as enough). Pressing it
  hands the ship to `MechJebModuleFlipAndBurnAutopilot`.
- The autopilot calls the guidance every physics step. It points with MechJeb's attitude control and sets thrust with
  MechJeb's throttle and acceleration limit, plus a slow trim.
- It saves and restores the player's MechJeb thrust and attitude settings with MechJeb's own save and load, and checks
  them after the restore.
- Watchdogs stop the trip, with a plain reason, for: out of fuel, can't turn, and off course.
- `WarpPolicy` (core, 8 tests): physics warp while burning, normal warp while coasting, normal speed while turning, and
  lead time before events.
- Flying view in the window: phase, times, acceleration, pointing error, warp, Abort; plus the "no trip after reload"
  line.
- ADR 0003: "Flying", "Time warp", and "Next: carrying on after a reload".
- README updated. The harness proof scripts (a) and (b) are written.
- build.sh also snapshots and restores BetterTimeWarp's settings file.

**In-game runs so far (both stopped early; settings restored each time):**
1. 12:29 run: the Start button helper had a bug: it reported "autopilot isn't loaded" when the trip had in fact started.
   Fixed.
2. 12:40 run: the trip started. The Bebop pointed 80° off the burn direction, and the warp policy went to 25× physics
   warp while it was still turning. The "can't hold its direction" watchdog then stopped the trip after 60 game
   seconds (2.4 real seconds). The MechJeb settings restore check reported identical. Fixed: no warp until the ship is
   pointed (with a test). This fix is not yet tried in game.

**Left to do:**
- Rerun the in-game proof: the warp probe (highest stable physics rate), (a) through the flip with braking and Abort,
  (b) into the coast and Abort. Screenshots, log check against the plan, settings byte-identical after each Abort.
- Commit the evidence; set `MaxPhysicsRate` to the measured value and record it in the ADR; open the PR.
- The turn-rate estimate came out at the 10°/s cap for the Bebop; check it against the measured flip time.
