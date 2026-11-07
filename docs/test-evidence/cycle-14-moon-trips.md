# Cycle 14: moon trips in the game

Kris's KSP 1.12.5 install, test copy of his saves only (`saves/FlipAndBurn-Test`), the Bebop Refueller starting in
orbit around Kerbin. The lines below are copied from KSP.log as the test helper wrote them (`moontrips`, 2026-11-04).
After each of the three launches the run's own check found MechJeb's settings, BetterTimeWarp's and KSP's settings.cfg identical to
before, and no file in any other save changed.

## The final build (cycle 15 review, 2026-11-06)

The review found the last burn covering for braking that ran out of thrust in strong gravity (ADR 0005, addendum). The
fixes change the guidance for every trip, so the moon trips, a Tylo coast trip and a Jool arrival were flown again on the
final build. Each started from the test copy's own quicksave. After each launch, MechJeb's, BetterTimeWarp's and KSP's
settings were checked identical to before.

The moon trips flew on build 2753209. The changes after it change none of their commands: how a coast plan counts
deep-well braking (61dd0a6) touches coast plans only where the braking is planned below 99 % (the Minmus coast keeps
99 %), and the braking reason's own line in the trip window (36dc9a5) is display only. The Tylo coast trip's plan does
change with 61dd0a6, so it was flown again on it. The trip window's braking line was moved onto its own row after the Jool
trip (36dc9a5, display only), and photographed on a short trip from Laythe to Jool on the final build.

