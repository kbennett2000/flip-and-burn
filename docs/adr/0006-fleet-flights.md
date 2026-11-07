# ADR 0006: Fleet flights (trips that fly on while the player is elsewhere)

- Status: Proposed (stage 1 in progress)
- Date: 2026-11-06

## Context

Until now, leaving a ship partway through a trip paused it (ADR 0004): the engine was cut and the trip waited for the
player to come back. Kris wants a fleet: several ships on trips at once, flying on while he flies something else, and
ships sent on trips from the Tracking Station.

## Kris's decisions

1. Leaving a ship partway through a trip never pauses it: switching ships, the Space Center, the Tracking Station, all
   of it. The trip keeps flying on its own. Pause still stops a trip if the player presses it.
2. While the player is away, the ship flies at the acceleration it had when the player left: any heat step-downs from the
   watched part stay, and it never steps back up.
3. When a ship the player isn't flying arrives, time warp drops to normal and a message names the ship, where it arrived
   and the orbit.
4. (Stage 2) Dispatch from the Tracking Station. Heat for a dispatched trip: if that ship's last trip stepped down for
   heat, use that lower figure; if it has never flown a trip, use the player's choice with a yellow note saying heat isn't
   checked for ships you aren't flying.

## Decisions

### 1. The same guidance, flown in a small simulation (`BackgroundFlight`)

An unloaded ship has no physics in KSP: it sits on its orbit (on rails). So the trip is flown by Flip and Burn itself,
with the very guidance a watched trip uses (`FlipAndBurnGuidance`), in a small simulation advanced to the game clock:

- **Gravity:** the trip's centre (the Sun, or the planet for a moon trip, always), the target, the body being left and,
  on a moon trip, the planet's other moons (each inside its sphere of influence). This is the simulator sweeps' model.
- **Steps:**
  - 2 s while burning in the cruise, far from bodies and events;
  - 0.1 s near a body or an event;
  - 0.02 s (a physics step) for the final approach and the last burn;
  - coasts and waits on Kepler orbits in steps of up to an hour, closing in on the next burn.
- **The live autopilot's rules:** the ship turns at its own turn rate, captured from the live ship. The engine fires
  only when the ship points within 5° of the burn direction, except during the flip (`EngineCommand`). The arrival
  watch is the same: a trip that would fly past the target makes the orbit safe instead, as does a plan lost for 10 s.
- **Fuel:** dm = m·a·dt / ve, with the exhaust velocity falling at part throttle for the Kerbstein drive (ADR 0002).
  Electric charge: what the engines draw beyond what the ship makes, never below the batteries' 10% floor.
- **Endings:** arrived (the parking orbit), a safe orbit (with the reason), or out of fuel (the ship coasts).

The simulator runs about 35,000 to 45,000 steps a second. A one-g trip to Duna is about 260,000 steps; a coast trip to
Jool, 1.2 million.

### 2. The fleet controller (`FleetFlights`)

A KSP add-on present in every scene, acting in flight, the Space Center and the Tracking Station:

- **Finding trips:** every second it looks for ships with a saved trip that isn't paused by the player, carries the
  figures needed to fly on (below), and isn't the ship being flown.
- **Starting:** for each, it re-plans the way a resume does (`TripReplanner.Check`, keeping a coast trip's arrival), from
  the ship's orbit at the moment the trip was last flown, and builds a `BackgroundFlight`.
- **Every frame** (4 ms of wall-clock time, shared, the trip furthest behind first) it advances each flight to the game
  clock and writes it back:
  - the orbit, around the body whose sphere of influence the ship is in (`Orbit.UpdateFromStateVectors`, the orbit
    snapshot too for an unloaded ship);
  - the propellant burned, out of the tanks by each propellant's share of the mass, and the electric charge (in the
    parts of a loaded ship, the saved part resources of an unloaded one);
  - the saved trip: delta-v used, last flown, the arrival, departed, facing and the handover state.
- **Keeping up with time warp:** in the cruise, far from any body and an hour or more from the next event, the flight
  steps up to a minute at a time (2 s otherwise, 0.1 s near a body, 0.02 s for the arrival). If a trip still falls more
  than a minute behind the game clock, or its arrival is a few real seconds away at the warp in use, time warp is
  stepped down a notch at a time until it keeps up. The first in-game run, without this, flew a Mun arrival 17 days of
  game time late at 100,000x: the warp drop and the message came long after the ship had arrived.
