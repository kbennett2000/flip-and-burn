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

- **Fly to:** grouped. "Moons of Kerbin" and "Planets" from a planet's orbit; "Mun's planet" and "Other moons of Kerbin"
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

Result:
- **62 trips, all arrived as planned:** Pe and Ap within ±5 % of the parking altitude (the worst was 1.5 % off), clear of
  the planet and every moon.
- **The safety net fired 0 times.**
- **Against the planner:** trip time +0.0 to +7.7 % of the estimate; delta-v used 7.3 % under to 1.5 % over (the estimate
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

The first two flew back to back in one launch, the coast trip in a second. In all three:
- the engine was off at arrival, and time warp was back to normal before the last burn;
- MechJeb's settings came back identical apart from window sizes;
- the engine was at zero whenever the ship pointed more than 5° off (outside the skew flip).

After each launch, MechJeb's, BetterTimeWarp's and KSP's settings were identical to before, and no other save changed.

## Consequences

- Moon trips plan and fly with the same windows, safety net, heat and warp rules, pause and resume as trips between
  planets.
- A moon trip's planner estimate searches for the departure window and flies the burns with gravity eight times: 9 to 90
  ms on a desktop (a coast plan 20 to 180 ms), against a few milliseconds between planets. The planner reuses a moon
  trip's estimate for 5 seconds, counting its times down meanwhile.
- A trip between planets is unchanged apart from the three guidance fixes that apply to every trip.
- **Not yet:** a moon straight to another planet's system; a detour around a moon in the way; the cheapest slow coast
  transfers inside a planet's pull.
