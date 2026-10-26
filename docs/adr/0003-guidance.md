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

### Room for a coast phase

`TwoArcSolver` already takes a coast time between its arcs (the flip uses it). A burn-up, coast, burn-down profile
would choose a longer coast and add a Coast phase between Accelerate and Flip; the steering laws don't change. It is
not built.

## Consequences

- Every step re-plans from the actual state, so the simulator's Sun gravity, planet motion, turn-rate limit and large
  warp steps are all absorbed without special cases.
- Acceleration stays within 2 % of the chosen value outside the flip: exact before it, 98.2-100 % after it.
- **Limit:** the final approach needs the chosen acceleration to beat the target's gravity at the parking orbit. Jool's
  pull at 220 km is 7.3 m/s^2, more than 0.25 g (2.45 m/s^2), so a 0.25 g trip to Jool can't end in that orbit. The
  planner should refuse such trips (a later cycle); the 0.25 g test flies to Duna (2.1 m/s^2 at 60 km).
- The simulation results are in the test output and the PR.
