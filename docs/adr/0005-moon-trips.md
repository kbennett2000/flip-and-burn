# ADR 0005: Trips within a planet's own system

- Status: Accepted
- Date: 2026-11-04

## Context

Until cycle 14 Flip and Burn flew only between planets, and refused to start from a moon. Players asked for the hops
inside one planet's system: from a planet's orbit to one of its moons (Kerbin to the Mun or Minmus, Duna to Ike, Jool to
Laythe), from a moon back to its planet, and between sibling moons (the Mun to Minmus).

Everything up to now was planned and flown relative to the Sun, where gravity is a small correction: the Sun pulls a
ship near Kerbin at 0.006 m/s². Inside a planet's system it isn't small at all:

| Where | Pull | Against 1 g |
|---|---|---|
| A low Kerbin orbit (80 km) | 7.6 m/s² | 78 % |
| Kerbin, at the Mun's distance | 0.025 m/s² | 0.3 % |
| A low Jool orbit (220 km) | 7.3 m/s² | 74 % |
| Laythe's parking orbit (60 km) | 6.3 m/s² | 64 % |
| The Mun's parking orbit (20 km) | 1.35 m/s² | 14 % |

The trips are short, too: Kerbin to the Mun takes 35 to 60 minutes at 1 g.

Flown with the interplanetary guidance as it was, Kerbin to the Mun from eight points around a low orbit:
- three arrived, in bad orbits (one with its lowest point 17 km below the Mun's surface);
- three never flipped and were caught by the arrival safety net;
- two tried a detour around Kerbin and failed after 13 hours;
- one path went through Kerbin.

## Decisions

### 1. The frame is the parent planet

A trip whose start and target are in the same planet's system is planned and flown relative to that planet, which stands
still at the origin. Its pull takes the Sun's place in the planner and the guidance (the code keeps the "Sun" names for
"the central body"):
- its atmosphere top (or highest terrain) plus 1 km is the distance the trip keeps from it;
- each of its other moons is an obstacle, with its own safe radius.

Flying to the planet itself (from one of its moons), the planet is the target, standing still at the origin, and its
pull is counted once, as the target's.

A trip between planets is unchanged: the Sun is the centre.

Which trips are offered:
- **From a planet's orbit:** its moons, then the other planets.
- **From a moon:** its planet, then its sibling moons.
- **Out of scope for now:** a moon to another planet's system. The planner says so in plain words: "To fly to another
  planet, get into orbit around Kerbin first."

The frame a trip started in is saved with it (`TripRecord` format 2, `centre`), so a resumed trip keeps its frame even
inside the target's sphere of influence. A trip between planets is still saved as format 1, exactly as before, so
earlier versions read it. Moon trips are format 2, so earlier versions refuse them cleanly.

### 2. Gravity is allowed for along the path the ship really flies

Each guidance step solves the two burns without gravity, then aims off by what gravity adds over the rest of the trip.
Between planets that allowance is added up along a smooth curve from here to the arrival. Near a planet the smooth
curve is far from the real path, and the allowance is wrong by more than the braking can absorb.

For a moon trip the allowance comes from flying the planned burns with gravity: RK4, steps crowded toward the start and
end of each burn, worked out again every half second of game time. The pull of the moon being left is included. The
planner's estimate (`SystemTrip`) runs the same loop eight times from the departure, so its trip time and delta-v include
gravity. The coast planner uses it too, and a coast trip's guidance starts from the allowance its plan settled on: deep
in Jool's pull, the fixed-arrival solve has no answer without it.

### 3. Leaving a body: wait for the departure window

Leaving a low orbit with the target behind the body, a burn now would aim through it, or along the orbit. A burn along
the orbit slows the ship until it falls in, even though a straight line looks clear.

So the ship waits in its orbit, engine off and pointing where the burn will go, until it has come round. The window is
the first point, at most one orbit ahead, where both hold:
- the straight trip from there never comes closer to the body than where the burn starts (or 1.2 times its safe radius,
  for a ship already well above it);
- the burn's upward part is at least the body's pull there.

Waiting costs no fuel. It is at most one orbit: about half an hour in a low Kerbin orbit, an hour and a half in a low
Jool orbit. The
planner shows it ("Burn starts in", and why), and the trip time includes it. Time warp runs on rails while waiting.

