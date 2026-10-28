# Cycle 13: what was measured in the game

Kris's KSP 1.12.5 install, test copy of his saves only (`saves/FlipAndBurn-Test`). The lines below are copied from
KSP.log as the test helper wrote them (2026-10-27). Kris stopped further in-game runs before a whole trip was flown; he
is testing the electric engines himself.

## Engines that burn only electric charge: what the planner reads against what KSP draws

Ship: "Nuclear Deep Space Probe" (three Jibar Side Thruster GIANTs, 37.09 t, in orbit around Kerbin). Measured over one
game second at each throttle, the engines' own fuel flow (`fuelFlowGui`) against the planner's figure for that thrust:

```
electric [at load]: drive: 9000 kN, 573590.4 a second at full (63.732 per kN)
electric: throttle 1.0: thrust 9000.0 kN; KSP's own draw (fuel flow) 573590.3 electric charge a second; planner 573590.4 a second for that thrust (-0.00%); batteries 9416559 -> 8842958: net 573588.2 a second over 1.00 s
electric: throttle 0.3: thrust 2700.0 kN; KSP's own draw (fuel flow) 172077.1 electric charge a second; planner 172077.1 a second for that thrust (+0.00%); batteries 8774104 -> 8602016: net 172084.1 a second over 1.00 s
```

The same figures in an earlier run (18:34). The batteries fell 7 a second more than the engines drew at 30 %: the probe's
own use.

With the reactor offline (see `electric-planner-reactor-offline.jpg`), the planner read the batteries alone and
refused the trip with its reasons:

```
electric [1 g] note: The engines need 23,187 a second of electric charge at 1.00 g, and the ship makes none of its own, so the batteries alone last about 6m of burning. Flip and Burn never takes them below the last 10% (960,038), which keeps the probe core and steering working.
electric [1 g] note: Not enough delta-v: the trip needs 692 km/s, but at 1.00 g this ship has 4 km/s. Short by 688 km/s.
```

Not measured in the game: what the reactor delivers when it runs, a trip on electric power, the step-down when power
falls short, and the switched-off note (it is covered by unit tests).

## The Jibar Electric RCS Block (Kris's part, `GameData/JibarIndustries/Parts/Engine/Thrusters/jibar_ElectricRCS.cfg`)

Test craft: an octagonal probe core, a Z-4K battery and the block, put into a 700 km orbit of Kerbin. Fired along one
axis for 10 game seconds, three times in three runs, the same each time:

```
jibar rcs: part loaded True: title 'Jibar Electric RCS Block', category Control, tech advFlightControl, cost 45; plasma resource density 1E-05 t/unit, flow NO_FLOW
jibar rcs: fired (0,1,0) for 10.00 game s: module thrust 1.000 kN; measured acceleration 2.9222 m/s^2 x mass 342.0 kg = 0.999 kN (KSP's accelerometer 2.0002 m/s^2 = 0.684 kN; the craft spinning 411 deg/s); electric charge 3625.9 -> 2988.4: 63.75 per second (expected 63.73 x 1.000 kN = 63.73); plasma 200.0 -> 200.0
jibar rcs: FIRES ON ELECTRIC CHARGE AS CONFIGURED
```

The thrust is measured from the craft's own change of velocity, frame by frame, less gravity's. One block on one side
spins this small craft, so KSP's accelerometer (which reads off the centre of mass) reads low; the frame-by-frame figure
is the one to trust. KSP.log has no errors or warnings about the part or its plasma.

Not measured in the game: the idle draw once the plasma is full, firing after time warp and after a save and reload (the
checks are written in `TestHarness.Jibar.cs`, but runs stopped before they ran), and a legible picture of the block in
the editor's parts list.

## The Jibar Electric RCS Block under TweakScale (2026-10-28)

Kris's report: in the VAB the block showed two Scale rows, the slider made the part vanish, and the chooser did nothing.
KSP.log of his session: `[TweakScale] WARNING: Removing TweakScale support for Jibar.ElectricRCS` ... `didn't passed
the sanity check due having a ModulePartVariants with Mass - see issue [#13]`. His TweakScale (2.4.3) refuses any part
whose variants change its mass or cost, and does the same to the stock RV-105 and ten other stock parts. It removes its
module from the part's list but leaves the module itself behind, never set up: that is the broken menu.

