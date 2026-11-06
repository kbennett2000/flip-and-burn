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

To be filled in as stage 1 is built and proven.
