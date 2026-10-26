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
