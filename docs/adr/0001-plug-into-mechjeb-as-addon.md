# ADR 0001: Plug into MechJeb as a separate add-on DLL

- Status: Accepted
- Date: 2026-10-26

## Context

FlipAndBurn should live inside MechJeb as a new autopilot and window. We need to know if a separate DLL in its own
GameData folder can add a MechJeb module without changing MechJeb.

The installed MechJeb is `GameData/MechJeb2/Plugins/MechJeb2.dll`, file version 2.9.1.0 (KSPAssembly `MechJeb2` V2.5.0,
dated 2019-11-15). There is no `v2.9.1` tag in MuMech/MechJeb2; the matching source is commit `579f1bd2` ("v2.9.1",
2019-11-15), merged to master as `a899d181`. All line numbers below are from `579f1bd2`.

## Evidence

### How MechJeb finds modules

`MechJeb2/MechJebCore.cs`, `LoadComputerModules()`:

- Lines 736-753: on first use it builds a static `moduleRegistry` by walking `AppDomain.CurrentDomain.GetAssemblies()`
  (line 739) and keeping every type where `t.IsSubclassOf(typeof(ComputerModule)) && !t.IsAbstract` (line 743). It looks
  at every loaded assembly, not just its own, so a type in `FlipAndBurn.dll` is picked up.
- Lines 748-751: if one assembly's `GetTypes()` throws, MechJeb logs it and moves on to the next one.
- Lines 777-786: for each registered type that is not on the `blacklist` (line 87) and whose short type name is not
  already taken (`GetComputerModule(t.Name) == null`, line 781), it looks for a constructor taking `MechJebCore`
  (line 783) and calls `AddComputerModule` on the new instance.
- Line 841: `OnLoad` calls `LoadComputerModules()`. KSP has loaded every GameData DLL long before any vessel's MechJeb
  part loads, so our assembly is in the AppDomain by then.

`MechJeb2/ModExtensionDemo.cs` (whole file) is MechJeb's own sample of a module defined outside its core set, using
only `ComputerModule` and the `(MechJebCore core)` constructor.

### How a window shows up

- `MechJeb2/DisplayModule.cs:7` `DisplayModule : ComputerModule`; constructor at line 128.
- `DisplayModule.cs:9` `hidden`, `:59` `showInFlight` (default true), `:74` `showInEditor`, `:123` `showInCurrentScene`.
- `DisplayModule.cs:172` `protected virtual void WindowGUI(int windowID)` draws the window body; `:317`
  `public virtual string GetName()` is the title.
- `MechJeb2/MechJebModuleMenu.cs:193-208`: the MechJeb menu lists every display module with `!hidden &&
  showInCurrentScene` and toggles it by `GetName()`. `:257-309` adds a toolbar button per display module. The icon is
  looked up at `MechJeb2/Icons/<name>` (line 266); when missing it falls back to a question mark (lines 272-276).
- `MechJeb2/ComputerModule.cs:22-24`, `:163-200`: `unlockParts` / `unlockTechs` gate a module in career. Both empty
  means always unlocked.

### How a module points the ship

`MechJeb2/MechJebModuleAttitudeController.cs`:

- Line 274: `public bool attitudeTo(Vector3d direction, AttitudeReference reference, object controller)`. Passing
  `AttitudeReference.INERTIAL` (enum at line 8, value at line 10) with a world-space direction points the ship there.
  It adds `controller` to the attitude controller's `users` (line 267), which turns it on.
- Line 265: `attitudeTo(Quaternion, AttitudeReference, object, ...)` for full orientation including roll.
- Line 300: `attitudeDeactivate()` releases it.
- Reached from a module as `core.attitude` (`MechJebCore.cs:35`).

### How a module sets throttle

`MechJeb2/MechJebModuleThrustController.cs`, reached as `core.thrust` (`MechJebCore.cs:37`):

- Line 231: `public float targetThrottle`.
- Lines 441-443: `Drive()` writes `s.mainThrottle = targetThrottle` only when `users.Count > 1`. A module must call
  `core.thrust.users.Add(this)` before `targetThrottle` takes effect, and remove itself when done.
- Line 305: `ThrustForDV(double dV, double timeConstant)` sets `targetThrottle` for a burn with a given delta-v left.
- A module can also override `ComputerModule.Drive(FlightCtrlState s)` (`ComputerModule.cs:92`) and write the controls
  itself.

### Existing acceleration limit

Yes. `MechJebModuleThrustController.cs`:

- Lines 122-126: `public bool limitAcceleration` and `public EditableDouble maxAcceleration = 40` (m/s²), both
  `[Persistent(pass = Pass.Global)]`.
- Lines 484-489: in `Drive()`, when `limitAcceleration` is on, the throttle is capped at `AccelerationLimitedThrottle()`.
- Lines 831-835: that cap is `(maxAcceleration - minThrustAccel) / (maxThrustAccel - minThrustAccel)`, recomputed every
  tick from the current `vesselState`. As fuel burns and the ship gets lighter, `maxThrustAccel` rises and the cap
  drops, which is the "ease the throttle down to hold 1g" behaviour we want.

Caveat: these two fields are global settings (`Pass.Global`) saved to `mechjeb_settings_global.cfg`. If FlipAndBurn
drives them, it changes the player's own ascent setting. FlipAndBurn must save the old values when it takes over and
put them back when it lets go, or compute its own throttle instead.

## Decision

FlipAndBurn ships as its own DLL in `GameData/FlipAndBurn/Plugins/`, compiled against the installed `MechJeb2.dll`
and never modifying MechJeb. It defines:

- A `DisplayModule` subclass for the window, with `GetName()` returning "Flip and Burn".
- The autopilot as a `ComputerModule` (or `AutopilotModule`, `MechJeb2/AutopilotModule.cs:6`) subclass that commands
  `core.attitude.attitudeTo(..., AttitudeReference.INERTIAL, this)` and registers as a `core.thrust` user.

The assembly declares `[KSPAssemblyDependency("MechJeb2", 2, 5)]` so KSP loads MechJeb first and skips us if it is
missing.

## Consequences

- No fork of MechJeb to maintain. Players install FlipAndBurn next to their existing MechJeb.
- Type names must not collide with MechJeb's own, because MechJeb dedupes by short type name (`MechJebCore.cs:781`).
  Use names that start with `MechJebModuleFlipAndBurn`.
- The toolbar icon has to live under `MechJeb2/Icons/`, which we will not touch. We accept the question-mark icon for
  now.
- We compile against MechJeb's public fields and methods, which have no stability promise. A MechJeb upgrade can break
  the build. We pin to the installed 2.9.1 and recheck on upgrade.
- MechJeb 2.9.1 predates KSP 1.12.5 by three years. It loads today with no MechJeb exceptions in the existing KSP.log,
  but a newer MechJeb (2.14+) may be worth a separate decision.
