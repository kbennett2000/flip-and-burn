# ADR 0004: A trip carries on after saving, loading and leaving the ship

- Status: Accepted
- Date: 2026-10-27

## Context

A trip to Duna takes days of game time. Players save, quit, go to the Space Center or the Tracking Station, fly other
ships and load quicksaves along the way. Until now a trip simply stopped when the flight scene ended. It should pause
instead, and pick up again where it left off.

How KSP and MechJeb behave decides most of this:

- **When KSP saves a ship.**
  - It saves every ship when it saves the game: on F5, on leaving flight (the Space Center and Tracking Station buttons,
    switching to a ship that isn't loaded), on autosave, and from the pause menu.
  - It saves *before* it leaves the scene, so a trip still flying is saved flying.
  - A ship's part modules are saved first thing in `Game.Updated()`, with no event before.
- **KSP won't save, switch ships or leave flight while a ship accelerates at more than 0.1 g.**
  - `FlightGlobals.ClearToSave` blocks it, and the buttons are locked.
  - On a full-burn trip the engine fires nearly the whole way.
- **What MechJeb flies.** It runs its modules only for the active ship's main MechJeb unit.
- **What MechJeb saves.**
  - It saves each module's per-ship ("local") settings inside its own part module's node in the saved game.
  - It writes its global settings file every 5 seconds while settings change, when the player switches ships, and when
    the game quits.
  - It saves its modules in rising priority.
  - On load it reads only the nodes of modules it has, and the next save rebuilds the node from the modules present.
- **The trip's only change to MechJeb's settings** is the thrust controller's acceleration limit (`LimitAcceleration`,
  `MaxAcceleration`), and MechJeb keeps that in the global file.

## Decisions

### Where the trip is kept: with the ship, inside MechJeb's per-ship settings

The trip is a `TripRecord`, stored as a `FLIPANDBURN_TRIP` node under the autopilot module's node in MechJeb's per-ship
settings. In the saved game that is:

```
VESSEL { PART { MODULE { name = MechJebCore  MechJebLocalSettings { MechJebModuleFlipAndBurnAutopilot { FLIPANDBURN_TRIP { … } } } } } }
```

What the record holds:
- the target's name, trip type, flip style and parking altitude;
- the chosen acceleration and the one being flown (lower after an overheating re-plan);
- the coast budget set at Start, and the delta-v used so far;
- the start time and the planned arrival;
- the last moment the trip was flying;
- the re-plan count;
- the engines the trip had shut down, by part id.

The plan itself isn't saved: a resumed trip is re-planned.

Why here rather than a KSP scenario module or a vessel module:
- **One trip per ship, for free.** Each ship's MechJeb carries its own record, and it travels with the ship while the
  ship isn't loaded. There is no list keyed by vessel id to keep in step when ships dock, undock, are destroyed or are
  recovered.
- **It is written by the same save pass as MechJeb's own settings,** and read back when the ship's MechJeb loads.
- **It is the safest place if Flip and Burn is removed** (see next).

Format:
- Plain name = value pairs, version 1.
- Numbers in the invariant culture with round-trip formatting, so a record reads back bit for bit whatever the
  player's language.
- A record from a newer version, or with anything missing or unreadable, isn't flown. The window says why.

### Safe if Flip and Burn is removed

- KSP never reads the node: it is inside MechJeb's.
- MechJeb looks up each of its modules' nodes by name, so with Flip and Burn gone nobody asks for it, and the ship
  loads as if it weren't there.
- The next time the game is saved, MechJeb rebuilds its node from the modules it has, and the trip data is dropped
  quietly. The rest of MechJeb's per-ship settings are kept.
- Proven in the test copy (`docs/test-evidence/resume-run.log`, phase "nomod"): with Flip and Burn removed from GameData,
  the save made mid-trip loaded and the ship flew with all its parts. Saving again kept the ship and MechJeb's
  settings, without the trip, and the save made mid-trip was left byte for byte as it was.
- One caveat, not specific to Flip and Burn: KSP gives a ship back the throttle it was saved with. KSP saves only
  below 0.1 g, so a trip saved during a burn below 0.1 g, and loaded without Flip and Burn, would burn on with nobody
  steering, as any save made mid-burn would.

### When the trip is written

Whenever KSP saves the ship. At that moment the record is brought up to date: the time, the delta-v used, the
acceleration and the planned arrival. Nothing is written anywhere else: no file of Flip and Burn's own. Abort, arrival
and every self-stop forget the trip, so the next save has none, and loading it brings nothing back.

### Leaving the ship pauses the trip

Every way of leaving pauses the trip, never stops it:
- **Leaving the flight scene** (Space Center, Tracking Station, main menu, a quickload). This is caught with
  `onGameSceneLoadRequested`, before the scene is torn down, after KSP has saved.
