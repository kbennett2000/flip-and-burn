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

## Addendum (2026-10-27): the delta-v the player sees, and propellant lost while the engines are off

Asked by Kris after trip B's first run reported arriving with 4.3 % of its delta-v, under the 10 % a coast trip keeps:
find where it went, fix the cause, and allow for boil-off only where it is real.

**Where it went: nowhere. The delta-v reader was wrong while the drive fired.**

- *Boil-off, measured in the game* (`drain-run.log`, the Bebop parked, 24 game hours per set-up):
  - engine active, or shut down, with power: no deuterium lost; electric charge falls 288 an hour;
  - shut down with the electric charge emptied: 1.84 units an hour, **0.30 kg per game hour**, from the six IFS cryogenic
    tanks' `ModuleStorageCryostat` (it boils off only when unpowered).
- *Trip B's own set-up, measured* (`coast-drain-run.log`: the coast trip to Duna at 0.4 g, no cheats, tanks logged every
  15 game minutes while burning and every 6 game hours while coasting):
  - speed-up: 11.220 t to 5.667 t of deuterium for 35,983 m/s, a steady 0.546 t per 15 game minutes, matching the engine
    model (effective Isp within 2 %);
  - coast, 102 game hours: **5.6668 t throughout** (34,893.9 units at every sample: nothing lost). The Megajoules store ran down from 409 to empty by
    about 60 hours; electric charge kept the cryostats powered (1.28 million to 0.93 million) the whole way.
  - (KSP crashed near the end of that coast: the plugin's DLL was replaced on disk while the game had it loaded. The coast
    figures were complete by then.)
- *The drive's code* (KSP Interstellar's `DaedalusEngineController`): a power shortfall scales thrust and fuel flow by
  the same factor, so it can weaken the drive but never wastes fuel. The Isp depends only on throttle: Isp = engine Isp ×
  throttle^0.5.
- *The reader's fault:* while the drive fires, KSP Interstellar writes the Isp *at the current throttle* (about 99,800 s at
  throttle 0.126) into the engine's Isp curve. The reader took that as the full-throttle Isp (281,250 s) and applied the
  throttle rule a second time. So it showed 24.8 km/s left with 95 % of the fuel aboard, and 3.2 km/s at trip B's
  arrival. At Start (drive idle) it was right, which is why the plan at Start was.

**Fixes:**

1. *The reader* reads a KSP Interstellar fusion drive's full-throttle thrust and Isp from its controller (`MaximumThrust`
   and `EngineIsp`, with the drive's relativity factor) instead of the live curve, so the delta-v shown is the same
   whether the drive is firing or idle. Other engines are read as before.
2. *While the engines are shut down* (the coast, a cut), they read as nothing; the delta-v is then worked out from the
   ship's mass now and the last reading's engines and dry mass, so propellant lost while coasting still shows.
3. *The live reserve check* (every 30 real seconds, `ReserveWatch`): the delta-v the ship has, less what the last plan
   (Start, a resume, or a heat re-plan) still needs: the plan's figure minus the delta-v burned since, measured from the
   acceleration. Burning takes the same amount off both, so flying the plan leaves the reserve where it was; propellant
   that goes without thrust comes off only what the ship has, and shows at once. It speaks up below 9.5 % (the reader is
   good to a few tenths of a percent), saying why in plain words: propellant going while the engines are off (with the
   measured rate), or the trip using more delta-v than planned.
   - A first version estimated what the rest of the trip needs from the time left at the chosen acceleration, scaled to
     the plan at the first check. Right after Start the guidance's flip and braking times aren't settled, so it read
     7.4 % 30 seconds into trip B and was replaced before the run went on.

**Boil-off allowance, for ships that do lose propellant** (ships that run out of electric charge, for example):

- *Measured, not assumed.* Whenever a trip is flying or the window is open, the autopilot samples the mass of every
  resource the ship's engines burn once a game minute. Stretches with any engine firing, or with propellant added, don't
  count. After an hour of game time without firing, the average is this ship's loss rate. It is saved with the ship.
  Nothing is engine-specific.