| Trip | Orbit | Time against the plan at Start |
|---|---|---|
| Kerbin to the Mun, full burn at 1 g, cut-flip-relight (waited 15 min) | 19.91 × 20.11 km, planned 20 | +30 s (+1.0 %) |
| The Mun back to Kerbin, full burn at 1 g, skew flip (waited 22 min; braking with headroom for Kerbin's pull) | 79.58 × 80.26 km, planned 80 | +78 s (+2.3 %) |
| Kerbin to Minmus, coast at 1 g, fuel counted (lowered to 25 km/s) | 19.75 × 20.01 km, planned 20 | +21 s (+0.3 %), 14.3 % of the delta-v left |
| Jool orbit to Tylo, coast at 1 g, fuel counted (lowered to 30 km/s; build 2753209) | 29.82 × 30.16 km, planned 30 | +38 s (+0.5 %), 11.8 % left |
| Jool orbit to Tylo, coast at 1 g, fuel counted (final build) | 29.83 × 30.18 km, planned 30 | +34 s (+0.4 %), 14.7 % left |
| Kerbin to Jool, full burn at 1 g, cut-flip-relight (build ab21c04) | 219.35 × 220.11 km, planned 220 | +8 s over 8 days 5 hours (+0.004 %) |
| Laythe orbit to Jool, full burn at 1 g (final build; set-orbit cheat, test copy) | 217.36 × 223.58 km, planned 220 | +61 s (+1.6 %) |

All arrived cleanly: engine off, out of warp before the last burn, MechJeb's settings identical apart from window sizes,
and the engine at zero whenever the ship pointed more than 5° off (outside the skew flip).

**The Tylo coast trip** needs the Bebop near Jool, which the test copy doesn't have: KSP's own set-orbit cheat (test copy
only) put it in a 220 km orbit around Jool, and its propellant was lowered in memory so the trip had to coast. Before the
fixes the same trip, flown watched in the game, arrived at 29.85 × 30.07 km; in the simulator its last burn took 727 s.

**The Jool trip** is the planet arrival that overspeeded in the simulator (955 m/s at the last burn, now 29). It braked at
0.850 g for 28 hours, the law rising to 0.89 g in the last minutes, and arrived 8 seconds after the time planned at Start.
While it braked the window read "0.85 g (aiming for 0.85 g: braking leaves spare push for Jool's gravity)" in one row
(`trip-j-4-slow-down.jpg`); the label column was squeezed, so the reason now has its own line. On the final build, braking
into Jool from Laythe (`jool-row-5-arriving.jpg`; `docs/images/trip-braking-headroom.jpg` for Ways to fly):

```
Acceleration        0.84 g (aiming for 0.85 g)
Aiming below 1.00 g: braking leaves spare push for Jool's gravity. The engines aren't overheating.
```

The Laythe trip used KSP's set-orbit cheat (test copy only) to put the Bebop 500 km above Laythe.

Pictures: `tylo-coast-1-start.jpg`, `-3-coast.jpg`, `-5-arriving.jpg`, `-6-final-orbit-map.jpg`; the moon trips as below;
`trip-j-1-start.jpg`, `-3-flip.jpg`, `-4-slow-down.jpg`, `-5-arriving.jpg`, `-6-final-orbit-map.jpg`; `jool-row-5-arriving.jpg`,
`-6-final-orbit-map.jpg`.

## Before the review's fixes

### The first launch: to the Mun, back to Kerbin, on to Minmus

Three trips back to back on one ship, with nothing touched in between. Trips 1 and 2 used the test-only cheats infinite
propellant and ignore max temperature (the Bebop's drive holds about 0.45 g cool, and leaving a low Kerbin orbit needs
at least 0.86 g).

The planner's list from each starting point:

```
moon trips: orbiting Kerbin; Fly to: Moons of Kerbin: Mun, Minmus; Planets: Moho, Eve, Duna, Dres, Jool, Sarnus, Urlum, Neidon, Plock
moon trips: orbiting Mun; Fly to: The Mun's planet: Kerbin; Other moons of Kerbin: Minmus | note: To fly to another planet, get into orbit around Kerbin first: Flip and Burn doesn't fly from a moon straight to another planet yet.
```

#### Trip 1: Kerbin orbit to the Mun, full burn at 1 g, cut, flip, relight

```
trip M1: waiting in orbit for the departure window, burn in 936 s; planner said 2960 s for the whole trip
trip M1 arrival: arrived True; around Mun; periapsis 19.81 km, apoapsis 20.05 km, eccentricity 0.00054; planned 20 km +/- 1.0 km: periapsis ok, apoapsis ok
trip M1 arrival message: Arrived at The Mun: in a 19.8 × 20.1 km orbit (planned: 20 km, circular). The trip took 49m, as planned at Start (49m). It used 19.6 km/s of delta-v; fuel isn't counted (infinite propellant is on).
trip M1 arrival numbers: trip 2991 s, planned at Start 2960 s (+31 s)
trip M1 warp: last time warp above x1 ended 135 game s before the check; warp during the circularizing burn: no
trip M1 turning [steady]: 420 physics steps pointing more than 5 deg off, highest engine throttle in them 0.000 (zero)
trip M1: MechJeb settings files before Start and after arrival: identical apart from window sizes (1805 files)
trip M1: ARRIVED CLEANLY (engine off True)
```

#### Trip 2: the Mun back to Kerbin orbit, full burn at 1 g with a skew flip

```
trip M2: waiting in orbit for the departure window, burn in 1299 s; planner said 3357 s for the whole trip
trip M2 arrival: arrived True; around Kerbin; periapsis 79.80 km, apoapsis 80.14 km, eccentricity 0.00025; planned 80 km +/- 4.0 km: periapsis ok, apoapsis ok; atmosphere top 70 km: both above
trip M2 arrival message: Arrived at Kerbin: in a 79.8 × 80.1 km orbit (planned: 80 km, circular). The trip took 56m, as planned at Start (55m). It used 19.9 km/s of delta-v; fuel isn't counted (infinite propellant is on).
trip M2 arrival numbers: trip 3375 s, planned at Start 3357 s (+18 s)
trip M2 warp: last time warp above x1 ended 130 game s before the check; warp during the circularizing burn: no
trip M2: MechJeb settings files before Start and after arrival: identical apart from window sizes (1805 files)
trip M2: ARRIVED CLEANLY (engine off True)
```

Kerbin was between the ship and the Mun at the start of trip 1, and the Mun between the ship and Kerbin at the start of
trip 2. Both waited in orbit, engine off and on time warp, then burned. The skew flip fired through the turn by design;
everywhere else the engine was at zero whenever the ship pointed more than 5° off.

#### Trip 3 in the first launch

It flew to Minmus and arrived cleanly (19.91 × 20.02 km), but it didn't really coast. With fuel counted, the Bebop
carries 116 km/s of delta-v, and with that much the fastest coast trip is a full burn: the planner says so ("nothing to
coast"). The run stopped there because the coast picture couldn't be taken.

### The second launch: the coast trip to Minmus

The Bebop's propellant was lowered in memory (test copy) until the planner read 25 km/s at 1 g, about half what a full
burn to Minmus takes, so the trip had to coast. Fuel was counted; only ignore max temperature was on.

```
moon trips: propellant (LqdDeuterium) lowered for the coast trip: delta-v at 1.0 g 116051 -> 25012 m/s (in memory, test copy)
trip M3 tanks [0.30 h, Coast]: LqdDeuterium 8861.9 units (1.4392 t), ship mass 142.862 t
trip M3 arrival: arrived True; around Minmus; periapsis 19.69 km, apoapsis 20.01 km, eccentricity 0.00200; planned 20 km +/- 1.0 km: periapsis ok, apoapsis ok
trip M3 arrival message: Arrived at Minmus: in a 19.7 × 20.0 km orbit (planned: 20 km, circular). The trip took 1h, 25m, as planned at Start (1h, 25m). It used 22.0 km/s of delta-v and has 3.63 km/s left, 14.1% of the 25.8 km/s it started with.
trip M3 arrival numbers: trip 5140 s, planned at Start 5119 s (+21 s); delta-v left 3631 m/s
trip M3 warp: last time warp above x1 ended 128 game s before the check; warp during the circularizing burn: no
trip M3 turning [course correction]: 343 physics steps pointing more than 5 deg off, highest engine throttle in them 0.000 (zero)
trip M3: MechJeb settings files before Start and after arrival: identical apart from window sizes (1805 files)
trip M3: ARRIVED CLEANLY (engine off True)
```

It kept more than the 10 % reserve (14.1 % left), and made one course correction on the way.

### The third launch: the Bebop to the Mun with real heat

The trip a player flies first: the Bebop from its Kerbin orbit to the Mun at 1 g, full burn, **fuel counted and no
cheats**. The planner's warp choice was set to its default, 10x, for the run and put back afterwards. The Bebop's orbit in
the test copy is about 740 km up, where the least acceleration that can leave Kerbin is about 0.22 g (from an 80 km orbit
it is 0.86 g).

Before Start the planner offered the trip: it can't know a drive will overheat until it runs. In flight the heat guard
stepped the trip down three times, each time re-planning the rest and saying so on screen, and the trip arrived:

```
moon heat: step-down 1 at 1093 s into the trip: now 0.76 g; phase Accelerate; altitude 815 km above Kerbin; hottest part 28%; notice: The engines couldn't safely hold 1.00 g: the ship is overheating (waste heat at 97%; KSP Interstellar drives lose thrust above 90%). Flip and Burn is now holding 0.76 g and has re-planned the rest of the trip: arrival is 4m later (35m from now).
moon heat: step-down 2 at 1632 s into the trip: now 0.60 g; phase Accelerate; altitude 2713 km above Kerbin; hottest part 90%; notice: The engines couldn't safely hold 0.76 g: "Rocinante" Kerbstein Fusion Drive reached 90% of its temperature limit. Flip and Burn is now holding 0.60 g and has re-planned the rest of the trip: arrival is 2m later (28m from now).
moon heat: step-down 3 at 1692 s into the trip: now 0.48 g; phase Accelerate; altitude 3090 km above Kerbin; hottest part 90%; notice: The engines couldn't safely hold 0.60 g: "Rocinante" Kerbstein Fusion Drive reached 90% of its temperature limit. Flip and Burn is now holding 0.48 g and has re-planned the rest of the trip: arrival is 2m later (30m from now).
moon heat: trip over after 3561 game s: running False, arrived True, step-downs 3, acceleration at the end 0.48 g; stop reason: Arrived at The Mun: in a 20.0 × 20.0 km orbit (planned: 20 km, circular). The trip took 59m, 10m longer than planned at Start (49m). It used 14.3 km/s of delta-v and has 68.6 km/s left, 59.1% of the 116 km/s it started with.
moon heat: the ship ends up ORBITING around Mun: periapsis 20.0 km, apoapsis 20.0 km, eccentricity 0.0002
```

The first step-down came about 2.5 minutes into the burn (after the 15-minute wait for the window). From then on the
braking ran at normal speed: the drive stayed near its heat limit, where the heat rules hold time warp off. Pictures:
`moon-heat-1-planner.jpg` (the planner before Start: 10x, no cheats), `moon-heat-2-step-down-1.jpg` and `-2`,
`moon-heat-3-end.jpg`, `moon-heat-4-map.jpg`.

## Pictures

The moon-trip pictures below are from the final build's launch (the same names as before the fixes).

| Trip | Planner | On the way | Flip or coast | Arrival, map view |
|---|---|---|---|---|
| 1, to the Mun | `moon-1-mun-1-start.jpg` | `moon-1-mun-2-waiting.jpg`, `-2-mid-burn`, `-5-arriving` | `moon-1-mun-3-flip.jpg` | `moon-1-mun-6-final-orbit-map.jpg` |
| 2, to Kerbin | `moon-2-kerbin-1-start.jpg` | `moon-2-kerbin-2-waiting.jpg`, `-2-mid-burn`, `-5-arriving` | `moon-2-kerbin-3-flip.jpg` (skew) | `moon-2-kerbin-6-final-orbit-map.jpg` |
| 3, to Minmus (coast) | `moon-3-minmus-1-start.jpg` | | `moon-3-minmus-3-coast.jpg` | `moon-3-minmus-6-final-orbit-map.jpg` |

## Not tested in the game

- Duna's moon and the Outer Planets Mod moons (only the simulator flew them); of Jool's, only Tylo, from a cheat-set orbit.
- A detour around a planet.
- A moon to a sibling moon.
- Pause and resume, and save and load, during a moon trip.
- The minimum-acceleration refusals on screen.
- 25x and 50x warp on a moon trip.
