# ADR 0003: Steering a Flip and Burn trip

- Status: Accepted
- Date: 2026-10-26

## Context

The autopilot needs steering that runs every physics step: given the ship's current position and velocity, the target
planet's orbit, the chosen acceleration, the flip style and the parking orbit altitude, say where to point, how hard to
thrust, which phase the trip is in, and how long until the flip and the arrival. It has to re-plan from the ship's
actual state every step, so errors, the Sun's gravity and the planet's motion are corrected continuously.

It lives in `src/FlipAndBurn.Core/Guidance` (no KSP or Unity) and is proven in a simulator in the test project
(`tests/FlipAndBurn.Core.Tests/ShipSimulator.cs`), outside KSP.

## Decisions

### Phases

Accelerate, Flip, Decelerate, Final approach (decelerating inside the target's sphere of influence), Done. A Sun detour
runs this sequence twice (two legs).

### Before the flip: two constant-thrust arcs

Each step, solve for two constant thrust vectors from the ship's actual state: A1 for t1 seconds, then the flip, then
A2 for t2 seconds, ending exactly in the parking-orbit state at the moving planet at time t1 + flip + t2
(`TwoArcSolver`). |A1| is the chosen acceleration; |A2| is 99 % of it (see the braking band below). The solve itself
is gravity-free; re-solving every step absorbs the Sun's pull. The target's gravity well is too big for that (falling
to 220 km over Jool is worth up to 9.5 km/s), so each step estimates what the well will add over the braking half,
from the current plan, and the solve aims short of the arrival state by that much. The solution is warm-started from
the previous step's, so a step costs a few microseconds.

The ship points along A1. t1 is the time to the flip.

### The flip

The plan includes the flip as a coast lasting as long as the turn takes (angle between A1 and A2 divided by the ship's
turn rate, which is an input; in the game it will come from MechJeb). Without that, a cut-drive flip leaves the ship
about 10,000 km short on a Duna trip and it can't recover.

- **Cut, flip, relight** (default): thrust 0 while turning; relight once the ship points within 5 degrees of the new
  direction.
- **Skew flip**: thrust stays on while turning. The sideways push this causes is corrected by the steering after the
  flip, which sees it as an error like any other.

### After the flip: zero-effort-miss / zero-effort-velocity steering

`ArrivalLaw`. With time to go τ, predict where the ship would coast to (ZEM: position miss, ZEV: velocity miss) and
command a = 6 ZEM / τ^2 - 2 ZEV / τ. Details that turned out to matter in simulation:

- **τ is a countdown**, started from the plan made before the flip. It is not re-solved to "whatever makes the thrust
  exactly right" each step: doing that silently resets the deadline and never corrects being ahead or behind.
- **Braking band.** The braking half is planned at 99 % of the chosen acceleration and may use 98.2-100 %. That band
  is the only way to fix being a little ahead or behind while keeping the acceleration steady. Only if the formula
  would leave the band is the deadline moved, just enough to stay on its edge, and never jumps (one of the other
  mathematical solutions speeds the ship up into the planet).
- **Gravity** in the prediction is the target planet's pull sampled along the expected path (samples crowd toward the
  arrival, where the pull is strongest), plus the difference between the Sun's pull on the ship and on the planet. An
  exact two-body coast is not used: while still braking toward the planet, the coast would pass near its centre, where
  the prediction becomes meaningless.
- **Circularising.** When less than 1 s is left, the ship is at the parking radius moving along the orbit, but
  usually not at exactly circular speed: near a strong well the pull is mostly sideways on the final approach, and
  fighting it at a fixed acceleration costs some braking. A last burn along the orbit (against the motion if too fast,
  with it if too slow) at the chosen acceleration, with a radial part that holds the parking radius, finishes the job
  in seconds to a couple of minutes. Then **Done**.

### Where the trip ends