- **Switching to another ship** (`onVesselChange`).
- **The ship unloading, or the game quitting** (the module's `OnDestroy`).

Pausing gives the ship back exactly as the end of a trip does:
- the engine is cut and left active at zero throttle;
- MechJeb's throttle and attitude control are released;
- the player's physics warp rates and MechJeb settings are put back;
- the trip is kept.

The player sees "paused … it carries on when you come back to this ship" on screen and in the window.

### The Pause and Resume buttons

KSP won't let a player save or leave above 0.1 g, so a player couldn't leave during a burn. The window therefore has a
**Pause** button: engine off, the ship coasts, and the trip is kept. KSP then allows saving and leaving.

- **Resume** carries on in place.
- Leaving the ship after Pause makes it an ordinary paused trip: it carries on by itself when the player comes back.
- Abort works while paused too.

### Coming back resumes the trip by itself

When the ship is the active ship again, loaded and off rails (switched back to, or loaded from a save made mid-trip),
the trip resumes on its own, in four steps:

1. **It waits 3 real seconds for the scene to settle,** holding the throttle and the engines at zero. KSP gives a
   loaded ship the throttle it was saved with, and BetterTimeWarp sets its rates once the scene's interface is ready.
2. **It re-plans from where the ship is now, not from the old plan.** `TripReplanner` applies the planner's own tests
   from Start:
   - The target must be reachable from here at the acceleration being flown.
   - That acceleration must beat the target's gravity at the parking orbit, and the engines must still reach it.
   - For a full burn, the ship must have the delta-v for the rest of the trip, unless infinite propellant is on.
   - For a coast trip, the budget is what is left of the one set at Start, which keeps the 10 % reserve set then,
     capped at 90 % of the ship's delta-v now. A coast plan must fit it, and the Sun must not be in the way.
   - A coast trip keeps the arrival it was already flying to, if the ship is still expected to arrive with at least 9 %
     of the delta-v the trip started with (see "Arrival after a resume" below). Otherwise it takes the fastest plan from
     here, which keeps the full 10 %.
3. **If the trip can no longer be finished, nothing fires.** The trip stops with the reason in plain words, and
   suggests planning a new trip in the window, which shows what the ship can still do from where it is.
4. **Otherwise it flies on, the same way as at Start.**
   - The engines stay at zero until the ship points within 5° of the burn direction.
   - Warp waits until the ship is within 2°.
   - The window opens.
   - On screen and in the window: "Trip resumed after *time away* away. Re-planned from where the ship is now:
     arrival in *…*, *…* later than planned before."

### Arrival after a resume

The first in-game run said "resumed after 5 s away, arrival 32 minutes later". Nothing was lost while away:
- Just before the pause, the guidance was flying to UT 18378245640, 35 s from the Start estimate.
- A coast trip flies to a **fixed** arrival time, chosen once when it starts (ADR 0003). The resume threw that away and
  asked the coast planner for a fresh fastest plan from part-way through the climb out of Kerbin, which said
  UT 18378247540.
- The planner's estimates from different points of the same trip differ by up to about 1 % (ADR 0003, "Known
  follow-up: coast accuracy"). The simulator shows the same with no time away at all. After 65 s, 300 s and 1,800 s
  of a Bebop coast trip, a fresh plan moved the arrival by +0.53 %, -0.98 % and +0.32 %.

So a resumed coast trip now keeps flying to the arrival it was flying to (`CoastPlanner.PlanArrivingAt`,
`GuidanceConfig.CoastArrivalUT`), exactly as an uninterrupted trip would. In the simulator it then arrives on the second,
within the budget (`ShortAbsence_TheTripArrivesWhenItWasGoingTo`).

**The reserve is the promise.** A coast trip keeps 10 % of the delta-v it started with in reserve (ADR 0002 addendum).
Keeping the arrival after a short pause took 100.4-100.6 % of the budget left, by the planner's own reckoning, which
eats a sliver of that reserve on paper. So the rule is about the reserve:
- The arrival is kept only if the ship is still expected to arrive with at least 9 % of the delta-v the trip started
  with (`TripReplanner.KeepReserveFloor`). Otherwise the trip re-plans to the fastest from here, which keeps the full
  10 %. The unit test `KeptArrival_OnlyAboveTheReserveFloor` puts the same kept arrival at 8.9 % (re-planned, 10.0 %)
  and 9.1 % (kept).
- "Expected" counts the burns plus the whole 6 % guidance margin, so it is conservative: flying costs less.
- The window shows the reserve expected at arrival. When it is below 10 % for any reason (a kept arrival, a ship with
  less delta-v than planned, an overheating re-plan), the window says so in plain words, with the amount and why.

The Bebop coast trip to Duna at 1 g in the simulator (`ReserveAtArrival_ThreeCases`). It started with 116.05 km/s, so
10 % is 11.61 km/s:

| Case | Reserve at arrival | Expected at the resume |
|---|---|---|
| (a) no pause | 16.13 km/s, 13.9 % | n/a |
| (b) paused after 30 min and resumed at once, arrival kept | 16.13 km/s, 13.9 % | 11.22 km/s, 9.7 % |
| (c) left for two days after 30 min, re-planned | 16.44 km/s, 14.2 % | 11.61 km/s, 10.0 % |

After a real absence (two days here) the old arrival needs far more than that. The trip re-plans to the fastest from
where it is, and the message says why the arrival moved: "the engine was off for …".

The arrival compared before and after is the guidance's own (a coast trip's fixed arrival, or a full burn's current
estimate), at both ends. The message comes on the trip's first guidance step after the resume.

