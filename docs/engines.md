# Engines

[← Back to the README](../README.md)

Flip and Burn works with any engine. What changes from engine to engine is how far it can take you, and that comes
down to one thing: how much delta-v (your ship's total "speed budget" from its fuel) the ship carries.

## How it reads your ship

The planner looks at **the engines that are running now** and **the fuel they can reach**, and works out:

- **Ship's delta-v:** the fuel budget at the acceleration you chose, and at full throttle;
- **Ship's max acceleration:** the hardest the engines can push the ship as it is now.

Things to know:

- **Only the current stage counts.** If there are more stages below, the planner says so and leaves them out. Stage or
  activate the engine you want to use before you plan.
- **Several engine types at once** are fine. When one runs dry, the others carry on, and the numbers allow for it.
- **Multi-mode engines** are read in the mode they're in. An air-breathing mode doesn't work in space, so the planner
  asks you to switch it to its rocket mode.
- **Electric engines** (like the stock ion engine) are limited by how much power the ship makes. If the solar panels or
  generators can't keep up, the planner says how much of full thrust they can hold, and plans with that.
- **An engine it doesn't recognise** (from a mod that does its own thing) is read from KSP's own figures, and the
  planner marks the numbers as estimates.

## KSP Interstellar fusion drives: the real deal

Burning the whole way between planets takes an enormous amount of delta-v. A full burn to Duna at 1 g needs around
**690 km/s**, while a good stock rocket carries **3 to 6 km/s**.

The fusion drives in [KSP Interstellar Extended](https://github.com/sswelm/KSP-Interstellar-Extended) are what make
these trips practical. A ship like the Bebop Refueller has well over 100 km/s at 1 g, enough for fast coast trips
anywhere in the Kerbol system.

Flip and Burn knows how these drives behave:

- **They're less efficient at low throttle,** so a gentle acceleration on a huge drive costs more fuel per km/s than
  you might expect. The planner's numbers already include this.
- **They run hot.** A drive that can push 3 g on paper may only be able to keep up 0.5 g before its radiators fall
  behind. Flip and Burn steps down to what the ship can sustain, re-plans and keeps flying (see
  [Ways to fly](ways-to-fly.md#what-overheating-does)).
- **Their power doesn't get wasted.** When a drive runs short of power, it pushes less hard but uses less fuel too, so
  you lose time, not fuel.
- **Cryogenic tanks can boil off** if the ship runs out of electric charge. Flip and Burn measures it and allows for it
  (see [Safety](safety.md#boil-off)).

## Stock engines: what to expect

You can use Flip and Burn with stock engines, but set your expectations:

- **A full burn is out of reach** for any stock rocket. The planner shows how much delta-v it would need, which will be
  far more than you have.
- **A coast trip** may fit, at a low acceleration and over a long time. The planner finds the fastest one your fuel
  allows, keeping 10% in reserve, or tells you that none fits.
- **Flip and Burn doesn't wait for transfer windows.** It points at where the planet will be and goes, which costs more
  fuel than a classic transfer at the perfect moment. For a stock rocket on a tight fuel budget, MechJeb's own
  Maneuver Planner (with its transfer-window tools) is usually the better choice.

Where Flip and Burn shines is a ship with plenty of delta-v to spare: a nuclear tug with big tanks, a mod engine with a
high efficiency rating, or a fusion drive.

Next: [troubleshooting and FAQ →](troubleshooting.md)
