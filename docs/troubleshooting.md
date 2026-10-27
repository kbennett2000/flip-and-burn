# Troubleshooting and FAQ

[← Back to the README](../README.md)

## Problems

### Flip and Burn isn't in MechJeb's menu

- **Is the ship in flight?** The menu entry only appears in the flight scene, not in the editor.
- **Is MechJeb itself working?** If MechJeb's menu tab isn't on screen at all, the problem is with
  MechJeb or Module Manager: see MechJeb's own install notes.
- **Is it installed in the right place?** You should have `GameData/FlipAndBurn/Plugins/FlipAndBurn.dll`. See
  [Installing](installing.md).
- **Is your MechJeb new enough?** Flip and Burn needs MechJeb 2.15 or later, and KSP skips it quietly if MechJeb is older.
- **Still nothing?** Open `KSP.log` (in your KSP folder) and search for `FlipAndBurn`. A working install has a line
  saying `[FlipAndBurn] loaded`.

### The Start button is greyed out

The planner has found a reason the trip can't be flown, and says so in yellow below the numbers. The most common ones:
the ship can't reach the acceleration you typed, there isn't enough fuel for a full burn (try **Coast in the middle**),
or the ship isn't in a stable orbit yet. [Safety](safety.md#what-it-wont-start-and-why) lists them all.

### "No engine is active"

Flip and Burn reads the engines that are running now. Stage, or right-click your main engine and activate it, then look
at the planner again.

### The numbers look too small for my ship

Only the current stage is counted. If your big engine is on a stage below, the planner says "This ship has … more stage
below this one". Stage down to it, or activate it, and the planner reads it.

### I can't save, switch ships or go to the Space Center during a burn

That's KSP: it doesn't allow any of those while a ship accelerates at more than 0.1 g. Press **Pause** in the trip
window, do what you need to do, and the trip carries on when you come back. See
[Saving, pausing and switching ships](saving-and-pausing.md).

### Time warp stopped by itself

That's on purpose. Flip and Burn drops out of warp in good time before anything happens: the flip, the start of
braking, a course correction, arriving. It also holds warp down while parts are warm, so you can see what's happening.
The **Warp** line in the trip window always says why.

### The acceleration went down on its own, and the arrival got later

The engines were overheating. Flip and Burn stepped down to an acceleration the ship can keep up, re-planned the rest of
the trip, and told you the new arrival time. See [Ways to fly](ways-to-fly.md#what-overheating-does). More radiators or
a lower starting acceleration avoid it.

### It stopped, or put me in a "safe orbit" instead of the planned one

Something made the planned arrival impossible, so it did the safe thing instead. The message in the window says what
happened and where the ship is now. From there you can fly the last step yourself, or plan a new trip.

### The final orbit isn't perfectly round

It aims for a circular orbit at the parking altitude and usually lands within a few hundred metres of it, for example
59.9 × 60.2 km for a planned 60 km. That's normal.

## Questions

### Can I go to a moon?

Not directly. Flip and Burn flies between planets, the bodies that orbit the Sun. Fly to the planet, then use MechJeb's
own tools for the hop to its moon. From a moon, get into orbit around its planet first.

### Does it work with Outer Planets Mod and other planet packs?

Yes. The planner lists every planet that orbits the Sun in your game, read from the game itself, so planets added by
mods appear on their own.

### Does it work in career mode?

It should, with no research of its own. In career, MechJeb keeps a window locked only if that window names a part or a
technology it needs, and Flip and Burn's names none. So it appears in MechJeb's menu as soon as the ship has MechJeb.
(MechJeb's own windows do unlock through the tech tree.) All the testing so far has been in a sandbox game.

### What does it change in my game?

During a trip it takes over MechJeb's throttle and steering, and sets the time warp speeds it needs. When the trip
ends, pauses or is aborted, all of that goes back exactly as it was. It never changes your graphics or game settings.

### What happens if I uninstall it in the middle of a trip?

Nothing bad. The ship loads normally without it. It just isn't on a trip any more, and keeps coasting on its current
path.

### Why not just use a normal transfer?

For ordinary rockets, a normal transfer at the right moment is cheaper on fuel, and MechJeb's Maneuver Planner does
that very well. Flip and Burn is for ships with fuel to burn: no waiting for a transfer window, trips of days instead
of months, and a ride that looks like *The Expanse*.

### How do I report a bug?

Open an issue on the [GitHub page](https://github.com/kbennett2000/flip-and-burn/issues). Say what you did and what
happened, and attach your `KSP.log` from the KSP folder. It has everything Flip and Burn noted during the trip.

[← Back to the README](../README.md)