A circular parking orbit at the chosen altitude, **in the target's equatorial plane, prograde** (counter-clockwise seen
from the planet's north; the guidance takes the planet's rotation axis as an input, which in KSP is the same for every
body). The arrival point is where that orbit runs in the ship's direction of travel, 90 degrees around from the side
facing the incoming ship. The braking then ends with the ship already moving along the orbit at orbital speed, instead
of falling straight in and having to turn sideways at the last moment (which needs more than the chosen acceleration).

### Leaving the departure planet

Thrusting straight at the target from a low orbit can point into the planet (from its far side) or fail to climb out at
low acceleration. Near the departure planet the ship first burns prograde until it is on an escape path with a clear
line to the target, then steers normally; once out, it doesn't switch back. At 1 g from a 100 km Kerbin orbit this
takes about 95 seconds.

### The Sun in the way

When the planner flags the straight path as passing within the Sun safety distance (ADR 0002), the guidance flies two
legs: first to a waypoint beside the Sun, arriving at rest there, then a normal trip from there. The waypoint sits in
the plane of the start, the target and the Sun, perpendicular to the straight line, and far enough out that both legs
keep 1.2 times the safety distance from the Sun. Stopping at the waypoint is simple and robust but costs extra delta-v
and time; a smoother detour (passing the waypoint without stopping) can replace it later behind the same phases.

### The coast phase (coast trips)

A coast trip ("Coast in the middle", ADR 0002 addendum) burns up, coasts with the engine off, and burns down. The
planner (`CoastPlanner`) picks the shortest coast whose two burns fit the delta-v budget, using the same two-arc solver
with a longer coast between the arcs. The arrival time is then fixed.

- **Burn up:** the two-arc solver with the total time fixed (`SolveFixedTotal`): the coast takes whatever the burns
  don't need. When the first arc runs out, the Coast phase starts.
- **Coast:** each step works out the velocity the ship should have now so that coasting, then braking at the planned
  acceleration, ends at the arrival point on time. With gravity's contribution G1 (velocity) and G2 (displacement) over
  the rest of the trip, the braking acceleration A satisfies A (τ - tb/2) = (vF - G1) τ - (rF - r - G2), and |A| fixes
  the braking time tb in closed form. When the ship's velocity is off by more than 1 m/s (and braking is more than ten
  minutes away) it turns and makes a short course correction burn at the chosen acceleration. Otherwise the engine is
  off and the ship points where the braking will go: the flip happens here, so the flip style doesn't matter.
- **Burn down:** when braking is due, the arrival law below takes over with the same arrival point and time.

Gravity over a coast of weeks needs care, because the Sun's pull differs between the ship and the target by up to a
few mm/s^2 and the ship travels tens of billions of metres:

- The expected path for the gravity integrals follows the burns, then the Sun-centred orbit the coasting ship will
  really be on (`PathGravity.CoastPath`), then a smooth curve into the arrival state. A smooth curve over the whole trip
  put the coasting ship billions of metres from where it would be; a straight line, about 10^8 m.
- The coast is sampled with 128 Simpson intervals instead of 32 (the Sun term varies along the whole coast).
- The plan is solved in the Sun's frame, so the allowance also includes the target's own curve around the Sun over
  the rest of the trip (`PathGravity.FrameCurve`). Leaving it out left a coast trip 5 x 10^8 m off at arrival.
- The planner allows for the departure planet's pull while the ship climbs out (`DepartureGravity`): a ship starting
  in low orbit doesn't keep its orbital speed. Without it the first burn ran about 330 s longer than planned to Duna.

Flying a coast trip still costs more than its two planned burns: 1.4 % to Duna and 4.4 % to Jool in the simulator
(steering corrections, the braking law holding 98-100 % of the acceleration, the final circularizing burn, and the
rough gravity model near the target). The planner therefore plans the burns 6 % under the budget
(`CoastPlanner.GuidanceMargin`) and shows the delta-v needed with that margin, so the 10 % reserve is still there at
arrival (13.9 % left at Duna, 11.4 % at Jool).

