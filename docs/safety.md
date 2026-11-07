# Safety

[← Back to the README](../README.md)

Flip and Burn moves ships very fast, and it tries hard never to lose one. This page covers what it refuses to start,
what it watches during a trip, and what it does when something goes wrong.

## What it won't start, and why

The **Start** button only lights up when the trip can really be flown. When it can't, a yellow note in the planner says
why, in plain words, and usually what to change.

<p align="center">
  <img src="images/planner-refused.jpg" alt="The planner refusing an acceleration the ship can't reach, with the reason in yellow" width="55%">
</p>

| The planner says | What to do |
|---|---|
| You're landed, in the atmosphere, or not in a stable orbit yet | Get into a stable orbit first. Trips start from orbit. |
| You're orbiting a moon and want another planet | From a moon, Flip and Burn flies to its planet and the planet's other moons. For another planet, get into orbit around this moon's planet first. |
| No engine is active | Stage or activate your main engine, so Flip and Burn can read its thrust. |
| This ship can reach only *X* g | Lower the acceleration to what the ship can do. |
| Not enough delta-v | The ship doesn't have the fuel for a full burn. Try **Coast in the middle**; the planner suggests it with its trip time. |
| The destination's gravity is too strong at this acceleration | Choose at least the acceleration it names, or a higher parking orbit. |
| *Kerbin*'s gravity where the ship is now is too strong to leave | Moon trips: leaving a low orbit, the engines have to out-pull the planet or moon there. Choose at least the acceleration it names, or start from a higher orbit. |
| A coast trip down to *Kerbin* needs at least *X* g | Moon trips: coasting down to the planet speeds the ship up all the way, so the braking needs more push. Choose that or more, or **Full burn**. |
| *The Mun* is in the way right now | Moon trips: the path to the target would pass too close to another moon. Try again in a few minutes. |
| A coast trip can't go around the Sun yet | The Sun is between you and the target. Choose **Full burn**, or wait a while. |

**When the Sun is in the way** of a full burn, the trip doesn't fly through it: the planner routes it around the Sun in
two legs, and says so. The numbers it shows include the detour.

**Leaving a planet with the target on its far side,** the ship doesn't burn through the planet or waste fuel burning
round it. It climbs until it's escaping, then coasts outward with the engine off until the way to the target is clear
(the window says so), and only then turns and fires. With a weak engine it keeps climbing along its orbit instead, until
it is far enough out that the planet's pull can't bring it back. The planner's fuel figures include all of this.

**On a moon trip, the planet and its moons are kept clear the same way.**
- **Leaving a low orbit with the target behind the planet (or moon):** the ship waits in its orbit, engine off, until it
  has come round to the target's side, and burns from there. Waiting costs no fuel; it's never more than one orbit, and
  the planner says how long.
- **The planet between two moons:** the trip goes around it in two legs, far enough out that its pull stays small.
- **Leaving a moon:** if the burn would ever aim through the moon, the ship climbs along its orbit until the way is clear.

## During the trip

- **The engine only fires when the ship points the right way.** If the ship is more than 5° away from its burn
  direction (at Start, after a flip, after a resume), the throttle is held at zero until it lines up. The skew flip is
  the one exception, since firing through the turn is the whole idea.
- **Overheating is handled, not ignored.** Time warp slows as parts warm up. If a part reaches 90% of its temperature
  limit, the trip drops to a lower acceleration and re-plans. See [Ways to fly](ways-to-fly.md#what-overheating-does).
- **It stops and tells you** if it can't carry on: out of fuel, an engine lost, the ship can't turn fast enough to flip,
  or it has drifted too far off course to reach the target. The engine is cut and the window says what happened.
- **Abort** is always there. It cuts the engine, stops time warp, and hands the ship back to you with your settings as
  they were.

## While you're away

A trip you leave [flies on without you](saving-and-pausing.md#fleet-flights-trips-that-fly-without-you), with the same
care:

- **The same steering and the same safety net.** Planets and moons are kept clear, and an arrival that goes wrong ends
  in the safest orbit the ship can reach, as below.
- **No stepping back up.** It flies at the acceleration it held when you left, lower if the engines had overheated.
  Heat isn't checked for a ship you aren't flying, so this is the figure the trip has proven it can hold.
- **Warp stops** when it arrives or has to stop, and a message names the ship, where it is and its orbit.

## The arrival safety net

The most dangerous moment is arriving: the ship comes in fast and has to stop above the atmosphere. Flip and Burn
watches that the whole way down. If it finds the ship:

- **falling too fast** to stop above the atmosphere at the thrust it can keep up, or
- **about to fly straight past** the planet,

it doesn't carry on with the plan and hope. It turns the ship and burns into the **safest orbit it can reach** around
the planet, then stops and tells you, with the orbit it got:

> Flip and Burn couldn't finish in the planned 60 km parking orbit: … It put the ship in a safe orbit around Duna
> instead, … Fly the last step yourself, or plan a new trip from here.

On a normal trip the safety net stays out of the way. In testing it stayed quiet on every normal arrival: 29 simulated
trips to Duna, Eve, Dres and Jool at accelerations from 0.25 g to 3 g, and every trip flown in the game.

## The 10% fuel reserve

A **coast trip** always keeps 10% of your ship's delta-v unused. You arrive with some fuel left for whatever you came to
do: landing, moving to a different orbit, or coming home. The planner shows the amount as **Kept in reserve**.

Slowing down into a planet with a strong pull, like Jool, costs more than the speed you arrive with: the ship gains speed
as it falls in. The planner prices the braking from that, and when a fast trip would eat into the 10%, it offers a
slower one that doesn't.

On top of that, the plan sets aside extra for steering and course corrections. Most trips don't need all of it, so
they usually arrive with a bit more than 10%. As the trip flies, the trip window shows what the ship will most likely
have left at arrival, worked out from the burning still to do. In testing it matched the arrival to within a point.

If that figure heads below 10% for any reason, the window says so in plain words, with the reason.

A **full burn** has no reserve built in: it either has the fuel for the trip or the planner won't start it.

## Boil-off

Some fuel tanks lose propellant over time when they aren't kept cold. KSP Interstellar's cryogenic tanks, for example,
boil off liquid deuterium once the ship runs out of power for their coolers: electric charge, and the reactor's power
store behind it. In a day-long test, once both had run out, the Bebop Refueller lost about 0.5 kg of deuterium an hour:
about 3 kg per 6-hour Kerbin day, or 12 kg per Earth day. On a coast trip of several weeks, that adds up.

Flip and Burn **measures** this instead of guessing. Whenever the planner window is open or a trip is flying, it notes
how much fuel disappears while no engine is firing, and remembers the rate for that ship. If the loss would make a real
difference, the coast plan allows for it, so you still arrive with your 10%, and the planner tells you. The picture is
from a shorter, 2½-hour measurement that started with cold tanks, so it came out lower: about 1 kg a Kerbin day. The
longer a ship is watched, the closer the figure gets.

<p align="center">
  <img src="images/planner-boiloff.jpg" alt="The planner showing a measured boil-off rate, and the delta-v set aside for it" width="55%">
</p>

If your tanks don't lose anything (and on most ships they don't, as long as the power stays on), you'll never see this
line. If the loss is so large that no coast trip can arrive with 10% left, the planner says so before you start.

Next: [engines →](engines.md)