While still in the moon's sphere of influence, the guidance checks every step that the burn doesn't aim through or close
past the moon. If it does, the ship climbs along its orbit until the line clears. In the simulator a plan swung to aim
through the Mun 8 seconds after a clear start.

### 4. Detours

If a path from the window still crosses the planet (a moon to a sibling on the far side), the trip goes around it in two
legs, as the Sun detour does. The waypoint is at least 1.2 times the planet's safe radius away, and far enough out that
the planet pulls no more than 10 % of the thrust. If a path would pass through another moon, the planner refuses for now
and says so ("The Mun is in the way right now … Try again in a few minutes"); the sweep never met one.

### 5. Arriving

The standard is the same as between planets: Pe and Ap within ±5 % of the parking altitude, the engine off, the controls
handed back, out of warp in time. The default parking orbit for an airless moon is 10 km above its highest terrain,
rounded up to 10 km (the Mun 20 km, Ike 30 km), and stays editable.

Short trips at high thrust to small moons needed four changes. All four apply to moon trips only, except the last two
bullets of "every trip" below.

**Re-tune at the start of braking.** When the flip (or the coast) ends and braking really starts, the braking deadline is
set so the law asks for exactly the planned braking. Through a 35-second flip the ship drifts unbraked. Left as it was,
the law started at its upper edge and slid to its lower one, braking too hard early: at Minmus it reached the parking
height 150 s early, then had 1.4 km/s of braking to lose.

**A wider braking band: 90-100 %** (98.2-100 % between planets). A trip of minutes, not days, can't absorb a small timing
error in a 2 % band. At Ike at 2 g the braking sat on the floor for its last minute and arrived 20 km low.

**Settle as soon as the orbit is reached.** Once the ship is within 2 % of the parking height, hardly climbing or
falling, and at most 3 seconds' braking above orbital speed, the last burn takes over. Only the orbit's shape matters on
arrival, not the point on it.

**Then a gentle trim.** At a small moon the last burn takes a second or two, too short to settle the leftover climb or
fall, and 1 % of orbital speed is a 5 % error in height. So the velocity is steered straight to circular at the current
height, within 0.2 m/s.

Found on the way, for every trip:
- **The flip.** When the flip is within a second and the next solution is a fresh search rather than the previous one
  carried on, the flip happens on the last solution. Near the flip the solution can stop existing a step early, and the
  search then finds a different trip (brake first) that never flips.
- **The last burn turns before it burns** if it has to turn more than 30°. Arriving slow needs half a turn, and thrust
  while turning pushes the ship the wrong way.
- **After a detour waypoint** the flip check forgets the first leg's solution, so the second leg doesn't flip on stale
  numbers.

The arrival safety net watches as before. Flying to the planet itself, the ship is inside its sphere of influence from
the start, and leaving a moon it can briefly be moving away from the planet fast enough to escape it. There the watch
starts once the ship is braking toward it (after the flip, or coasting to the braking burn). "Final approach" for the
planet itself means within ten parking-orbit radii.

### 6. Accelerations that can't do the trip are refused, with the minimum

| Rule | Example |
|---|---|
| Leaving a body: at least 1.1 times its pull where the ship is | 0.86 g from an 80 km Kerbin orbit; "or start from a higher orbit" |
| Arriving: at least 1.1 times the target's pull at the parking orbit (as before) | 0.70 g at Laythe's 60 km |
| A coast trip down to the planet itself: at least 1.5 times its pull at the parking orbit | 1.17 g for Kerbin's 80 km |