- *Allowed for only when it matters.* A coast trip allows for it when the loss over the trip would cost at least 0.1 % of
  the ship's delta-v. The coast plan then takes the loss's delta-v cost over the whole trip time out of the budget,
  re-planning until the two agree (`CoastPlanner.PlanAllowingForLoss`): a slower trip that still arrives with 10 %. The
  window then shows "Set aside for boil-off" and says, for example: "This ship's tanks lose about 1.8 kg of propellant a
  day while the engines are off (boil-off, measured on this ship). This trip allows for it: … of delta-v is set aside, so
  it still arrives with 10% in reserve."
- *Refused before Start* when no coast fits once the loss is allowed for, with the cause and what to do (keep the tanks
  powered, or a higher acceleration for a shorter trip).
- *Not re-planned during the speed-up.* The loss can't be measured while the engine fires (the burn's own use swamps a
  loss of grams an hour). The coast is where a loss shows, and the live check reports it there.
- *Full burns don't allow for it.* They are short (a day or so at 1 g), and the live check still reports a shortfall.

**Proof: trip B again, on the fixed build** (`trip-b-run.log`: coast to Duna at 0.4 g, no cheats, with a quicksave and
quickload on the way):

| Point | Deuterium in the tanks | Reader (at 0.4 g) | Expected reserve | Megajoules | Electric charge |
|---|---|---|---|---|---|
| Start | 11.220 t | 73,385 m/s | 10.00 % | 416 (full) | 1,279,674 |
| 0.5 h, firing | 10.130 t | 66,377 m/s (the old reader: 23,495) | 10.01 % | 415 | 1,279,085 |
| After the quickload | | | 10.15 % | | |
| 2.5 h, end of the speed-up | 5.779 t | 38,146 m/s | 10.15 % | 410 | 1,278,506 |
| Coast, 3 h to 117 h | 5.667 t throughout (0.15 kg on a course correction) | 37,411 m/s | 10.15 % | 407, empty from about 60 h | 1,278,114 to 902,412 |
| Arrived | 1.383 t | 9,199 m/s | (12.5 % real) | 0 | 896,476 |

- It arrived in a 59.9 × 60.2 km orbit, 16 s later than planned at Start, and the report said: "It used 64.0 km/s of
  delta-v and has 9.20 km/s left, 12.5% of the 73.4 km/s it started with." An independent calculation from the 1.383 t
  left and the drive's part config gives 9,199 m/s.
- Trip B's first run used exactly the same delta-v (64,044 m/s), so it too arrived with about 12.5 %; its "4.3 %" was the
  reader.
- The live check's last estimate before arrival was 9.94 %: cautious, because it still counts the part of the guidance
  margin the trip didn't use. Under 10 % but above the 9.5 % line, so the window stayed quiet, as it should.
- The planner, three ways (`lossplan`, same run): no loss measured, no allowance and no note; the 0.30 kg/h measured
  without electric charge, 231 m/s set aside, a trip 27 minutes longer, still 10.00 % at arrival, and the note; 100 kg/h,
  Start refused before launch, with boil-off named as the cause.

**The reader, checked independently** (`reader-check-run.log`, `tools/verify/check_reader.py`). The script shares no
code with the plugin. It works the drive's figures out from its part config (Mk7: 15,000 kN, Isp 500,000 s), scaled to
the Bebop's 3.75 m drive by the config's TweakScale exponents (thrust × 0.316, Isp × 0.5625). The propellant comes from
the save. The delta-v holding acceleration a is then 2 · Isp · g0 · √(a/F) · (√m_start − √m_end):

| Bebop Refueller (11.22 t deuterium) | Independent | Flip and Burn, fixed | The old reader |
|---|---|---|---|
| Drive idle, 0.4 g | 73,397.3 m/s | 73,397.3 m/s | 73,397.3 m/s |
| Drive idle, 1 g | 116,051.3 m/s | 116,051.3 m/s | 116,051.3 m/s |
| Drive idle, 2 g | 164,121.3 m/s | 164,121.3 m/s | 164,121.3 m/s |
| Drive firing at throttle 0.126, 0.4 g | 73,384.3 m/s | 73,384.3 m/s | 26,048.8 m/s |
| Drive firing at throttle 0.126, 1 g | 116,030.8 m/s | 116,030.8 m/s | 41,186.8 m/s |
| Drive firing at throttle 0.126, 2 g | 164,092.3 m/s | 164,092.3 m/s | 58,247.0 m/s |