### MechJeb settings: never saved modified

MechJeb can write its global settings file at any moment of a trip. **`MechJebModuleFlipAndBurnSaveGuard`**
(priority 150) runs just before the thrust controller (200) saves, and puts the player's acceleration limit in. The
autopilot (250) puts the trip's back straight after.

So every file MechJeb writes, and every saved game, has the player's values, even mid-trip, and even when the game
quits mid-burn. Proven in the game: a MechJeb save forced mid-trip wrote the player's values to
`mechjeb_settings_global.cfg` while the trip's limit stayed on in memory.

### Warp rates

KSP doesn't save warp rates, and BetterTimeWarp saves its presets only from its own edit window. The player's rates
come back at every pause, as at every stop (ADR 0003). KSP's TimeWarp is a new object in each scene.

If something hands the game new rates during a trip (BetterTimeWarp sets its preset when a scene's interface is
ready), those become the player's rates to give back, and the trip's go back in.

### One trip per ship

Each ship carries its own record, and only the active ship can fly a trip. Two ships with trips just work:
switching from one to the other pauses one and resumes the other.

## Consequences

- **What players should know: a save made mid-trip and loaded without Flip and Burn keeps the ship and simply loses
  the trip.** The ship, its parts and its MechJeb settings load as they were. The ship coasts with the engine as it was
  saved, which is off unless the save was made during a burn below 0.1 g. Nothing resumes, and the next save drops the
  trip for good. Putting Flip and Burn back later doesn't bring that trip back; start it again from the window.

- **A resumed trip's arrival changes.** It usually comes later than first planned, because the ship coasted slower
  than the plan would have flown it. The message says by how much.
- **The resume only needs the record.** A trip that couldn't be read, or whose target no longer exists, stops with a
  plain reason instead of guessing.
- **Known limits:**
  - If docking or undocking makes a different MechJeb part the ship's main one, the trip stays with the old part and
    won't resume. Starting it again is the fix.
  - Time away is game time: a save loaded months later in real time, at the same game time, has "away" near zero.

## Proof

- **Unit tests**:
  - `TripRecordTests`: the record is saved and read back exactly, whatever the player's language. A missing,
    unreadable or newer record isn't flown.
  - `TripReplannerTests`: re-planned from where the ship is after days away (arrives later, still fits the budget),
    and the re-planned trip then flies to its parking orbit when the re-plan said, within 1 %. Refused when the
    budget or the ship's delta-v is short, when the engines can't reach the acceleration, and when the acceleration
    can't beat the target's gravity.
- **In the game** (`docs/test-evidence/resume-run.log`, screenshots `r-*.jpg`): Bebop Refueller, a coast trip to Duna
  at 0.4 g, no cheats, test copy only.
  - **Every resume kept the arrival (+0 s).** That covers the Space Center and back, a quickload, the switch to another
    ship and back, Resume in place, and a relaunch of KSP.
  - **The engines stayed at zero until the ship pointed within 5 degrees.** The test turned the ship 90 degrees away
    while paused, before the save used for the relaunch. Both resumes from it turned back from 91.4-91.6 degrees and
    first fired at 4.5-4.6 degrees.
  - **A second ship (Bebop V2.4, at Duna) flew its own trip to Dres.** It resumed on its own when switched back to
    (59 degrees off at the resume, first fire at 1.0 degree), and its Abort left nothing in the save.
  - **MechJeb's settings file, written mid-trip, held the player's acceleration limit** (`mechjeb-settings-mid-trip.txt`).
  - **No exceptions; saves and settings untouched.** No exception mentions Flip and Burn or MechJeb in any of the
    three KSP launches. None of the player's other 5,162 save files changed (`cheat-check-resume-run.log`).