The last rule exists because the coast speeds up all the way down and leaves the braking to the end, where the pull is
strongest. From the Mun at 1 g (1.29 times Kerbin's pull at 80 km) every simulated coast trip arrived 5 km low after a
four-minute last burn. At 2 g they arrived within 0.3 %.

### 7. Short trips: warp, flip and heat

**Warp.** The rules are the same: physics warp only while burning and pointed, rails while coasting or waiting, and
always back to normal speed at least 30 real seconds before the flip and the arrival (60 before a braking burn). On a
40-minute trip at 10x that leaves most of the burning warped.

**The flip.** It takes about 35 s at 5°/s, a bigger share of a short trip. The plan counts it as a coast, the braking is
re-tuned when it ends (above), and the skew flip keeps thrusting through it as before.

**Heat.** The heat guard and the step-downs are per part and per second, so they don't depend on the trip's length.

### What the planner and the windows show

- **Fly to:** grouped. "Moons of Kerbin" and "Planets" from a planet's orbit; "The Mun's planet" and "Other moons of Kerbin"
  from the Mun, with the note about other planets.
- **New rows:** "Burn starts in", when the trip waits for its window, in the planner and the trip window.
- **New notes:**
  - why the trip waits, and that waiting costs no fuel;
  - "These numbers allow for Kerbin's gravity along the way";
  - the detour around the planet;
  - a moon in the way;
  - the minimum accelerations.
- **The trip window:** the new phase "waiting in orbit, engine off: coming round to the target's side".

### Coast trips within a planet's system

The coast planner can't yet find the very slow, cheap transfers inside a planet's strong pull. A coast trip there needs
roughly half or more of a full burn's delta-v. At 40 % of it, no coast trip fit from the Mun to Kerbin or from Jool to
Laythe, and the planner said so. At 60 % every route fit.

## Proof

### Simulator

Unit tests, plus the sweep `MoonTripSweepTests` (Category=MoonSweep). The routes:
- Kerbin to the Mun and to Minmus;
- the Mun to Minmus and to Kerbin;
- Duna to Ike;
- Jool to Laythe and to Tylo.

Each route is flown:
- as a full burn, cut-flip-relight, at 0.5 g (where allowed), 1 g and 2 g, from three points around the starting orbit;
- once with the skew flip;
- once as a coast trip with 60 % of a full burn's delta-v (at 2 g for the coast down to Kerbin).

Every case is flown twice, by a ship turning at 5°/s and one turning at 10°/s, with the game's engine rule (no thrust
while pointing more than 5° off, outside the skew flip). Result, on the final build (with the addendum below):
- **124 trips, all arrived as planned:** Pe and Ap within ±5 % of the parking altitude, clear of the planet and every
  moon (by at least 4.3 km). Worst at 5°/s: 2.0 % (Kerbin to Minmus, full burn at 2 g, 19.6 × 20.0 km). Worst at 10°/s:
  1.7 % (Jool to Tylo, full burn at 2 g, 29.5 × 30.2 km).
- **The safety net fired 0 times.**
- **Against the planner:** trip time −0.2 to +6.3 % of the estimate; delta-v used 7.4 % under to 2.2 % over (the estimate
  includes a 3 % margin).

A few of every kind (planet to moon waiting for its window, moon to planet with a skew flip, moon to moon, a coast trip)
run with the everyday suite.

### In the game

Test copy of Kris's saves, the Bebop Refueller (`docs/test-evidence/cycle-14-moon-trips.md`).

| Trip | Orbit | Time against the plan at Start |
|---|---|---|
| Kerbin to the Mun, full burn at 1 g, cut-flip-relight (waited 15 min for its window) | 19.81 × 20.05 km, planned 20 | +31 s (+1.0 %) |
| The Mun back to Kerbin, full burn at 1 g, skew flip (waited 22 min) | 79.80 × 80.14 km, planned 80 | +18 s (+0.5 %) |
| Kerbin to Minmus, coast trip at 1 g, fuel counted (lowered to 25 km/s so it had to coast) | 19.69 × 20.01 km, planned 20 | +21 s (+0.4 %), 14.1 % of the delta-v left |

