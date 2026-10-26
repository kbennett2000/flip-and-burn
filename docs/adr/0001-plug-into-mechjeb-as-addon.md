# ADR 0001: Plug into MechJeb as a separate add-on DLL

- Status: Accepted
- Date: 2026-10-26
- Revised: 2026-10-26, re-verified against MechJeb2 2.15.3.1 (first written against 2.9.1)

## Context

FlipAndBurn should live inside MechJeb as a new autopilot and window. We need to know if a separate DLL in its own
GameData folder can add a MechJeb module without changing MechJeb.

The installed MechJeb is 2.15.3.1 (GitHub release `2.15.3.1`, 2026-09-29, KSPAssembly `MechJeb2` 2.15.3). The matching
source is tag `2.15.3.1`, commit `28b49be3`. All line numbers below are from that tag and are relative to the
`MechJeb2/` source folder.

## Evidence

### How MechJeb finds modules

`MechJebCore.cs`, `LoadComputerModules()` (line 677):

- Lines 679-697: on first use it builds a static `_moduleRegistry` (line 32) by walking
  `AppDomain.CurrentDomain.GetAssemblies()` (line 682) and keeping every type where
  `t.IsSubclassOf(typeof(ComputerModule)) && !t.IsAbstract` (line 686). It looks at every loaded assembly, not just its
  own, so a type in `FlipAndBurn.dll` is picked up.
- Lines 692-695: if one assembly's `GetTypes()` throws, MechJeb logs it and moves on.
- Lines 719-729: for each registered type that is not on the `blacklist` (line 93) and whose short type name is not
  already taken (`GetComputerModule(t.Name) == null`, line 723), it looks up a constructor taking `MechJebCore`
  (line 725) and calls `AddComputerModule` (line 727). `Type.GetConstructor(Type[])` only finds public constructors,
  so our module's constructor must be public.
- Line 787: `OnLoad` calls `LoadComputerModules()`. KSP has loaded every GameData DLL long before any vessel's MechJeb
  part loads, so our assembly is in the AppDomain by then.

`ModExtensionDemo.cs:9-11` is MechJeb's own sample of a module defined outside its core set: a `ComputerModule` with a
public `(MechJebCore core)` constructor.

### How a window shows up

- `DisplayModule.cs:9` `DisplayModule : ComputerModule`; constructor at line 132 (now `protected`, fine for a
  subclass).
- `DisplayModule.cs:11` `Hidden`, `:63` `ShowInFlight`, `:81` `ShowInEditor`, `:127` `ShowInCurrentScene`.
- `DisplayModule.cs:173` `protected virtual void WindowGUI(int windowID)` draws the window body; `:310`
  `public virtual string GetName()` is the title; `:312` `public virtual string IconName()` names the toolbar icon.
- `MechJebModuleMenu.cs:210-228`: the MechJeb menu lists every display module with `!Hidden && ShowInCurrentScene`
  (line 219) and toggles it by `GetName()` (line 228).
- `MechJebModuleMenu.cs:280-333` adds a toolbar button per display module. The icon is looked up at
  `MechJeb2/Icons/<IconName()>` (lines 287-289); when missing it falls back to `MechJeb2/Icons/QMark` (lines 185,
  295-298).
- `ComputerModule.cs:32`, `:36`, `:189-244`: `unlockParts` / `unlockTechs` gate a module in career. Both empty means
  always unlocked.

### How a module points the ship

`MechJebModuleAttitudeController.cs`, reached from a module as `Core.Attitude` (`MechJebCore.cs:38`):

- Line 264: `public void attitudeTo(Vector3d direction, AttitudeReference reference, object controller,
  bool killRollRotation = false)`. Passing `AttitudeReference.INERTIAL` (enum at line 10, value at line 12) with a
  world-space direction points the ship there.
- Line 254: `attitudeTo(QuaternionD attitude, AttitudeReference reference, object controller, ...)` for full
  orientation including roll. It adds `controller` to the controller's `Users` (line 257), which turns it on.
- Line 289: `attitudeDeactivate()` releases it.

### How a module sets throttle

`MechJebModuleThrustController.cs`, reached as `Core.Thrust` (`MechJebCore.cs:40`):

- Line 232: `public float TargetThrottle`.
- Lines 456-459: `Drive()` writes `s.mainThrottle = TargetThrottle` only when `Users.Count > 1`. A module must call
  `Core.Thrust.Users.Add(this)` before `TargetThrottle` takes effect, and remove itself when done.