### Known follow-up: coast accuracy

Not done in this cycle; recorded so it isn't lost:

- **Departure gravity in the guidance.** Only the planner allows for the departure planet's pull. The guidance
  re-solves from the actual state and so absorbs it, but aims slightly off at first; the first burn still runs about
  90 s (Duna) longer than planned. Feeding the same allowance to the guidance, cheaply enough to run every step, would
  close that.
- **Patched-conic coast near the target.** The expected coast path ignores the target's pull until braking. Near Jool
  that pull bends the path enough to need several course corrections and a braking burn about 400 s longer than
  planned. Switching the predicted coast to an orbit around the target once inside its sphere of influence (as KSP's
  patched conics do) should fix it. A first attempt slowed the guidance too much and was set aside.
- With both, the guidance margin could come down from 6 %.

## Consequences

- Every step re-plans from the actual state, so the simulator's Sun gravity, planet motion, turn-rate limit and large
  warp steps are all absorbed without special cases.
- Acceleration stays within 2 % of the chosen value outside the flip (and the engine-off coast): exact before it,
  98.2-100 % after it, and course corrections burn at the chosen acceleration.
- **Limit:** the final approach needs the chosen acceleration to beat the target's gravity at the parking orbit. Jool's
  pull at 220 km is 7.3 m/s^2, more than 0.25 g (2.45 m/s^2), so a 0.25 g trip to Jool can't end in that orbit. The
  planner refuses such trips (it asks for at least 1.1 times that gravity); the 0.25 g test flies to Duna (2.1 m/s^2 at
  60 km).
- The simulator moves a coasting ship (engine off, braking not yet due) along its orbit in big steps, as KSP's on-rails
  warp does, and uses 2 s steps (physics warp x100) in a full burn's cruise away from the planets; 0.02 s steps
  everywhere else. One Duna trip at 0.02 s throughout runs as a separate long test (`Category=LongRunning`).
- The simulation results are in the test output and the PR.

## Flying (2026-10-26)

`MechJebModuleFlipAndBurnAutopilot` flies the trip. MechJeb finds it like the window; it is enabled only while a trip
runs.

- **Start** is enabled only when the planner says the trip can be flown (infinite propellant counts as enough delta-v).
- **Every physics step** it calls the guidance with the ship's state relative to the Sun (KSP's orbit frame; Y and Z
  swap to get world space).
- **Pointing:** MechJeb's attitude controller, `attitudeTo(direction, INERTIAL)`.
- **Thrust:** MechJeb's thrust controller. Flip and Burn joins its users (and leaves them at every stop; MechJeb's
  controller always lists itself as one user, added when it starts, and drives the throttle only when it has more than
  that one, so after a stop the player's throttle is theirs again), sets the target throttle to full, and sets
  MechJeb's acceleration limit to the commanded acceleration, so the throttle falls as fuel burns off. MechJeb's limit
  reads each engine's live maximum thrust, which KSP Interstellar lowers when the drive's power or cooling falls short,
  so no extra trim is used (one was tried: when the drive was short of cooling it pushed the throttle up, which made
  more waste heat and less thrust). Outside the flip the
  engine fires only when the ship points within 5 degrees of the commanded direction. MechJeb's delta-v simulation
  isn't used (it crashed on two of the test ships).
- **The player's MechJeb settings:** the thrust and attitude controllers' settings are saved with MechJeb's own save
  routine when the trip starts and loaded back the same way when it ends or is aborted. A second save afterwards is
  compared with the first, and the window says whether they match.
  - In the game every MechJeb settings file was byte-identical before Start and after Abort except for two window
    heights MechJeb records whenever a window is redrawn:
    - Flip and Burn's own window (650 → 660 px: it shows the stop reason after Abort);
    - MechJeb's Maneuver Planner (238 → 223 px: its contents follow the ship's orbit).
  - No setting value changed. Fixing window heights would mean fighting MechJeb's window layout, so they are left as
    MechJeb records them.