The first two flew back to back in one launch, the coast trip in a second. All three used the "ignore max temperature"
cheat (the Bebop's drive holds about 0.45 g cool). A third launch flew the Bebop from its Kerbin orbit (about 740 km up)
to the Mun at 1 g with no cheats at all: the heat guard stepped it down three times (to 0.76, 0.60 and 0.48 g, the first
about 2.5 minutes into the burn, at 815 km), each with a re-plan and a message, and it arrived in a 20.0 × 20.0 km orbit,
59 minutes against 49 planned. In all three:
- the engine was off at arrival, and time warp was back to normal before the last burn;
- MechJeb's settings came back identical apart from window sizes;
- the engine was at zero whenever the ship pointed more than 5° off (outside the skew flip).

After each launch, MechJeb's, BetterTimeWarp's and KSP's settings were identical to before, and no other save changed.

## Addendum (cycle 15 review): the last burn, and braking into strong gravity

Found while proving trips flown in the background (ADR 0006), where the simulator first used the game's 5° rule.

**The last burn now holds the parking height.** A watched coast trip to Tylo reached 30 km about 1.25 km/s faster than
orbital speed. There the last burn can't hold the height while it brakes (the centrifugal push is about 17 m/s² against
Tylo's 7), so it ended at whatever height it had reached: 22.7 km at 5°/s. The trim now seeks the parking height
(climbing or sinking at up to 30 m/s, finishing within 1 % of the altitude), and the last burn's direction turns no faster
than the ship can. The simulator's watched trips use the game's 5° rule everywhere.

**Why it arrived so fast: braking with headroom.** The braking is steered by the arrival law, whose thrust changes along
the braking. The plan budgets the braking at a constant 99 % of the acceleration and the law only checks the thrust it
asks for now. Diving into a deep well, the end of the braking has to hold the ship up against the target's pull as well
as slow it, and needs more than the acceleration. So the law ran at full thrust and fell behind, and the last burn took
off the difference: 727 s of it at Tylo.

The braking is now planned below the acceleration where the target's pull at the parking orbit is a large share of it
(`CoastPlanner.BrakingFractionFor`): 99 % up to a quarter of it, sliding to 85 % from 0.4. The coast planner, the
moon-trip planner and the guidance share the figure, and the planet full-burn estimate is corrected for it. The band
below the braking keeps its width. The reserve the window shows counts the braking at its own share, and the trip
window's Acceleration row says why the aim is lower ("braking leaves spare push for Jool's gravity").

Speed above orbital when the last burn begins (`LastBurnReport`, 140 watched trips, moons and planets, both turn
rates), before and after:

| Arrival | Before | After |
|---|---|---|
| Jool to Tylo, coast, 1 g | 1,237 m/s (last burn 727 s) | 46 m/s (5 s) |
| The Mun to Kerbin, coast, 2 g | 1,209 m/s | 280 m/s (17 s) |
| Jool, full burn, 1 g (between planets) | 955 m/s (99 s) | 29 m/s (3 s) |
| Jool to Laythe, coast, 1 g | 189 m/s | 29 m/s |
| Duna, full burn, 0.25 g | 164 m/s | 7 m/s |
| All 140 | 8 over 500 m/s, 8 from 100 to 500 | none over 500, 6 from 100 to 500 |

29 m/s at 1 g (59 at 2 g) is the designed handover: the last 3 seconds' braking go to the last burn. The six left over
100 m/s are the Mun to Kerbin at 2 g, and coast trips into the shallow wells of Ike and the Mun (180 to 210 m/s, a
different and smaller cause, left for later).

What the planner says, before and after (trip time; delta-v):

| Trip | Before | After |
|---|---|---|
| Kerbin to Duna, full burn, 1 g | 32 h 10 m; 1,135 km/s | unchanged |
| Kerbin to Duna, full burn, 0.25 g | 64 h 19 m; 568 km/s | 66 h 55 m (+4.1 %); 545 km/s (−3.9 %) |
| Kerbin to Duna, coast (Bebop) | | unchanged |
| Kerbin to Jool, coast (Bebop), same delta-v | 1,502,511 s | 1,558,568 s (+3.7 %) |
| Kerbin to Jool, full burn, 1 g | 48 h 15 m; 1,706 km/s | 50 h 13 m (+4.1 %); 1,640 km/s (−3.9 %) |
| Kerbin to Jool, full burn, 2 g | 34 h 5 m; 2,409 km/s | 35 h 11 m (+3.2 %); 2,334 km/s (−3.1 %) |
| Kerbin to the Mun or Minmus, 1 g | | unchanged |
| The Mun to Kerbin, full burn, 1 g | 51 m 16 s; 20.6 km/s | 52 m 39 s (+2.7 %); 19.8 km/s (−3.8 %) |
| Jool to Laythe, full burn, 1 g | 1 h 19 m 40 s; 29.9 km/s | 1 h 21 m 17 s (+2.0 %); 28.9 km/s (−3.5 %) |
| Jool to Tylo, full burn, 1 g | 1 h 48 m 26 s; 50.9 km/s | 1 h 51 m 31 s (+2.8 %); 49.0 km/s (−3.7 %) |
| Jool to Tylo, coast, 1 g | 2 h 2 m 48 s | 2 h 8 m 23 s (+4.5 %) |
| The Mun to Kerbin, coast, 2 g | 44 m 21 s | 45 m 48 s (+3.3 %) |

Braking more gently takes a little longer and uses a little less fuel. Flown, the trips match these figures: Jool at
1 g in the simulator took 180,747 s against 180,756 planned. A coast trip has a fixed delta-v, so it coasts a little
longer instead.

**Coast plans count the braking as it is flown.** A coast plan fits its burns into the budget, so it has to know what
the braking costs. In a deep well the braking costs more than its planned share for its planned time, for two reasons
that come to about the same:
- moon trips: the arrival law starts at the planned share and uses the headroom at the end, finishing at full thrust
  (Tylo 0.92 of the acceleration on average for 0.85 planned, Laythe 0.88, the Mun to Kerbin at 2 g 0.95);
- between planets: the braking flies at its planned share, but about 10 % longer than the plan's timing, whose smooth
  gravity path misses the end of a deep well (Jool: 5,287 s against 4,774).

So the plan counts the braking's delta-v at halfway between its planned share and full thrust
(`CoastPlanner.BrakingFlownShare`). Planned against flown delta-v, before and after:

| Coast trip | Before the headroom change | Headroom, braking counted at its share | Counted as flown |
|---|---|---|---|
| Kerbin to Jool (Bebop) | +4.2 % | +4.8 % | +1.2 % |
| Kerbin to Duna (Bebop) | +1.3 % | +1.4 % | +1.4 % |
| Jool to Tylo, 1 g | | +0.8 % | −2.6 % |
| Jool to Laythe, 1 g | | | −5.1 % |
| The Mun to Kerbin, 2 g | | | −4.0 % |

Moon coast trips fly a little under their plan, as they always have (7.4 % under to 2.2 % over across the sweep).

**The halfway share is a fit to measured flights, not derived.** It was measured in the simulator over these coast trips,
each flown by a ship turning at 5°/s and one turning at 10°/s, with the same result at both rates (planned against flown):

| Coast trip | Acceleration | Braking planned at | Flown against plan |
|---|---|---|---|
| Kerbin to Jool (Bebop, between planets) | 1 g | 85 % | +1.2 % |
| Jool to Tylo | 1 g | 85 % | −2.6 % |
| Jool to Laythe | 1 g | 85 % | −5.1 % |
| The Mun to Kerbin | 2 g | 86 % | −4.0 % |

The shallow-well coast trips (Duna, Ike, the Mun, Minmus, the Mun to Minmus) brake at 99 % and are counted at it, as
before (−0.2 to +2.2 %). No other acceleration was measured for a deep-well coast trip, and no coast trip to Eve, Laythe
at 2 g, or an Outer Planets Mod planet.

It needs revisiting if any of these show up:
- a deep-well coast trip flying more than about 3 % over its plan (the plan's margin is 6 %; the 10 % reserve is kept
  only while the burns stay inside it), or the reserve shown at arrival more than a point off (ReserveShownTests);
- a different arrival law, braking band or gravity model for the plan, any of which changes how the braking is flown;
- coast trips at other accelerations, or to other deep wells, falling outside the range above.

The simulator's last-burn report (`LastBurnReport`) and the planned-against-flown figures in the sweeps are where it
shows. The
reserve the trip window shows uses the same figures (the arrival law's own forecast once braking), and still matches the
arrival to within a point (ReserveShownTests: Jool 0.99, Duna 0.50; while climbing out, 1.28 and 1.13).

**Braking below full thrust costs more fuel on the Kerbstein drive (cycle 15, in-game run 9).** KSP Interstellar's
fusion drives lose Isp at part throttle (Isp = full Isp × throttle^0.5). Holding a share s of the chosen acceleration
takes s of the throttle that acceleration takes, at any mass, so each m/s at that share takes s^-0.5 as much
propellant: braking at 85 % costs 8.5 % more per m/s than the ship's delta-v at the chosen acceleration, which is the
figure the planner, the guidance and the window count in. Nothing counted it, and the simulator didn't model it. In the
game the 0.4 g coast to Duna (trip R, fuel counted, braking at 85 %) showed 17.03 % in reserve through the coast and
arrived with 14.24 %; its braking burned about 9 % more than its delta-v. The 10 % held only because of the 6 % margin.

- `ShipPerformance.ThrottleCost(share, k)` = share^-k (1 for stock engines). The coast plan counts the braking at
  `CoastPlanner.BrakingCost` (the flown share times what each m/s costs there); a full burn's braking half and a moon
  full burn's braking the same way; the guidance's burning left and the arrival law's forecast (`ForecastDeltaV`) count
  each moment at its cost. The engines' exponent travels in `TripRequest` and `GuidanceConfig`.
- The simulator loses Isp at part throttle as the Kerbstein drive does (k = 0.5, `Flights.ThrottleCost`), so every sweep
  counts it.
- Two window fixes found on the way: a moon coast trip waiting for its departure window showed a full burn's delta-v as
  still to come (−36 % on the Tylo coast until the first burn); and the window took the guidance's figure as soon as the
  ship stopped climbing prograde, while still deep in Kerbin's pull, where it wasn't settled (at 1 g to Jool, a point
  high two minutes after Start). It now shows the plan's figure until the ship leaves the departure planet's sphere of
  influence.

Simulator, the Bebop with the Isp loss (k = 0.5), before and after:

| Coast trip (fuel counted) | Braking at | Before: arrived with | Before: window after the climb-out | After: arrived with | After: window after the climb-out | After: trip time |
|---|---|---|---|---|---|---|
| Kerbin to Jool, 1 g | 85 % | 10.9 % | up to 4.2 points high | 12.3 % | within 0.13 | +1.8 % |
| Kerbin to Duna, 0.4 g (trip R) | 85 % | 14.5 % | up to 4.6 points high | 16.4 % | within 0.40 | +1.9 % |
| Kerbin to Duna, 1 g | 99 % | 13.7 % | within 0.46 | 13.9 % | within 0.38 | +0.2 % |
| Jool to Tylo, 1 g | 85 % | 15.8 % | −36 % while waiting | 17.4 % | within 0.39 from the coast on | +0.0 % |
| Jool to Laythe, 1 g | 85 % | 17.5 % | | 17.8 % | 3.1 points low while speeding up | −0.5 % |

Every sweep passes on this branch with the Isp loss in the simulator: everyday 205/205; moon trips 124/124 (worst orbit
2.0 %; moon coast trips 4.8 % under to 2.0 % over their plan); planets at 10°/s 8/8; arrival watch 21 parked, no false
alarms; last burn passes; every physics step passes. One display blip remains: on the Tylo coast, for one guidance step
as a course correction starts, the burning left reads 9 km/s high (it would show for at most one 30-second window update).

Shallow wells barely move (braking at 99 % costs 0.5 % more). **Still open on this branch:** the figure shown at Start
(and, on a moon trip, while speeding up) comes from the plan with a fixed allowance for what trips use over their burns
(`ReserveWatch.TypicalCoastOverhead`, 2.7 %), and this planner's moon coast trips and the 0.4 g Duna coast fly 1.5 to
3.2 % under their burns, so it reads 3.6 to 5 points low (pessimistic) there. Cycle 15a's climb-out change narrows this
for trips between planets.

## Consequences

- Moon trips plan and fly with the same windows, safety net, heat and warp rules, pause and resume as trips between
  planets.
- A moon trip's planner estimate searches for the departure window and flies the burns with gravity eight times: 9 to 90
  ms on a desktop (a coast plan 20 to 180 ms), against a few milliseconds between planets. The planner reuses a moon
  trip's estimate for 5 seconds, counting its times down meanwhile.
- A trip between planets is unchanged apart from the three guidance fixes that apply to every trip, and braking with
  headroom where the target's pull is strong (Jool at 1 g; Duna at 0.25 g).
- **Not yet:** a moon straight to another planet's system; a detour around a moon in the way; the cheapest slow coast
  transfers inside a planet's pull.
