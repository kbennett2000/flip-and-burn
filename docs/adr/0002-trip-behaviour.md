# ADR 0002: How a Flip and Burn trip behaves

- Status: Accepted
- Date: 2026-10-26
- Decided by: Kris (product decisions 1-4); items under "Standing" were set earlier

## Decisions

1. **A trip ends in a circular parking orbit around the target planet.** The player sets the parking orbit's altitude
   in the Flip and Burn window.
2. **Flip and Burn runs time warp itself.** It warps through the cruise and drops to normal speed before the flip and
   before arrival. Long burns use BetterTimeWarp (see the README).
3. **No trip starts that the ship can't finish.** If the ship can't hold the chosen acceleration, or lacks the delta-v
   for the trip, the trip is refused: the planner shows the shortfall and the start button stays off.
4. **Default flip style: cut drive, flip, relight.** The other choice is a skew flip, turning while still thrusting.

### Standing

- Targets are planets that orbit the Sun. Moons are never targets.
- Default acceleration is 1 g = 9.81 m/s².

## What the trip estimate assumes

The trip calculator (`src/FlipAndBurn.Core/TripCalculator.cs`) estimates a trip before it starts. It is the number
behind decision 3: the planner compares the estimate's total delta-v and acceleration against what the ship has.

- **Gravity is ignored during the burn.** At 1 g the Sun's pull is under 0.1 % of the thrust at Kerbin's distance, so
  a straight-line, flip-at-the-midpoint profile is a good estimate. The result carries a warning when the chosen
  acceleration is less than 10 times the Sun's pull anywhere on the path; there the estimate is rough.
- **The planet moves during the trip.** The calculator aims at where the planet will be on arrival and iterates on
  the arrival time until it converges.
- **Arrival point.** The burn ends at rest relative to the planet, at the parking orbit's radius on the near side of
  the planet. Entering the parking orbit is then one more burn of the circular orbit speed there.
- **Velocity matching is part of the burn.** The ship's start velocity and the planet's velocity usually differ. The
  burn spreads that difference evenly over the trip on top of the flip-at-the-midpoint profile, so the two halves of
  the burn are not exactly equal in strength. The calculator picks the trip time so the stronger half is exactly the
  chosen acceleration.

## The Sun can be in the way

A straight-ish path to a planet on the far side of the Sun can pass through the Sun or skim it. The calculator
measures the path's closest approach to the Sun and flags the trip when it comes within the **Sun safety distance:
4.0 × 10⁹ m from the Sun's centre** (about 15 solar radii).

Why that number:

- It sits just inside Moho's perihelion (4.21 × 10⁹ m), the closest any stock planet gets to the Sun. Every trip to a
  real planet can therefore end outside it, and no ship is asked to go nearer the Sun than the most sun-baked stock
  planet already sits.
- Solar flux there is about 15,700 W/m², 11.6 times Kerbin's (KSP's `Physics.cfg`: `solarLuminosityAtHome = 1360`
  W/m² at Kerbin's orbit). A flat plate facing the Sun reaches about 610 K in equilibrium, well under the temperature
  limits of stock parts.
- OPM does not change Moho, so the number holds with Outer Planets Mod installed.

The distance is a parameter of the calculation, so it can be tuned once there is in-game evidence.

**Guidance will need a detour for these trips.** This ADR only requires that they are detected and flagged. How to
route around the Sun (for example, a waypoint off to one side, or a curved burn) is a later decision.

## Consequences

- The planner can show trip time, time to flip, peak speed and total delta-v before the player commits.
- The estimate needs refining by real guidance in flight: gravity, the planet's pull on arrival and the exact capture
  are MechJeb's and the autopilot's job.
- Trips that pass near the Sun are visible to the player but cannot fly safely until the detour exists.

## Addendum (2026-10-26): default parking altitude

The planner suggests a parking altitude for each target; the player can change it in the window. The suggestion comes
from the planet's own data, read at runtime (`ParkingOrbit.DefaultAltitude` in `src/FlipAndBurn.Core`):

