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
- **Ion engines** (like the stock one, which burns xenon as well as electric charge) are limited by how much power the
  ship makes. If the solar panels or generators can't keep up, the planner says how much of full thrust they can hold,
  and plans with that.
- **Engines that run on electricity alone** have a section of their own [below](#engines-that-run-on-electricity).
- **An engine it doesn't recognise** (from a mod that does its own thing) is read from KSP's own figures, and the
  planner marks the numbers as estimates.

## KSP Interstellar fusion drives: the real deal

Burning the whole way between planets takes an enormous amount of delta-v. A full burn to Duna at 1 g needs around
**690 km/s**, while a stock rocket carries **a few km/s**.

The fusion drives in [KSP Interstellar Extended](https://github.com/sswelm/KSP-Interstellar-Extended) are what make
these trips practical. The Bebop Refueller has 116 km/s at 1 g: enough for a coast trip from Kerbin to Duna in about
12 Kerbin days.

Flip and Burn knows how these drives behave:

- **They're less efficient at low throttle,** so a gentle acceleration on a huge drive costs more fuel per km/s than
  you might expect. The planner's numbers already include this.
- **They run hot.** The Bebop's drive can push over 3 g on paper, but its radiators can only keep up with a fraction of
  that for long. Flip and Burn steps down to what the ship can sustain, re-plans and keeps flying (see
  [Ways to fly](ways-to-fly.md#what-overheating-does)).
- **Their power doesn't get wasted.** When a drive runs short of power, it pushes less hard but uses less fuel too, so
  you lose time, not fuel.
- **Cryogenic tanks can boil off** if the ship runs out of power for their coolers. Flip and Burn measures it and
  allows for it (see [Safety](safety.md#boil-off)).

## Engines that run on electricity

Some mod engines burn nothing but electric charge, like the Jibar Side Thruster GIANT. Nothing leaves the ship, so
there's no fuel to run out of. What limits the trip is electricity: how much the ship makes, and how much it has
stored.

**What the engines need.** The planner works it out from the engine itself. The Side Thruster GIANT needs about
**64 electric charge a second for every kN** of thrust: 191,000 a second at its full 3,000 kN, and a third of that at a
third of the thrust. The ship's row says "(powered)" or "(batteries)" so you can see which case you're in.

**What the ship has.** The planner adds up:

- **Stored charge** in the batteries.
- **What generators make**: stock generators and RTGs, reactors and generators from KSP Interstellar, and converters
  that make electric charge. They count only while they're switched on. If one is switched off (or a solar panel is
  folded), the planner tells you, and what turning it on would change: for example, "Your reactor is switched off.
  Turn it on: if the ship then makes 23,189 a second of electric charge or more, this trip needs no battery limit."
- **Solar panels** that are deployed. They make less the farther you are from the Sun, so the planner counts them at
  the farthest point of the trip (the destination's farthest point from the Sun, if that's farther than you are now).
  In a planet's shadow they make nothing, and the planner can't plan for that, so it says so.
- **Fuel cells**, but only for as long as their fuel lasts.

Then one of two things is true:

- **The ship's own power keeps up.** The planner says **"Powered all the way"**, with what the ship makes and what the
  engines need. Delta-v is unlimited, so a full burn always fits.
- **It doesn't, so the batteries make up the difference** until they run low. The planner says for how long, works out
  the delta-v that gives, and plans just as it does for fuel: a full burn if it fits, otherwise the fastest coast trip
  that does, with the usual 10% kept in reserve. It also tells you the acceleration your ship's own power can keep up
  with, if you'd rather fly slower and not touch the batteries.

**The last 10% is never used.** Flip and Burn never takes the batteries below a tenth of their capacity, so the probe
core, reaction wheels and steering always have power. The trip window shows how much is stored ("never below 10%") and
how much is being made and used each second.

**If the power falls short on the way** (a generator switched off, a solar ship in shadow), Flip and Burn treats it like
overheating: it lowers the acceleration to what the ship's own power can keep up with, re-plans the rest of the trip,
tells you the new arrival time and carries on. It only stops, with a message saying why, if the ship makes so little
power that no lower acceleration helps.

**Ships with fuel engines and electric ones** work too. The electric engines count for what the ship's power can keep
up with, alongside the fuel engines.

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
