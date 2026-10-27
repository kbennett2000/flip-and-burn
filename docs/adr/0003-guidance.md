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
- **Thrust:** MechJeb's thrust controller. Flip and Burn joins its users, sets the target throttle to full, and sets
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
  other stop), each running engine's actual throttle is set to zero at once. Engines follow the commanded throttle at
  their own pace, and the Kerbstein drive winds down so slowly that in the game it kept pushing about 1 g through a
  whole "cut" flip, and was still at 1 g three seconds after an Abort. On a coast trip the flip is the turn to the braking direction during the coast,
  with the engine off.
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
- **Physics warp rates:** during a trip Flip and Burn sets its own (1, 4, 10, 25), and puts the player's
  (BetterTimeWarp's) rates back afterwards.
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

### Next: carrying on after a reload

A trip doesn't survive the game being saved and loaded: MechJeb doesn't save the autopilot's enabled state, and the
guidance's plan lives only in memory. After a reload the window shows that no trip is running and says a trip must be
started again. Saving the trip (target, settings, the fixed arrival time of a coast trip) with the vessel and resuming
it is the next item.
