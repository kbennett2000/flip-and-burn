using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 10: a trip carries on after saving, loading and leaving the ship (ADR 0004). Every way of leaving goes through
    // the same code KSP's own buttons use, and only when KSP would allow it (FlightGlobals.ClearToSave): KSP won't save,
    // switch ships or leave flight while a ship accelerates at more than 0.1 g, so the test presses Pause first, as a
    // player must. Every save goes into the test copy (FlipAndBurn-Test) and never over its quicksave, which every run
    // starts from.
    public partial class TestHarness
    {
        private const string ResumeShip = "Bebop Refueller";
        private const string QuickSaveName = "fab-quicksave";     // F5 in the test, under its own name
        private const string MidTripSave = "fab-midtrip";         // loaded after KSP is relaunched, and with the mod removed
        private const string AfterAbortSave = "fab-after-abort";
        // A second ship with its own trip: another KSP Interstellar ship, in orbit around Duna. Its targets are tried in
        // turn (Kerbin's pull at the parking orbit needs 0.86 g; Dres and Eeloo pull far less).
        private const string SecondShip = "Bebop V2.4";
        private static readonly string[] SecondTargets = { "Dres", "Eeloo", "Kerbin" };
        private const double ResumeAcceleration = 0.4;            // g: a coast trip the Bebop can fly without cheats

        // ------------------------------------------------------------------ main run

        private IEnumerator ProofResume(Flown f, CaptureResult outcome)
        {
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurn module = f.Module;
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = ResumeAcceleration;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            Log($"step {step}: (resume) coast trip to Duna at {ResumeAcceleration} g, no cheats; planner can start: {module.Planner.CanStart}");
            LogBeforeStart(f);
            LogState("before Start", f);

            // 1. Start a coast trip and burn a minute.
            string problem = module.PressStart();
            if (problem != null) { Fail("step " + step, "Start refused: " + problem); yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            yield return BurnFor(pilot, 60, "a minute of burn");
            if (!pilot.Running) { Fail("step " + step, "trip stopped: " + pilot.StopReason); yield break; }
            LogState("after a minute of burn", f);

            // MechJeb writes its settings file every few seconds during a trip: it must still hold the player's limit.
            f.Core.OnSave(null);   // MechJeb's own periodic save, now
            Log($"step {step}: MechJeb's own settings save mid-trip: in memory LimitAcceleration {f.Core.Thrust.LimitAcceleration}, MaxAcceleration " +
                $"{f.Core.Thrust.MaxAcceleration.Val:F3} (the trip's); in mechjeb_settings_global.cfg {GlobalLimit()} (must be the player's: {playerGlobalLimit})");
            step++;

            // 2. Go to the Space Center: Pause first (the trip is burning), then the Space Center button.
            yield return PauseUntilClear(f, "r-0-paused.png");
            if (failed) yield break;
            yield return LeaveToSpaceCenter();
            LogTripInSave("persistent", ResumeShip, "in the Space Center");

            // 3. Come back to the ship (the Tracking Station's Fly button): the trip resumes by itself.
            var back = new Flown();
            yield return FlyFromSave("persistent", ResumeShip, back, asQuickload: false);
            if (!back.Ok) yield break;
            yield return WatchResume(back, "after the Space Center", "r-1-resumed-after-space-center.png");
            if (failed) yield break;

            // 4. Quicksave (Pause first), burn a bit, quickload: the trip resumes from the quicksave.
            yield return PauseUntilClear(back, null);
            if (failed) yield break;
            yield return QuickSave(QuickSaveName);
            TripRecord atSave = back.Module.Autopilot.Record;
            double savedUT = atSave.LastFlownUT, savedUsed = atSave.DeltaVUsed;
            Log($"step {step}: quicksaved '{QuickSaveName}' at UT {savedUT:F1}, delta-v used {savedUsed:F0} m/s");
            LogTripInSave(QuickSaveName, ResumeShip, "in the quicksave");
            problem = back.Module.PressResume();
            if (problem != null) { Fail("step " + step, "Resume refused: " + problem); yield break; }
            yield return BurnFor(back.Module.Autopilot, 60, "burn a bit after the quicksave");
            Log($"step {step}: after burning on: UT {Planetarium.GetUniversalTime():F1}, delta-v used {back.Module.Autopilot.Record.DeltaVUsed:F0} m/s");
            step++;
            var quick = new Flown();
            yield return FlyFromSave(QuickSaveName, ResumeShip, quick, asQuickload: true);
            if (!quick.Ok) yield break;
            yield return WatchResume(quick, "after the quickload", "r-2-resumed-after-quickload.png");
            if (failed) yield break;
            TripRecord r = quick.Module.Autopilot.Record;
            Log($"step {step}: resumed from the quicksave: delta-v used {r.DeltaVUsed:F0} m/s (quicksave had {savedUsed:F0}), " +
                $"away {quick.Module.Autopilot.LastAwaySeconds:F1} s measured from the quicksave's UT {savedUT:F1}: " +
                (Math.Abs(r.DeltaVUsed - savedUsed) < 1 ? "the quicksave's trip" : "NOT the quicksave's trip"));
            step++;

            // 5. Switch to another ship and back; the second ship flies a trip of its own.
            Flown here = quick;
            yield return SecondShipAndBack(here, result => here = result);
            if (failed) yield break;

            // 6. A save made mid-trip, for the relaunch and the mod-removed check. While paused, the ship is turned 90 degrees
            // away (as a player might, looking around): Resume, and the relaunch, must turn it back before the engine fires.
            yield return PauseUntilClear(here, null);
            if (failed) yield break;
            TurnAway(here.Ship, 90);
            yield return new WaitForSecondsRealtime(2f);
            yield return QuickSave(MidTripSave);
            LogTripInSave(MidTripSave, ResumeShip, "in the mid-trip save");
            problem = here.Module.PressResume();
            if (problem != null) { Fail("step " + step, "Resume refused: " + problem); yield break; }
            yield return WatchResume(here, "after Resume, turned 90 degrees away", null, alreadyRunning: true);
            if (failed) yield break;
            yield return BurnFor(here.Module.Autopilot, 20, "back on the trip");

            // 7. Abort, quicksave, quickload: nothing comes back.
            here.Module.PressAbort();
            Log($"step {step}: Abort pressed: {here.Module.Autopilot.StopReason} {here.Module.Autopilot.SettingsCheck}");
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(here);
            LogState("after Abort", here);
            yield return WaitClear();
            yield return QuickSave(AfterAbortSave);
            bool tripSaved = LogTripInSave(AfterAbortSave, ResumeShip, "in the save made after Abort");
            var reloaded = new Flown();
            yield return FlyFromSave(AfterAbortSave, ResumeShip, reloaded, asQuickload: true);
            if (!reloaded.Ok) yield break;
            yield return new WaitForSecondsRealtime(20f);   // long past when a trip would have resumed
            MechJebModuleFlipAndBurnAutopilot after = reloaded.Module.Autopilot;
            reloaded.Module.Enabled = true;
            LogState("20 s after loading the save made after Abort", reloaded);
            bool nothing = !after.Running && !after.Paused && after.Record == null && !tripSaved;
            Log($"step {step}: after Abort, quicksave and quickload: running {after.Running}, paused {after.Paused}, trip in the save {tripSaved}: " +
                (nothing ? "nothing came back" : "SOMETHING CAME BACK"));
            yield return Shot("r-4-no-trip-after-abort-reload.png", reloaded);
            if (!nothing) { Fail("step " + step, "a trip came back after Abort"); yield break; }
            outcome.Ok = true;
        }

        // ------------------------------------------------------------------ after a relaunch (FLIPANDBURN_PHASE=relaunch)

        private IEnumerator ProofResumeAfterRelaunch()
        {
            Log($"step {step}: KSP was quit and launched again; resuming the test save from the main menu, then loading '{MidTripSave}', saved mid-trip");
            yield return ResumeFromMainMenu();
            if (failed) yield break;
            var f = new Flown();
            yield return FlyFromSave(MidTripSave, ResumeShip, f, asQuickload: true);
            if (!f.Ok) yield break;
            yield return WatchResume(f, "after relaunching KSP", "r-5-resumed-after-relaunch.png");
            if (failed) yield break;
            yield return BurnFor(f.Module.Autopilot, 30, "flying on after the relaunch");
            f.Module.PressAbort();
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(f);
            LogState("after Abort (relaunch)", f);
        }

        // ------------------------------------------------------------------ the second ship

        private IEnumerator SecondShipAndBack(Flown bebop, Action<Flown> now)
        {
            yield return PauseUntilClear(bebop, null);
            if (failed) yield break;
            var second = new Flown();
            yield return SwitchTo(SecondShip, second);
            if (failed || !second.Ok) yield break;
            LogState("on the second ship", second);
            LogTripInSave("persistent", ResumeShip, "while on the second ship");

            // Its own trip, if this ship can fly one: try what its planner allows.
            bool started = false;
            MechJebModuleFlipAndBurn m2 = second.Module;
            OpenWindow(second);
            foreach (string target in SecondTargets)
            {
                if (started) break;
                if (!m2.SelectTarget(target))
                {
                    Log($"step {step}: the second ship can't pick {target}");
                    continue;
                }

                foreach ((TripType type, double g) in new[] { (TripType.CoastInTheMiddle, 0.4), (TripType.FullBurn, 0.4), (TripType.CoastInTheMiddle, 0.2), (TripType.CoastInTheMiddle, 1.0) })
                {
                    m2.AccelerationG.Val = g;
                    m2.SelectTripType(type);
                    yield return new WaitForSecondsRealtime(1f);
                    m2.Planner.Refresh(second.Ship);
                    Log($"step {step}: second ship, {type} to {target} at {g} g: can start {m2.Planner.CanStart}" +
                        (m2.Planner.CanStart ? "" : "; " + string.Join(" ", m2.Planner.Notes)));
                    if (!m2.Planner.CanStart) continue;
                    LogBeforeStart(second);
                    string problem = m2.PressStart();
                    Log($"step {step}: second ship's own trip to {target}: {(problem ?? "started")}");
                    started = problem == null;
                    break;
                }
            }

            step++;
            if (started)
            {
                LogPlan(m2.Autopilot);
                yield return BurnFor(m2.Autopilot, 30, "second ship's trip");
                LogState("second ship flying its own trip", second);
                yield return PauseUntilClear(second, null);
                if (failed) yield break;
            }
            else
            {
                Log($"step {step}: the second ship can't fly a trip of its own here; switching back without one");
            }

            // Back to the Bebop: its trip resumes; the second ship's stays saved, paused.
            var again = new Flown();
            yield return SwitchTo(ResumeShip, again);
            if (failed || !again.Ok) yield break;
            yield return WatchResume(again, "after switching ships and back", "r-3-resumed-after-switching-back.png");
            if (failed) yield break;
            if (started) LogTripInSave("persistent", SecondShip, "the second ship's own trip, while on the Bebop");
            now(again);
            if (!started) yield break;

            // The second ship's trip carries on when the player goes back to it; then it is aborted.
            yield return PauseUntilClear(again, null);
            if (failed) yield break;
            var second2 = new Flown();
            yield return SwitchTo(SecondShip, second2);
            if (failed || !second2.Ok) yield break;
            yield return WatchResume(second2, "on the second ship again", "r-3b-second-ship-resumed.png");
            if (failed) yield break;
            second2.Module.PressAbort();
            yield return new WaitForSecondsRealtime(3f);
            LogAfterAbort(second2);
            LogState("second ship after Abort", second2);
            yield return WaitClear();
            var third = new Flown();
            yield return SwitchTo(ResumeShip, third);
            if (failed || !third.Ok) yield break;
            yield return WatchResume(third, "back on the Bebop", null);
            if (failed) yield break;
            bool secondLeft = LogTripInSave("persistent", SecondShip, "the second ship after its Abort");
            Log($"step {step}: the second ship's trip after its Abort: {(secondLeft ? "STILL SAVED" : "gone, as it should be")}");
            if (secondLeft) { Fail("step " + step, "the second ship's aborted trip is still in the save"); yield break; }
            now(third);
        }

        // ------------------------------------------------------------------ helpers

        private bool failed;

        private IEnumerator BurnFor(MechJebModuleFlipAndBurnAutopilot pilot, double gameSeconds, string what)
        {
            double since = double.NaN;
            yield return Fly(pilot, () =>
            {
                bool burning = pilot.Last != null && pilot.MeasuredAcceleration > 0.5 * pilot.Last.Acceleration;
                if (burning && double.IsNaN(since)) since = Planetarium.GetUniversalTime();
                return !double.IsNaN(since) && Planetarium.GetUniversalTime() - since > gameSeconds;
            }, 600, what);
        }

        /// <summary>The Pause button, then wait until KSP allows saving (the ship no longer accelerating).</summary>
        private IEnumerator PauseUntilClear(Flown f, string shot)
        {
            f.Module.PressPause();
            MechJebModuleFlipAndBurnAutopilot pilot = f.Module.Autopilot;
            Log($"step {step}: Pause pressed: paused {pilot.Paused}, notice: {pilot.PauseNotice}");
            if (!pilot.Paused) { Fail("step " + step, "the trip didn't pause"); failed = true; yield break; }
            yield return WaitClear();
            if (failed) yield break;
            LogState("paused", f);
            if (shot != null) yield return Shot(shot, f);
        }

        private IEnumerator WaitClear()
        {
            float t0 = Time.realtimeSinceStartup;
            while (FlightGlobals.ClearToSave(false) != ClearToSaveStatus.CLEAR && Time.realtimeSinceStartup - t0 < 60)
                yield return new WaitForSecondsRealtime(0.5f);
            ClearToSaveStatus status = FlightGlobals.ClearToSave(false);
            Log($"step {step}: KSP allows saving and leaving: {status} (acceleration {FlightGlobals.ActiveVessel.geeForce:F3} g)");
            if (status != ClearToSaveStatus.CLEAR) { Fail("step " + step, "KSP won't allow saving: " + status); failed = true; }
        }

        // Every save goes to the test copy, never over its quicksave.
        private bool SafeToSave(string name)
        {
            bool ok = HighLogic.SaveFolder == SaveFolder && name != "quicksave";
            if (!ok) { Fail("step " + step, $"refusing to save '{name}' in '{HighLogic.SaveFolder}'"); failed = true; }
            return ok;
        }

        /// <summary>F5, as KSP's QuickSaveLoad does it, under a name of the test's own.</summary>
        private IEnumerator QuickSave(string name)
        {
            if (!SafeToSave(name)) yield break;
            if (FlightGlobals.ClearToSave(false) != ClearToSaveStatus.CLEAR) { Fail("step " + step, "KSP won't quicksave now"); failed = true; yield break; }
            GameEvents.onGameAboutToQuicksave.Fire();
            Game game = HighLogic.CurrentGame.Updated();
            game.startScene = GameScenes.FLIGHT;
            GamePersistence.SaveGame(game, name, HighLogic.SaveFolder, SaveMode.BACKUP);
            game.startScene = GameScenes.SPACECENTER;
            Log($"step {step}: quicksaved to saves/{HighLogic.SaveFolder}/{name}.sfs at UT {Planetarium.GetUniversalTime():F1}");
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>The Space Center button, as AltimeterSliderButtons does it: save persistent, leave.</summary>
        private IEnumerator LeaveToSpaceCenter()
        {
            if (!SafeToSave("persistent")) yield break;
            if (FlightGlobals.ClearToSave(false) != ClearToSaveStatus.CLEAR) { Fail("step " + step, "the Space Center button would be locked"); failed = true; yield break; }
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            Log($"step {step}: leaving for the Space Center");
            HighLogic.LoadScene(GameScenes.SPACECENTER);
            float t0 = Time.realtimeSinceStartup;
            while (!(HighLogic.LoadedScene == GameScenes.SPACECENTER && TimeWarp.fetch != null) && Time.realtimeSinceStartup - t0 < 300)
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);   // BetterTimeWarp sets its rates once the scene's GUI is ready
            Log($"step {step}: in the Space Center: physics rates {Rates(TimeWarp.fetch?.physicsWarpRates)}, values " +
                $"{(SameValues(TimeWarp.fetch?.physicsWarpRates) ? "the player's" : "NOT the player's")} (before Start {ratesBeforeStart}); " +
                $"MechJeb's settings file: {GlobalLimit()} (player's: {playerGlobalLimit})");
            step++;
        }

        /// <summary>Switch ships as the map view does: FlightGlobals.SetActiveVessel (saves and reloads for an unloaded ship).</summary>
        private IEnumerator SwitchTo(string shipName, Flown outcome)
        {
            Vessel v = FlightGlobals.Vessels.FirstOrDefault(x => x.vesselName == shipName);
            if (v == null) { Fail("step " + step, "no ship called " + shipName); failed = true; yield break; }
            if (!SafeToSave("persistent")) yield break;
            Log($"step {step}: switching to '{shipName}' (loaded {v.loaded})");
            Vessel old = FlightGlobals.ActiveVessel;
            if (!FlightGlobals.SetActiveVessel(v)) { Fail("step " + step, "KSP refused to switch to " + shipName); failed = true; yield break; }
            yield return WaitForShip(shipName, outcome, old);
        }

        /// <summary>
        /// The main menu's Resume Game, as MainMenu.OnLoadDialogPipelineFinished does it: the save folder's persistent game,
        /// started in the scene it was saved in. (Loading a named save straight from the main menu froze KSP.)
        /// </summary>
        private IEnumerator ResumeFromMainMenu()
        {
            try
            {
                ConfigNode node = GamePersistence.LoadSFSFile("persistent", SaveFolder);
                HighLogic.CurrentGame = node == null ? null : GamePersistence.LoadGameCfg(node, SaveFolder, true, false);
                if (HighLogic.CurrentGame == null) { Fail("step " + step, "could not resume the test save"); failed = true; yield break; }
                GamePersistence.UpdateScenarioModules(HighLogic.CurrentGame);
                GamePersistence.SaveGame(HighLogic.CurrentGame, "persistent", SaveFolder, SaveMode.OVERWRITE);
                GameEvents.onGameStatePostLoad.Fire(node);
                HighLogic.SaveFolder = SaveFolder;
                Log($"step {step}: resuming the test save (it starts in {HighLogic.CurrentGame.startScene})");
                HighLogic.CurrentGame.Start();
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                failed = true;
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + 300;
            while (Time.realtimeSinceStartup < deadline &&
                   !(HighLogic.LoadedScene == GameScenes.SPACECENTER || (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed)))
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);
            Log($"step {step}: resumed into {HighLogic.LoadedScene}");
            step++;
        }

        /// <summary>Fly a save: the Tracking Station's Fly button, or F9 (quickload).</summary>
        private IEnumerator FlyFromSave(string saveName, string shipName, Flown outcome, bool asQuickload)
        {
            Vessel old = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            try
            {
                ConfigNode node = GamePersistence.LoadSFSFile(saveName, SaveFolder);
                Game game = node == null ? null : GamePersistence.LoadGameCfg(node, saveName, true, false);
                if (game?.flightState == null) { Fail("step " + step, $"could not load saves/{SaveFolder}/{saveName}.sfs"); failed = true; yield break; }
                if (asQuickload)
                {
                    // As QuickSaveLoad.onQuickloadPipelineFinished.
                    GamePersistence.UpdateScenarioModules(game);
                    GameEvents.onGameStatePostLoad.Fire(node);
                    Log($"step {step}: loading '{saveName}' (quickload, active ship {game.flightState.protoVessels[game.flightState.activeVesselIdx].vesselName})");
                    FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
                }
                else
                {
                    HighLogic.CurrentGame = game;
                    GamePersistence.UpdateScenarioModules(game);
                    GameEvents.onGameStatePostLoad.Fire(node);
                    HighLogic.SaveFolder = SaveFolder;
                    int index = game.flightState.protoVessels.FindIndex(p => p.vesselName == shipName);
                    Log($"step {step}: flying '{shipName}' from '{saveName}' (vessel index {index})");
                    FlightDriver.StartAndFocusVessel(saveName, index);
                }
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                failed = true;
                yield break;
            }

            yield return WaitForShip(shipName, outcome, old);
        }

        // Wait for flight on the ship, in the new scene: not the ship object the switch or load started from (after a
        // quickload the old scene's ship has the same name).
        private IEnumerator WaitForShip(string shipName, Flown outcome, Vessel old)
        {
            yield return new WaitForSecondsRealtime(2f);
            float deadline = Time.realtimeSinceStartup + 300f;
            while (true)
            {
                if (Time.realtimeSinceStartup > deadline) { Fail("step " + step, "timed out waiting for flight on " + shipName); failed = true; yield break; }
                Vessel active = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (active != null && !ReferenceEquals(active, old) && active.vesselName == shipName && !active.packed)
                {
                    MechJebCore core = active.GetMasterMechJeb();
                    MechJebModuleFlipAndBurn module = core?.GetComputerModule<MechJebModuleFlipAndBurn>();
                    if (module != null)
                    {
                        outcome.Core = core;
                        outcome.Module = module;
                        outcome.Ship = active;
                        outcome.Ok = true;
                        break;
                    }
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            HighLogic.SaveFolder = SaveFolder;
            Log($"step {step}: in flight on '{shipName}', {outcome.Ship.situation} {outcome.Ship.mainBody.bodyName}, UT {Planetarium.GetUniversalTime():F1}; " +
                $"trip loaded with it: {(outcome.Module.Autopilot.Record != null ? "yes" : "no")}");
            step++;
        }

        private static void OpenWindow(Flown f)
        {
            var menu = f.Core.GetComputerModule<MechJebModuleMenu>();
            if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();
            float scale = GuiUtils.Scale > 0 ? GuiUtils.Scale : 1f;
            f.Module.WindowVector = new Vector4(Screen.width / scale * 0.38f, Screen.height / scale * 0.18f, 0, 0);
            f.Module.Enabled = true;
        }

        /// <summary>
        /// A paused trip on the ship just flown to: it must resume on its own, hold the engines at zero until it does, and
        /// not fire or warp before the ship points where it should. Logs the re-plan and screenshots the window and message.
        /// </summary>
        // Turn the ship about an axis across it, as if the player had turned it while the trip was paused.
        private static void TurnAway(Vessel v, float degrees)
        {
            Quaternion now = v.transform.rotation;
            Vector3 across = v.transform.right;
            v.SetRotation(Quaternion.AngleAxis(degrees, across) * now);
            Log($"turned '{v.vesselName}' {degrees} degrees away from where it pointed");
        }

        private IEnumerator WatchResume(Flown f, string when, string shot, bool alreadyRunning = false)
        {
            MechJebModuleFlipAndBurnAutopilot pilot = f.Module.Autopilot;
            Vessel v = f.Ship;
            float t0 = Time.realtimeSinceStartup;
            float maxWaitingThrottle = 0;
            while (!pilot.Running && Time.realtimeSinceStartup - t0 < 60)
            {
                if (pilot.StopReason != null && pilot.Record == null) break;   // refused
                maxWaitingThrottle = Math.Max(maxWaitingThrottle, Math.Max(v.ctrlState.mainThrottle, EngineThrottle(v)));
                yield return new WaitForFixedUpdate();
            }

            if (!pilot.Running)
            {
                Fail("step " + step, $"the trip didn't resume {when}: {pilot.StopReason ?? "(no reason)"}");
                failed = true;
                yield break;
            }

            // The resume message comes on the trip's first guidance step, with the arrival as the guidance has it.
            float t1 = Time.realtimeSinceStartup;
            while (pilot.Running && double.IsNaN(pilot.LastResumeNewArrivalUT) && Time.realtimeSinceStartup - t1 < 10)
                yield return new WaitForFixedUpdate();
            f.Module.Enabled = true;
            Log($"step {step}: resumed {when}: {(alreadyRunning ? "with Resume" : $"{Time.realtimeSinceStartup - t0:F1} s real after the ship was ready")}; away {pilot.LastAwaySeconds:F1} s; " +
                $"arrival UT {pilot.LastResumeOldArrivalUT:F0} -> {pilot.LastResumeNewArrivalUT:F0} ({pilot.LastResumeNewArrivalUT - pilot.LastResumeOldArrivalUT:+0;-0} s); " +
                $"highest throttle while waiting {maxWaitingThrottle:F3}; notice: {pilot.Notice}");
            LogState("just resumed " + when, f);
            if (shot != null) yield return Shot(shot, f);

            // Pointing before thrust: watch until the engine fires (or a minute of game time if no thrust is wanted).
            double firstThrustPointing = double.NaN, maxOffThrottle = 0, ut0 = Planetarium.GetUniversalTime();
            float maxWarpUnpointed = 1;
            double maxPointing = 0;
            while (pilot.Running && Planetarium.GetUniversalTime() - ut0 < 600 && Time.realtimeSinceStartup - t0 < 240)
            {
                float throttle = EngineThrottle(v);
                maxPointing = Math.Max(maxPointing, pilot.PointingError);
                if (pilot.PointingError > MechJebModuleFlipAndBurnAutopilot.PointingTolerance)
                {
                    maxOffThrottle = Math.Max(maxOffThrottle, throttle);
                    maxWarpUnpointed = Math.Max(maxWarpUnpointed, TimeWarp.CurrentRate);
                }

                if (throttle > 0.01 && double.IsNaN(firstThrustPointing))
                {
                    firstThrustPointing = pilot.PointingError;
                    break;
                }

                yield return new WaitForFixedUpdate();
            }

            bool ok = !(maxOffThrottle > 0.001) && !(maxWarpUnpointed > 1.01f) && (double.IsNaN(firstThrustPointing) || firstThrustPointing <= MechJebModuleFlipAndBurnAutopilot.PointingTolerance);
            Log($"step {step}: pointing before thrust {when}: largest pointing error {maxPointing:F2} deg, first thrust at {firstThrustPointing:F2} deg " +
                $"(limit {MechJebModuleFlipAndBurnAutopilot.PointingTolerance} deg); highest throttle while more than {MechJebModuleFlipAndBurnAutopilot.PointingTolerance} deg off " +
                $"{maxOffThrottle:F3}; highest warp then x{maxWarpUnpointed:0.#}: {(ok ? "ok" : "NOT ok")}");
            step++;
            if (!ok) { Fail("step " + step, "the resumed trip fired or warped before pointing"); failed = true; }
        }

        private static string playerGlobalLimit = "";

        /// <summary>MechJeb's acceleration limit as its global settings file has it now.</summary>
        private static string GlobalLimit()
        {
            try
            {
                string file = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "MechJeb2", "Plugins", "PluginData", "MechJeb2", "mechjeb_settings_global.cfg");
                ConfigNode root = ConfigNode.Load(file);
                ConfigNode thrust = root?.GetNode("MechJebModuleThrustController");
                if (thrust == null) return "no thrust controller node";
                // MechJeb writes these names in lower camel case; the limit is a node of its own (read from the text).
                string text = File.ReadAllText(file);
                int at = text.IndexOf("MechJebModuleThrustController", StringComparison.Ordinal);
                System.Text.RegularExpressions.Match m = at < 0 ? null
                    : System.Text.RegularExpressions.Regex.Match(text.Substring(at), @"maxAcceleration\s*\{\s*(?:_val|ValConfig)\s*=\s*([^\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string max = m != null && m.Success ? m.Groups[1].Value.Trim() : "not found";
                return $"LimitAcceleration {thrust.GetValue("limitAcceleration") ?? thrust.GetValue("LimitAcceleration")}, MaxAcceleration {max}";
            }
            catch (Exception e)
            {
                return "unreadable: " + e.Message;
            }
        }

        /// <summary>Rates, MechJeb's controller users, the trip's state and MechJeb's saved limit, at one step.</summary>
        private void LogState(string when, Flown f)
        {
            MechJebModuleFlipAndBurnAutopilot pilot = f.Module.Autopilot;
            TripRecord r = pilot.Record;
            if (when == "before Start") playerGlobalLimit = GlobalLimit();
            Log($"state [{when}]: physics rates {Rates(TimeWarp.fetch?.physicsWarpRates)} " +
                $"({(ReferenceEquals(TimeWarp.fetch?.physicsWarpRates, ratesBeforeStart?.Array) && ratesBeforeStart.Matches(TimeWarp.fetch.physicsWarpRates) ? "the player's" : SameValues(TimeWarp.fetch?.physicsWarpRates) ? "the player's values" : "the trip's or other")}), " +
                $"warp x{TimeWarp.CurrentRate:0.#}; thrust users {Users(f.Core.Thrust)}, attitude users {Users(f.Core.Attitude)}; " +
                $"running {pilot.Running}, paused {pilot.Paused}{(pilot.PausedByPlayer ? " (Pause button)" : "")}; " +
                (r == null ? "no trip saved with the ship" : $"trip: {r.Trip} to {r.Target} at {r.Acceleration / 9.80665:F2} g, delta-v used {r.DeltaVUsed:F0} m/s, last flown UT {r.LastFlownUT:F1}") +
                $"; throttle {f.Ship.ctrlState.mainThrottle:F3}, engine throttle {EngineThrottle(f.Ship):F3}; MechJeb settings file {GlobalLimit()}; " +
                $"KSP allows saving: {FlightGlobals.ClearToSave(false)}");
        }

        /// <summary>Is a trip saved with <paramref name="shipName"/> in the save file? Logs its values.</summary>
        private bool LogTripInSave(string saveName, string shipName, string when)
        {
            try
            {
                string file = Path.Combine(KSPUtil.ApplicationRootPath, "saves", SaveFolder, saveName + ".sfs");
                ConfigNode root = ConfigNode.Load(file);
                ConfigNode game = root?.GetNode("GAME"), flight = game?.GetNode("FLIGHTSTATE");
                ConfigNode vessel = flight?.GetNodes("VESSEL").FirstOrDefault(n => n.GetValue("name") == shipName);
                ConfigNode trip = vessel == null ? null : Find(vessel, MechJebModuleFlipAndBurnAutopilot.TripNodeName);
                Log($"saved trip [{when}]: {saveName}.sfs, '{shipName}': " + (vessel == null ? "ship not in the save" : trip == null ? "no trip" :
                    string.Join(", ", trip.values.Cast<ConfigNode.Value>().Select(x => x.name + " " + x.value))));
                return trip != null;
            }
            catch (Exception e)
            {
                Log($"saved trip [{when}]: couldn't read {saveName}.sfs: {e.Message}");
                return false;
            }
        }

        private static ConfigNode Find(ConfigNode n, string name)
        {
            foreach (ConfigNode c in n.nodes)
            {
                if (c.name == name) return c;
                ConfigNode d = Find(c, name);
                if (d != null) return d;
            }

            return null;
        }
    }
}