The fix, in the block's TweakScale patch only (`:NEEDS[TweakScale]`, in `jibar_ElectricRCS.cfg` in Kris's GameData):
the five variants keep their shapes but no longer change mass or cost (they were +10 kg / +7 for the 5-horn, -10 kg / -8
for the 3-horn, -20 kg / -15 for the 2-horn). Every variant now weighs 40 kg and costs 45 at 100 %, like the Angled
4-horn. After it, TweakScale's start-up check: `0 Show Stoppers found; 16 Sanity Check failed` (17 before: the block
no longer fails).

One test launch, test copy, the test craft in the VAB, the Scale slider's own field set (as dragging it does):

```
jibar scale: TweakScale modules on the block: 1; variants: Angled 4-horn (mass 0, cost 0), Orthogonal 5-horn (mass 0, cost 0), Orthogonal 4-horn (mass 0, cost 0), Orthogonal 3-horn (mass 0, cost 0), Orthogonal 2-horn (mass 0, cost 0)
jibar scale: Scale rows shown: slider True, chooser False; type free True, defaultScale 100, now 100
jibar scale: at 100%: thrust 1.0000 kN (expected 1.0000); converter takes 318.75 electric charge a second (expected 318.75) and makes 212.50 plasma (expected 212.50); plasma store 200.0 (expected 200.0); firing it would use 63.73 electric charge a second; ... visible True (5 renderers on), size 0.605 m (1.00x), model scale 1.250: AS EXPECTED
jibar scale: at 50%: thrust 0.1768 kN (expected 0.1768); converter takes 56.35 electric charge a second (expected 56.35) and makes 37.57 plasma (expected 37.57); plasma store 25.0 (expected 25.0); firing it would use 11.27 electric charge a second; ... visible True (5 renderers on), size 0.303 m (0.50x), model scale 0.625: AS EXPECTED
jibar scale: at 200%: thrust 5.6569 kN (expected 5.6569); converter takes 1803.12 electric charge a second (expected 1803.12) and makes 1202.08 plasma (expected 1202.08); plasma store 1600.0 (expected 1600.0); firing it would use 360.52 electric charge a second; ... visible True (5 renderers on), size 1.210 m (2.00x), model scale 2.500: AS EXPECTED
jibar scale: at 200%, variant Orthogonal 5-horn: visible True, size 1.315 m, ... thrust 5.6569 kN   (and the same for all five variants)
```

(The helper's "mass" figure counts TweakScale's mass change twice; the block's own mass is 40 kg x size^2.5: 7.1 kg at
50 %, 226 kg at 200 %, which the craft's total mass in flight confirms: 307.3 kg and 542.3 kg.)

Both sizes saved as craft in the test copy, put in orbit, and fired:

```
jibar scale: in orbit at 50%: thrust 0.1768 kN, converter 56.35 a second, plasma 25.0 of 25.0, visible True: SAME AS THE EDITOR
jibar rcs: at 50%: fired (0,1,0) for 10.00 game s: module thrust 0.177 kN; measured ... = 0.178 kN; electric charge 3987.3 -> 3874.4: 11.29 per second (expected 63.73 x 0.177 kN = 11.27); plasma 25.0 -> 25.0
jibar scale: in orbit at 200%: thrust 5.6569 kN, converter 1803.12 a second, plasma 1600.0 of 1600.0, visible True: SAME AS THE EDITOR
jibar rcs: at 200%: fired (0,1,0) for 3.00 game s: module thrust 5.657 kN; measured ... = 5.625 kN; electric charge 3296.0 -> 2214.3: 360.54 per second (expected 63.73 x 5.657 kN = 360.52); plasma 1600.0 -> 1600.0
jibar scale: SCALES WITH TWEAKSCALE: ONE SCALE ROW, VISIBLE AT 50/100/200%, THRUST AND CHARGE AS EXPECTED, FIRES AT 50% AND 200%
```

Nothing started the plasma charger in either run: it runs by itself in flight (`AlwaysActive`), as in the firing runs
above. "Plasma Charger: Inactive" in the VAB is only the editor. Pictures: `jibar-scale-menu.jpg` (the right-click menu
at 200 %, one Scale row), `jibar-scale-menu-closeup.jpg`, `jibar-scale-200.jpg` (the 200 % craft in orbit). Settings
restored and checked identical afterwards; no other save touched.
