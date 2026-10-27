# Installing

[← Back to the README](../README.md)

Flip and Burn is a small add-on for MechJeb, so you need MechJeb first. If you already use MechJeb, you probably have
everything else too.

## What you need

| | Version | Where to get it |
|---|---|---|
| **Kerbal Space Program** | 1.12.x (tested on 1.12.5) | Steam, or wherever you bought KSP |
| **MechJeb2** | 2.15 or later (tested on 2.15.3.1) | [MechJeb2 releases on GitHub](https://github.com/MuMech/MechJeb2/releases) |
| **Module Manager** | any recent version (4.2.3 is current) | [Module Manager releases on GitHub](https://github.com/sarbian/ModuleManager/releases) |
| **Flip and Burn** | 1.0.0 | [Flip and Burn releases](https://github.com/kbennett2000/flip-and-burn/releases) |

Module Manager isn't used by Flip and Burn itself. MechJeb needs it, so it's on the list.

If you use **CKAN** (the mod manager), you can install MechJeb2 there and it brings Module Manager along with it. Flip
and Burn itself isn't on CKAN, so install it by hand as below.

## Step by step

1. **Find your GameData folder.** It's inside your KSP folder. On Steam: right-click Kerbal Space Program in your
   library, choose **Manage → Browse local files**, and open `GameData`.
2. **Install MechJeb2 and Module Manager** if you don't have them, following their own instructions. MechJeb's zip holds
   a `MechJeb2` folder that goes into `GameData`. Module Manager is a single file, `ModuleManager.4.2.3.dll`, that goes
   straight into `GameData`.
3. **Download Flip and Burn:** `FlipAndBurn-1.0.0.zip` from the
   [releases page](https://github.com/kbennett2000/flip-and-burn/releases).
4. **Unzip it.** Inside is a `GameData` folder with a `FlipAndBurn` folder in it. Copy the **`FlipAndBurn`** folder
   into your own `GameData` folder.

When you're done, the files should be laid out like this:

```
Kerbal Space Program/
└── GameData/
    ├── FlipAndBurn/
    │   ├── Plugins/
    │   │   ├── FlipAndBurn.dll
    │   │   └── FlipAndBurn.Core.dll
    │   ├── LICENSE.txt
    │   └── README.txt
    ├── MechJeb2/
    │   └── ...
    └── ModuleManager.4.2.3.dll   (or whichever version you have)
```

> **Tip:** copy the `FlipAndBurn` folder, not the zip's `GameData` folder. If you end up with
> `GameData/GameData/FlipAndBurn`, move `FlipAndBurn` up one level so it sits directly in `GameData`, the way every
> other mod does. That keeps updating and removing it simple.

## Check that it worked

1. Start KSP and load a game.
2. Go to any ship **in orbit**. MechJeb adds itself to every command pod and probe core, so any crewed or probe-controlled
   ship will do. Flip and Burn needs no research in career mode.
3. Open MechJeb's menu with the small **MechJeb** tab (on the right edge of the screen, unless you've moved it).
4. **Flip and Burn** should be in the list. Click it and the planner window opens.

<p align="center">
  <img src="images/mechjeb-menu.jpg" alt="MechJeb's menu with Flip and Burn in its list" width="60%">
</p>

If it isn't there, see [Troubleshooting](troubleshooting.md#flip-and-burn-isnt-in-mechjebs-menu).

## Updating and removing

- **To update,** delete the old `GameData/FlipAndBurn` folder and copy in the new one.
- **To remove,** delete `GameData/FlipAndBurn`. Your saves are fine without it. A ship that was mid-trip when you saved
  simply keeps flying on its current path, with no trip running.

Next: [your first trip →](first-trip.md)