- **Scene changes:** the flights are kept across them, and dropped when a save is loaded (the save's trips are flown
  from what it says).

### 3. Handing over and back

- **Player to fleet:** leaving a trip (switching ships, a scene change, the ship unloading) calls `HandOver` instead of
  `Pause`. The ship is given back as at a pause, and the trip stays in the record with its away figures.
- **The away figures** (`AwayShip`), read from the live ship: turn rate, full thrust, exhaust velocity, Isp throttle
  exponent, dry mass, propellant mass shares, charge per newton, charge made, the charge floor and facing.
  - Every save made while flying writes them too. KSP saves just before leaving for the Space Center or the Tracking
    Station, and the fleet flies the trip from that save.
- **Leave it flying:** KSP blocks saving, switching ships and leaving flight above 0.1 g. So during a burn a player
  couldn't leave without pressing Pause, which (decision 1) stops the trip. The **Leave it flying** button turns the
  engine off for up to a minute so the player can go. If they don't leave, the trip carries on, as it does at once
  with **Stay and fly**.
- **Pause stays a stop:** a player's Pause is saved in the record (`pausedByPlayer`), and the fleet leaves those trips
  alone. When the player comes back, the trip carries on, as before (ADR 0004).
- **Fleet to player:** when the player takes a ship over, the fleet writes the newest state and steps aside. The
  autopilot's existing resume path re-plans from where the ship is, and says how long the trip flew without the player.
- **The jump** in position and velocity at each handover is measured and logged: from the handover state in the record
  to the ship's orbit at that moment.

### 4. Saving and compatibility

The trip lives where it always has, in the ship's MechJeb settings (MODULE MechJebCore, MechJebLocalSettings,
MechJebModuleFlipAndBurnAutopilot, FLIPANDBURN_TRIP). So saving, loading and quitting carry it, and removing the mod
mid-trip leaves a ship coasting on its orbit. The new values are optional and the format number is unchanged, so earlier
versions read the record as before (and resume it when the player comes back, as they did).

### Every trip ends with the trim

The gentle last trim that moon trips have (ADR 0005: settle the height and the whole velocity) now ends every trip.
Found by the fleet's first Dres arrival: a slow coast from Duna arrived at orbital speed along the orbit but still
sinking a few m/s, which the last burn's check didn't look at (18 × 22 km for a 20 km orbit). Trips between planets
otherwise arrive as before (planet sweep worst 0.9 %).

### Known limits

- A ship inside the physics range of the one being flown (a few hundred metres) is moved by KSP, not the fleet. It waits
  until it is out of range or on rails.
- Heat isn't checked while away (decision 2): the trip flies at the acceleration it held when the player left.
- **Follow-up: slow approaches to small planets.** A coast from Duna to Dres on a small budget (2 % of a full burn's
  delta-v) brakes too little early: near the end the path bends in, the ship dips below the parking height and the
  braking law asks for twice the acceleration. Watched, the arrival safety net fires (down to 9 km over Dres, then a
  19.8 × 20.2 km orbit); in the background the trim brings it to 19.8 × 20.0 km. Either way it uses about 20 % more
  delta-v than planned. It predates fleet flights (watched trips have it too) and needs its own guidance work; the
  sweeps now include the case in the background.

## Evidence

- **Simulator sweep** (`BackgroundFlightTests`, category BackgroundSweep): 15 routes, planets and moons, each at a
  5°/s and a 10°/s turn rate, flown in the background:
  - 5°/s: 15 of 15 arrive to the standard, worst 1.3% (Jool to Tylo coast, 29.7 × 30.4 km);
  - 10°/s: 15 of 15, worst 1.5% (Kerbin to Minmus coast, 19.7 × 20.0 km);
  - propellant matches the rocket equation for the delta-v flown within 0.01%;
  - handovers (watched, then background, then watched; a moon trip left after its window) arrive to the standard.
- **A test bug found on the way:** for the Mun back to Kerbin, the background test's set-up left out Kerbin's pull (the
  centre, when it is the target). The trip still "arrived", because the last burn sets the circular speed at the parking
  height. Fixed: the centre always pulls, and the trip now takes 0.6% longer than planned (11% before).
- **In the game:** see `docs/test-evidence/cycle-15-fleet-flights.md` (to come).