- **Flip styles:** cut, flip, relight (engine off while turning, relight within 5 degrees) and skew flip (thrust while
  turning) come from the guidance.
- **Engine off means off.** Whenever no thrust is wanted (the cut in a cut, flip, relight; the coast; Abort and every
  other stop), each running engine's throttle is zeroed and the engine is shut down, the way KSP Interstellar's own
  safety cut-off does it. It is relit when thrust is wanted again, and after a stop it is left active at zero throttle,
  as it was at Start.
  - Found in the game: after a long warped cruise, the Kerbstein drive kept pushing about 1 g through a whole "cut" flip,
    and after an Abort, with the ship's throttle at zero. Its requested throttle stayed at the old value.
  - Short tests (temperature cheat on or off, 25x warp for 30 s) cut cleanly, so the trigger is the long cruise. Shutting
    the engine down cuts it whatever the cause.
  - Relighting: the engine is activated again (and the part force-activated, as KSP Interstellar does). While thrust is
    wanted, Flip and Burn also sets each running engine's own throttle to what MechJeb's acceleration limit computes
    (commanded acceleration over full-thrust acceleration). In the game the relit Kerbstein drive didn't pick up the
    ship's throttle by itself.
- **Stopping by itself**, with the reason in plain words:
  - out of fuel: thrust wanted but the engines give less than 10 % of it for 10 s, unless infinite propellant is on;
  - can't turn: the flip takes more than 4 half-turns at the estimated turn rate (at least 2 minutes), or the ship
    stays more than 10 degrees off the commanded direction, getting no closer, for 3 half-turns (at least 1 minute)
    while it should be thrusting;
  - off course: the guidance finds no way to the target for 10 s, or the arrival slips by more than 10 % of the trip
    (at least an hour).
