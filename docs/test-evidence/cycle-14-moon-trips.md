# Cycle 14: moon trips in the game

Kris's KSP 1.12.5 install, test copy of his saves only (`saves/FlipAndBurn-Test`), the Bebop Refueller starting in
orbit around Kerbin. The lines below are copied from KSP.log as the test helper wrote them (`moontrips`, 2026-11-04).
After each launch the run's own check found MechJeb's settings, BetterTimeWarp's and KSP's settings.cfg identical to
before, and no file in any other save changed.

## The first launch: to the Mun, back to Kerbin, on to Minmus

Three trips back to back on one ship, with nothing touched in between. Trips 1 and 2 used the test-only cheats infinite
propellant and ignore max temperature (the Bebop's drive holds about 0.45 g cool, and leaving a low Kerbin orbit needs
at least 0.86 g).

The planner's list from each starting point:

```
moon trips: orbiting Kerbin; Fly to: Moons of Kerbin: Mun, Minmus; Planets: Moho, Eve, Duna, Dres, Jool, Sarnus, Urlum, Neidon, Plock
moon trips: orbiting Mun; Fly to: The Mun's planet: Kerbin; Other moons of Kerbin: Minmus | note: To fly to another planet, get into orbit around Kerbin first: Flip and Burn doesn't fly from a moon straight to another planet yet.
```

### Trip 1: Kerbin orbit to the Mun, full burn at 1 g, cut, flip, relight

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

### Trip 2: the Mun back to Kerbin orbit, full burn at 1 g with a skew flip

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

### Trip 3 in the first launch

It flew to Minmus and arrived cleanly (19.91 × 20.02 km), but it didn't really coast. With fuel counted, the Bebop
carries 116 km/s of delta-v, and with that much the fastest coast trip is a full burn: the planner says so ("nothing to
coast"). The run stopped there because the coast picture couldn't be taken.

## The second launch: the coast trip to Minmus

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

## Pictures

| Trip | Planner | On the way | Flip or coast | Arrival, map view |
|---|---|---|---|---|
| 1, to the Mun | `moon-1-mun-1-start.jpg` | `moon-1-mun-2-waiting.jpg`, `-2-mid-burn`, `-5-arriving` | `moon-1-mun-3-flip.jpg` | `moon-1-mun-6-final-orbit-map.jpg` |
| 2, to Kerbin | `moon-2-kerbin-1-start.jpg` | `moon-2-kerbin-2-waiting.jpg`, `-2-mid-burn`, `-5-arriving` | `moon-2-kerbin-3-flip.jpg` (skew) | `moon-2-kerbin-6-final-orbit-map.jpg` |
| 3, to Minmus (coast) | `moon-3-minmus-1-start.jpg` | | `moon-3-minmus-3-coast.jpg` | `moon-3-minmus-6-final-orbit-map.jpg` |

## Not tested in the game

- Duna, Jool and the Outer Planets Mod moons (only the simulator flew them).
- A detour around a planet.
- A moon to a sibling moon.
- Pause and resume, and save and load, during a moon trip.
- The minimum-acceleration refusals on screen.
- 25x and 50x warp on a moon trip.