- With the drive idle (the planner before Start) the old reader was right. That is why every plan at Start was right,
  and why this went unnoticed until a whole trip without cheats was flown.
- While the drive fires, the engine's Isp curve read 99,833.8 s. The script's engine Isp × √0.126 gives 99,833.8 s, so
  the old figures are the right ones × √0.126: the throttle rule counted twice.
- A stock ship (CubeSat v3.2, a KE-1 Mastodon; `check_engines.py` from cycle 7) is unchanged: 699.5 m/s at 0.4, 1 and
  2 g, before and after, and 699.5 m/s independently. A stock engine has no throttle rule.

## Addendum (2026-10-27): what the player reads (cycle 12 polish)

Kris asked for the window and every message to read cleanly for a first-time player. Changes that alter behaviour:

- **The window fits what it shows.** Unity's layout grows a window but never shrinks it, so the trip window kept the
  planner's height. When the content changes shape, the height is reset once (not every frame: every new window
  position marks MechJeb's settings as changed).
- **No stale rows.** An input changed in the window is planned again at the start of the next layout pass, so the rows
  never show the old plan beside the new input. The test helper plans through the same path (`Replan`).
- **Numbers that add up as written.** The shortfall message writes needed, available and short at one precision (the
  largest number's), and takes "short" from the two rounded numbers (`Readout.Shortfall`). A ship's most acceleration
  is rounded **down** everywhere, so the window never promises more than the ship can do ("can reach only 3.16 g …
  lower the acceleration to that or less", where it used to say 3.17 g and then 3.16 g).
- **The right reason for a low reserve.** The live check keeps the reason the last plan gave (Start, a resume, a heat
  re-plan) while the reserve is where that plan put it, and names a new cause only when it falls further. A resume that
  keeps its arrival at 9.2 % used to be blamed on "the trip using more delta-v than planned".
- **Passing messages pass.** "Trip resumed …" and "the engines couldn't safely hold …" clear when the trip moves to its
  next phase; the safe-orbit messages stay.
- **Plainer rows:** "Top speed (relative to Kerbin)", "Pointing off by", "1.00 g (aiming for 1.00 g)", "engine off
  (coasting)" during the coast, and a coast trip's warp line says "coast in …" rather than "flip in …".
- **MechJeb's stage-simulation NullReference** on KSP Interstellar ships never came from Flip and Burn: only the test
  helper's diagnostic asks MechJeb for its stage simulation (and catches it). The mod itself never calls it.
- **The reserve the player sees is the realistic figure** (Kris, product owner, cycle 12). A first trip that warned
  "under 10 %" mid-trip and then arrived with 13.1 % would worry players for nothing. The window and messages now show
  what the ship will most likely arrive with; the autopilot's own decisions (keeping an arrival after a pause, re-plans,
  refusals) keep the whole 6 % guidance margin as before.
  - *After the climb out:* the delta-v the ship has, less the burning the guidance has left (`BurnTimeLeft` × the
    acceleration), plus 1 % for a coast trip's course corrections and final settling (`ReserveWatch.FlyingOverhead`; a
    full burn is flown within 0.1 % of its plan and gets none).
  - *While climbing out* (the guidance's times aren't settled yet): from the plan, at what coast trips really use, 2.7 %
    over their burns rather than the plan's 6 % (`ReserveWatch.TypicalCoastOverhead`; trip B used 64,044 of 66,058 m/s
    planned with the margin, the guide's 1 g trip 101 of 104 km/s).
  - *Proof* (`ReserveShownTests`, the simulator): every sample after the climb out within a point of what the ship
    arrived with: Duna coast 0.92 points (184 samples), Jool coast 0.56 (417), a full burn to Duna 0.45 (33). While
    climbing out: 1.10, 1.44 and 0.10 points.
  - *The warning* comes only when the realistic figure is under 10 %. It keeps the last plan's reason while the figure
    is within a point of that plan's, and names a new cause only when it falls further.