- **With an atmosphere:** 10 % above the top of the atmosphere.
- **Without one:** 10 km above the highest terrain (the PQS maximum radius minus the planet's radius), or 10 % of the
  radius if the terrain height isn't known.
- Then rounded up to the next 10 km, at least 10 km, and kept below half the distance from the surface to the edge of
  the sphere of influence.

What it gives in Kris's install (logged by the test harness on 2026-10-26, `docs/test-evidence/test-run.log`):

| Planet | Atmosphere top | Highest terrain | Default parking altitude |
|---|---|---|---|
| Moho | none | 8.2 km | 20 km |
| Eve | 90 km | | 100 km |
| Kerbin | 70 km | | 80 km |
| Duna | 50 km | | 60 km |
| Dres | none | 7.1 km | 20 km |
| Jool | 200 km | | 220 km |
| Sarnus (OPM) | 580 km | | 640 km |
| Urlum (OPM) | 325 km | | 360 km |
| Neidon (OPM) | 260 km | | 290 km |
| Plock (OPM) | none | 4.2 km | 20 km |

The flip style is chosen in the window (cut, flip, relight by default; skew flip as the other choice) but doesn't change
the estimate yet: the estimate treats the flip as instant.

## Addendum (2026-10-26): trip type, and infinite propellant

Decided by Kris:

1. **Trip type.** The planner offers "Full burn" (the default) or "Coast in the middle". Coast in the middle burns at
   the chosen acceleration, coasts weightless, then burns to stop. It plans the fastest trip the ship's delta-v allows
   while keeping 10 % of the ship's delta-v in reserve, and shows the burn time at each end, the coast time and the
   total trip time. When a full burn is short on delta-v, the planner suggests the coast trip and says how long it
   would take, instead of just refusing. It still refuses if even a coast trip can't be done.
2. **Infinite propellant.** When KSP's infinite propellant cheat is on, the planner says so in a plain line and doesn't
   report a delta-v shortfall. Only the test helper turns the cheat on, only during test runs on the FlipAndBurn-Test
   copy, and the cheat must not stick in Kris's settings or his other saves.

How these are read in the code:

- **"The ship's delta-v"** is the delta-v while holding the chosen acceleration (the number the planner already shows,
  which accounts for KSP Interstellar Extended's throttle rule). The reserve is 10 % of that; a coast trip may use the
  other 90 %, burns, course corrections and final circularising included. Flying the trip costs a few percent more than
  its two planned burns, so the burns are planned 6 % under that and the planner shows the delta-v needed with the
  margin (ADR 0003, "The coast phase").
- **The flip on a coast trip** happens during the coast, with the engine off, so the flip style doesn't matter; the
  window says so instead of offering the choice.
- **A coast trip that would need a Sun detour** isn't offered yet: the planner says so plainly. The detour stops beside
  the Sun and doubles the burns, which the coast budget isn't built for.
- **Gravity at the target.** A trip ends in the parking orbit only if the chosen acceleration beats the target's gravity
  there (ADR 0003). The planner refuses unless the acceleration is at least 10 % more than that gravity, and says what
  acceleration or parking altitude would work.
- **The Sun detour** is now part of the estimate: when the Sun is in the way, the trip time and delta-v shown are for
  the two legs the autopilot will fly (start to waypoint, waypoint to target).

## Addendum (2026-10-26): engines and stages

Kris: the mod must work with any engine type, not just the Epstein drive.

**Stages (decided this cycle).** A trip burns on the engines that are active now, the current stage, and the fuel
those engines can reach. If the ship has more stages below the current one (stages with engines or decouplers still to
fire), the window says so plainly and says the planner counts only the current stage. Staging during a trip isn't
planned: a torch ship is usually one stage, and planning drops would mean guessing when the player stages.

**How the engines are read** (`ShipPerformanceReader` in the plugin, `ShipEngines` in the core library):

- Every active engine is read from its own engine module: vacuum thrust (after the thrust limiter), vacuum Isp, and
  its propellant mix. That is what KSP's own delta-v readout reads, so for stock engines (multi-mode, nuclear and
  electric included) and other mods' engines built on the stock engine modules (Near Future Launch Vehicles, for
  example) the figures are KSP's.
- All active engines burn together. When one propellant runs out, the engines that need it stop and the rest carry on
  with less thrust: each stretch is a burn phase. Delta-v while holding an acceleration counts a phase only if its
  engines can hold that acceleration.
- **Overridden where KSP is proven wrong:**
  - KSP Interstellar Extended's fusion drives (the Kerbstein drive): stock leaves them out, and doesn't know their Isp
    falls at part throttle (ADR 0002 planner work, unchanged).
  - Electric engines: stock assumes full thrust. Here the ship's power supply limits it. Stock solar panels, generators
    (RTGs) and running converters (fuel cells) count; other mods' power plants don't. If the supply can't keep up, the
    window says so and the planner uses the share of full thrust it allows. Batteries are mentioned but not counted:
    they last minutes against a burn of days.
- **Air-breathing modes** (a RAPIER on air) don't work in space: left out, with a note to switch modes.
- **Engines Flip and Burn doesn't know** are read the same way, from their engine module, and flagged; the window says
  the figures are estimates. "Doesn't know" means a subclass of the stock engine module, or a part carrying another
  mod's engine controller. (Whether KSP includes the engine in its own delta-v, includeinDVCalcs, is no guide: on
  Kris's install a mod turns it off even for stock engines.)
- **Stages below** means stages with engines or decouplers still to fire.

## Addendum (2026-10-26): when the engines can't hold the acceleration; above 1 g

Decided by Kris:

1. **Overheating doesn't stop the trip.** When the drive can't hold the chosen acceleration because of heat, or any
   other limit that isn't running out of fuel, Flip and Burn:
   - drops to the highest acceleration the ship can actually sustain;
   - re-plans the rest of the trip at it (flip timing, arrival, delta-v);
   - keeps flying;
   - tells the player in the window and with an on-screen message what happened, the acceleration it now holds, and how
     the trip time changed.

   It stops only if even the reduced acceleration can't finish the trip safely (for example, it can no longer beat the
   target's gravity at the parking orbit), and says why.
2. **1 g is not a maximum.** Ships that can go faster may choose it. The acceleration box takes anything above zero up
   to the ship's own maximum, with no cap at 1 g anywhere (planner, guidance, autopilot). 1 g stays the default.

How this is read in the code (`MechJebModuleFlipAndBurnAutopilot`):

- **"Can't hold":** at full throttle the engines give less than 95 % of the acceleration, but at least 10 % (less
  than that is the out-of-fuel case), for a minute of game time outside the flip.
- **"Sustainable":** 95 % of what they give at full throttle, rounded down to 0.01 g. With KSP Interstellar's heat
  rule the drive's thrust at full throttle is what its cooling can carry, and the 5 % leaves the throttle a little
  room.
- **Re-plan:** from the ship's current state, with the trip calculator's feasibility checks (including the target's
  gravity at the parking orbit). A coast trip keeps its budget less the delta-v already burned.
- **Found in the game:** the Bebop Refueller at 1 g overheats in about 100 s, settles at 0.80 g, and is re-planned at
  0.76 g.