- Line 313: `ThrustForDV(double dV, double timeConstant)` sets `TargetThrottle` for a burn with a given delta-v left.
- A module can also override `ComputerModule.Drive(FlightCtrlState s)` (`ComputerModule.cs:100`) and write the
  controls itself.

### Existing acceleration limit

Yes. `MechJebModuleThrustController.cs`:

- Lines 126-131: `public bool LimitAcceleration` and `public readonly EditableDouble MaxAcceleration = 40` (m/s²), both
  `[Persistent(pass = (int)Pass.GLOBAL)]`.
- Lines 501-507: in `Drive()`, when `LimitAcceleration` is on, the throttle is capped at `AccelerationLimitedThrottle()`.
- Lines 827-831: that cap is `(MaxAcceleration - minThrustAccel) / (maxThrustAccel - minThrustAccel)`, recomputed
  every tick from the current `VesselState`. As fuel burns and the ship gets lighter, `maxThrustAccel` rises and the
  cap drops, which is the "ease the throttle down to hold 1g" behaviour we want.

Caveat: these are global settings (`Pass.GLOBAL`) saved to `mechjeb_settings_global.cfg`. If FlipAndBurn drives them,
it changes the player's own setting. FlipAndBurn must save the old values when it takes over and put them back when
it lets go, or compute its own throttle instead. `MaxAcceleration` is now `readonly`, so we set its value through
`EditableDouble` rather than replacing the object.

## Changes from 2.9.1 (commit `579f1bd2`)

The add-on route works the same way. What changed for us:

- MechJeb targets .NET Framework 4.8 (`MechJeb2.csproj:12`), up from 4.7. FlipAndBurn must target `net48` to compile
  against it.
- Public fields became PascalCase: `core` -> `Core`, `users` -> `Users`, `enabled` -> `Enabled`, `hidden` -> `Hidden`,
  `showInCurrentScene` -> `ShowInCurrentScene`, `core.attitude` -> `Core.Attitude`, `core.thrust` -> `Core.Thrust`,
  `targetThrottle` -> `TargetThrottle`, `limitAcceleration` -> `LimitAcceleration`, `maxAcceleration` ->
  `MaxAcceleration`, `Pass.Global` -> `Pass.GLOBAL`.
- `attitudeTo` returns `void` (was `bool`) and the quaternion overload takes `QuaternionD`. The direction overload gained
  an optional `killRollRotation`. A new `AttitudeReference.INERTIAL_COT` sits next to `INERTIAL`.
- `ComputerModule`, `DisplayModule` and `AutopilotModule` constructors are now `protected`. Subclasses still need a
  public one for discovery.
- Toolbar icons are keyed by the new `DisplayModule.IconName()` instead of `GetName()`.
- Discovery also skips `MechJebModuleAscentBaseAutopilot` (line 722).
- MechJeb now ships `MechJebLib.dll`, `MechJebLibBindings.dll` and `alglib.dll` next to `MechJeb2.dll`.
- `KSPAssembly` is `MechJeb2` 2.15.3 (`Properties/AssemblyInfo.cs:31`), so we now declare
  `[KSPAssemblyDependency("MechJeb2", 2, 15)]`.

## Decision

FlipAndBurn ships as its own DLL in `GameData/FlipAndBurn/Plugins/`, compiled against the installed `MechJeb2.dll`
and never modifying MechJeb. It defines:

- A `DisplayModule` subclass for the window, with `GetName()` returning "Flip and Burn".
- The autopilot as a `ComputerModule` (or `AutopilotModule`, `AutopilotModule.cs:6`) subclass that commands
  `Core.Attitude.attitudeTo(..., AttitudeReference.INERTIAL, this)` and registers as a `Core.Thrust` user.

The assembly declares `[KSPAssemblyDependency("MechJeb2", 2, 15)]` so KSP loads MechJeb first and skips us if it is
missing or older.

## Consequences

- No fork of MechJeb to maintain. Players install FlipAndBurn next to their existing MechJeb.
- Type names must not collide with MechJeb's own, because MechJeb dedupes by short type name (`MechJebCore.cs:723`).
  Use names that start with `MechJebModuleFlipAndBurn`.
- The toolbar icon has to live under `MechJeb2/Icons/`, which we will not touch. We accept the question-mark icon for
  now.
- We compile against MechJeb's public fields and methods, which have no stability promise. The 2.9.1 -> 2.15.3.1 jump
  renamed most of them. We pin to the installed version and recheck on every MechJeb upgrade.