- **Can't hold the acceleration** (not a stop): at full throttle the engines give less than 95 % of it for a minute.
  The autopilot re-plans the rest of the trip at what they can sustain, and tells the player (ADR 0002, "When the
  engines can't hold the acceleration"). It stops only if the trip can't be finished at that acceleration.
  - Found in the game: the Bebop Refueller's radiators can't shed the Kerbstein drive's waste heat at 1 g. Its heat bar
    climbs from 37 % to 89 % in about 90 s; above 90 % KSP Interstellar cuts the drive's power.
  - It settles at 0.80 g with the throttle at 100 % and the heat at 97.5 %.
- **Turn rate** (for the flip timing and the checks above) comes from MechJeb's torque and moment of inertia: a
  bang-bang half turn takes 2 sqrt(pi / alpha), giving an average rate of sqrt(pi alpha) / 2, capped at 10 deg/s.

### Time warp

`WarpPolicy`, run by Flip and Burn:

- **Engine firing:** physics warp only. KSP Interstellar drives fire at full thrust in normal warp, and stock engines
  can't fire in normal warp at all.
- **Coasting with the engine off:** normal (on-rails) warp, but only once the ship points where the braking will go,
  since it can't turn while on rails.
- **Turning** (the flip, a course correction) and the final circularizing burn run at normal speed.
- **Lead time:** the rate is capped so the next event (the flip, the braking burn, the arrival) is at least 30 real
  seconds away under physics warp and 60 under normal warp. Warp falls steadily as an event nears and is back to
  normal speed in time.
- **Physics warp rates:** during a trip Flip and Burn hands the game its own (1, 4, 10, 25): the stock top rate of
  4x is far too slow for a trip. The player's rates must never outlive the trip:
  - At Start `WarpRatesSnapshot` keeps the player's array itself and a copy of its values. With BetterTimeWarp installed
    that array *is* BetterTimeWarp's preset: it hands KSP its own array, so writing into the game's rates edits the preset
    for the rest of the session. Flip and Burn never writes into it; it replaces the reference.
  - Every way a trip ends puts the same array back with its Start values: Abort, arrival, every self-stop, leaving the
    flight scene (`onGameSceneLoadRequested`, before the scene is torn down), the ship being unloaded, MechJeb switching
    the module off, and the game quitting (the module's `OnDestroy`). Each step of a stop runs on its own, so one that
    fails can't skip putting the rates back. KSP doesn't save warp rates, and BetterTimeWarp saves its presets only from
    its own edit window, so nothing reaches a file.
  - Found in review (cycle 9): the log showed physics rates [1,2,3,25] after the coast proof's Abort. The test helper
    did it: it wrote 25 into the player's array (and so BetterTimeWarp's preset) before Start, and the autopilot
    faithfully put back what it found. The helper now writes rates only into the trip's own array while a trip runs,
    and checks after every Abort that the rates match those before Start, putting them back if not.
  - Checked in the game (`docs/test-evidence/rates-run.log`): before Start [1,2,3,4]; during the trip [1,4,10,25];
    after a minute of burn and Abort [1,2,3,4], the same array with the same values. Leaving for the Space Center
    mid-trip stopped the trip first and put them back. KSP's TimeWarp is a new object in each scene, and the Space
    Center and the next flight scene both had [1,2,3,4]. BetterTimeWarp's settings file was unchanged.
- **Highest physics warp: 10x** (`MaxPhysicsRate`), measured on Kris's machine with the Bebop Refueller, 45 parts,
  holding its acceleration:

  | Rate | Ship cool (temperature cheat on) | Ship hot (waste heat at 93 %) |
  |---|---|---|
  | 10x | steady: acceleration within 0.1 %, pointing within 0.01°, full 10x speed-up | steady |
  | 25x | steady: acceleration exact, pointing within 0.03°, full 25x speed-up | the Kerbstein drive overheated and exploded within 20 s (KSP's heating runs in coarse steps at high warp) |

  So 10x is the limit.
- **Hot parts:** physics warp is held to 4x when any part passes 60 % of its temperature limit, and to normal speed
  past 75 %. Past 90 % the autopilot re-plans at 80 % of the acceleration to cool down, and says so.
- **Losing the engine** (none running while thrust is wanted, for 5 s) stops the trip whatever the cheats, with a plain
  reason.

### Arriving (2026-10-27)

**What "arrived" means** (checked from the log in the cycle 11 trips):
- a circular parking orbit at the chosen altitude: periapsis and apoapsis both within ±5 % of it, and both above the
  atmosphere;
- the engine off, MechJeb's throttle and steering handed back, and the player's warp rates and MechJeb settings back
  exactly;
- out of time warp before the circularizing burn.

**The arrival report.** The window and an on-screen message say it in plain words: "Arrived at Duna: in a 59.9 × 60.2 km
orbit (planned: 60 km, circular). The trip took …, … than planned at Start (…). It used … of delta-v and has … left, … of
the … it started with." With infinite propellant on, the fuel line says fuel isn't counted.

**When a trip can't arrive cleanly, it makes the ship's orbit safe** rather than letting it crash or fly past
(`ArrivalSafety`, the guidance's safe-orbit mode):
- **The watch.** Inside the target's sphere of influence, every physics step, it looks for two things:
  - **Falling too fast to stop above the atmosphere.** A flip-and-burn arrival brakes at 99-100 % of the chosen
    acceleration by design, so braking at the chosen acceleration is never the test. The test is whether the ship could
    stop above the atmosphere at the most the engines can **keep up**:
    - With KSP's "ignore max temperature" cheat on, that is full thrust. Otherwise it is only what the trip has proven
      it can hold: its acceleration after any heat step-downs. The Bebop's drive gives 3.17 g on paper but holds about
      0.45 g.
    - It fires when stopping at that thrust would take more than 90 % of the height left above the atmosphere, and only
      if that thrust is at least twice the chosen acceleration. A normal approach can need up to about 1.2 times the
      simple stopping distance (Jool's pull, a curved path), so with less headroom a rescue can't be told apart from a
      normal arrival, nor do much more.
  - **Flying past:** outbound, unbound, and beyond twice the parking radius.
- **Watchdog stops near the target become rescues.** These are: off course, the arrival slipping, the engines unable to
  hold any useful acceleration, and a lower-acceleration re-plan that can't finish. Each makes the orbit safe first and
  then stops. Fuel out, a lost engine and a ship that can't turn still just stop: nothing can fly the ship.
- **The rescue.** It steers the velocity straight to circular at the current radius, the cheapest way to a safe orbit,
  climbing gently if below the parking orbit:
  - It burns at the sustainable thrust above, with the cycle 9 heat guard still running (`HeatGuard`, now shared by the
    trip, the rescue and the simulator). Warp is held at normal speed.
  - Then it stops and says why and where: "couldn't finish in the planned 60 km parking orbit: … It put the ship in a
    safe orbit around Duna instead, … km."
- **Simulator proof** (`ArrivalSafetyTests`, `ArrivalWatchSweepTests`):
  - **Three emergencies end in safe circular orbits at about the minimum delta-v:**
    - falling at 6 km/s from 3,000 km: 6.0 km/s;
    - flying past at 2 km/s from 2,000 km: 2.0 km/s;
    - falling at 300 m/s from 100 km: 0.37 km/s.
  - **The rescue and heat** (`Rescue_StaysWithinTheHeatLimit`). The case is a ship flying past Duna at 6 km/s from
    4,000 km, in a heat model of the Bebop's drive that sustains 0.45 g:
    - at the trip's nominal 1 g, the drive overheats before the orbit is safe;
    - at the 0.48 g the trip had proven, a safe 7,800 km orbit, with the hottest part at 48 %;
    - started too high at 1 g, the heat guard brings it down in time: 98 % at the peak, 8 step-downs.
  - **No false alarms.** The normal run's 8 simulated trips plus a sweep of 21 (`ArrivalWatchSweepTests`,
    `Category=LongRunning`, 1.5 min) all parked with the watch quiet: **29 simulated trips, 0 false alarms.** The sweep
    covers Duna, Dres and Eve; full burns with both flip styles and coast trips; 0.25, 0.45, 1, 2 and 3 g. All run with
    the watch at its most sensitive (engines keeping up exactly twice the chosen acceleration). The closest any came to
    the firing line was 74 % of it (Duna at 0.25 g).
- **A trip that couldn't arrive, found and fixed: fast coast trips braked too late.**
  - **What the sweep found:** coast trips with very large budgets flew past the target. Examples: Duna at 1 g with a
    681 km/s budget, coasting at 321 km/s; Dres at 0.45 g, coasting at 216 km/s. The watch fired correctly, but at
    71 km/s inside Duna's sphere of influence no rescue can stop the ship either.
  - **Why:** near the braking start, the coast plan's "time left" is the square root of a small difference of large
    numbers. At 320 km/s a 2 m/s error moves it by about two minutes. Step after step it receded (11 s, 12 s, 16 s, …
    547 s) while the course error grew to 16 km/s, so braking never started. A late "course correction" then braked on
    a fixed heading and missed.
  - **Fix:** once braking is less than ten minutes away, its start is latched. It may come earlier, never later, and
    the braking law corrects what is left.
  - **Proof:** all three now park exactly, to the second (periapsis 60.0 and 20.0 km, eccentricity ≤ 0.0002). The
    Bebop's own coast trips (about 50 km/s) never reached this regime.

**Proof in the game** (cycle 11, the Bebop Refueller from Kerbin orbit to Duna, 60 km parking orbit; `trip-*-run.log`):

| Trip | Orbit | Trip time vs plan at Start | Delta-v | Warp ended before arrival | Engine throttle while more than 5° off |
|---|---|---|---|---|---|
| A: full burn, 1 g, cut-flip-relight, cheats | 59.96 × 60.06 km, e 0.00014 | 70,981 s vs 70,914 s (+67 s) | 692.6 km/s used | 127 s, none while circularizing | 0 in 632 steps |
| B: coast, 0.4 g, no cheats, quicksave and quickload on the way | 59.91 × 60.19 km, e 0.00037 | 431,265 s vs 431,249 s (+16 s) | 64.0 km/s used, 9.20 km/s left (12.5 %) | 144 s, none while circularizing | 0 in 1,033 steps |
| C: full burn, 1 g, skew flip, cheats | 59.82 × 60.02 km, e 0.00025 | 70,981 s vs 70,914 s (+67 s) | 692.7 km/s used | 127 s, none while circularizing | 0 in 492 steps |

Every trip ended with the engine off, MechJeb's throttle and steering handed back (one thrust user, MechJeb's own, as
before Start), the player's warp rates back, and all 1,803 MechJeb settings files byte-identical to before Start. Trip A
was flown on an earlier build of this cycle; the changes after it (the one-step throttle lag, the delta-v reader, the
reserve check, the boil-off allowance) are covered by trips B and C on the final build.

### Firing while up to 5 degrees off (2026-10-27)

**Rule:** the engine fires at the full commanded throttle once the ship points within 5 degrees of the burn direction
(`EngineCommand`), and is held at zero beyond that.

**Question:** what does firing at up to 5 degrees off cost? Every physics step of the cycle 11 trips logged it: the thrust
pushed across the burn direction, and the thrust lost along it, while firing between 0.1 and 5 degrees off.

| When | Firing 0.1-5° off | Largest error while firing | Pushed sideways | Lost along the burn | Fired in that window |
|---|---|---|---|---|---|
| Trip A, after Start | 23 s | 4.64° | 5.5 m/s | 0.09 m/s | 1,768 m/s |
| Trip A, after the relight | 10 s | 4.67° | 2.9 m/s | 0.04 m/s | 1,403 m/s |
| Trip B, after Start | 21 s | 4.88° | 2.0 m/s | 0.04 m/s | 639 m/s |
| Trip B, steady (course corrections included) | 16 s | 4.57° | 0.7 m/s | 0.01 m/s | 31,738 m/s |
| Trip C, after Start | 23 s | 4.91° | 5.5 m/s | 0.09 m/s | 1,771 m/s |
| Trip C, after the skew flip | 9 s | 4.64° | 2.5 m/s | 0.03 m/s | 1,407 m/s |

A burn starts only a few times per trip: Start, the relight, a resume, a course correction. Each costs a few m/s sideways
and under 0.1 m/s of forward thrust, against trips of 64-693 km/s, so well under 0.01 % of the delta-v. The guidance
re-solves from the real state every step and absorbs the sideways push: trip A still arrived in a 60.0 × 60.1 km orbit for
a planned 60 km.

**Decision: keep 5 degrees, with no ramp.**
- Tightening it would make every burn wait while MechJeb settles the last degrees, for no measurable gain.
- Scaling the throttle down with the pointing error would add code and tuning for well under 0.01 %.
- The rule that matters, no thrust while more than 5 degrees off, is checked every physics step in the trips. A
  one-step lag found there is fixed (below).

**The one-step lag.** In trip B's first run the engine kept its burn throttle (0.126) for a single physics step after the
ship swung past 5 degrees mid-burn. The cause: MechJeb hands its throttle (from the previous target) to the ship each
physics step, and an engine that updates after the autopilot copies it. The autopilot now zeroes the ship's own throttle in
the same step it holds the engine at zero or cuts it.

### Carrying on after a reload

Done in cycle 10: a trip is saved with the ship, pauses when the player leaves it, and resumes, re-planned, when they
come back or load a save made during the trip. See ADR 0004.
