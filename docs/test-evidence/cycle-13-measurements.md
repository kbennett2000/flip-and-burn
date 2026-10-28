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
