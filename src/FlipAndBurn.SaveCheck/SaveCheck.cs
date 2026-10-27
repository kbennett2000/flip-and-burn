using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.SaveCheck
{
    /// <summary>
    /// With Flip and Burn removed from GameData: load the test copy's save made mid-trip, fly the ship, save again, and
    /// check that nothing broke (ADR 0004). Logs with the prefix [FlipAndBurn.Test], like the main test helper.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class SaveCheck : MonoBehaviour
    {
        private const string Flag = "-flipandburn-test";
        private const string SaveFolder = "FlipAndBurn-Test";
        private const string MidTripSave = "fab-midtrip";
        private const string AfterSave = "fab-nomod";
        private const string Ship = "Bebop Refueller";
        private const string TripNode = "FLIPANDBURN_TRIP";
        private int step = 1;

        private static void Log(string message) => Debug.Log("[FlipAndBurn.Test] " + message);

        private static void Fail(string step, string why)
        {
            Debug.LogError("[FlipAndBurn.Test] FAILED at " + step + ": " + why);
            Log("step 99: done (failed), quitting KSP");
            Application.Quit();
        }

        private void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains(Flag))
            {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);
        }

        private void Start() => StartCoroutine(Run());

        private static string SavePath(string name) => Path.Combine(KSPUtil.ApplicationRootPath, "saves", SaveFolder, name + ".sfs");

        private static string Sha(string file)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file))).Replace("-", "").Substring(0, 16);
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

        private static ConfigNode ShipNode(string save)
        {
            ConfigNode root = ConfigNode.Load(SavePath(save));
            return root?.GetNode("GAME")?.GetNode("FLIGHTSTATE")?.GetNodes("VESSEL").FirstOrDefault(n => n.GetValue("name") == Ship);
        }

        private IEnumerator Run()
        {
            yield return new WaitForSecondsRealtime(3f);
            bool installed = AssemblyLoader.loadedAssemblies.Any(a => a.name == "FlipAndBurn" || a.name == "FlipAndBurn.Core");
            Log($"step {step}: save check active, Flip and Burn installed: {installed} (must be False)");
            if (installed) { Fail("step " + step, "Flip and Burn is still installed"); yield break; }
            step++;

            string file = SavePath(MidTripSave);
            if (!File.Exists(file)) { Fail("step " + step, "no save " + file); yield break; }
            string before = Sha(file);
            ConfigNode saved = ShipNode(MidTripSave);
            ConfigNode trip = saved == null ? null : Find(saved, TripNode);
            int partsSaved = saved?.GetNodes("PART").Length ?? 0;
            Log($"step {step}: '{MidTripSave}.sfs' (sha {before}): '{Ship}' has {partsSaved} parts and a saved trip: {trip != null}");
            if (trip == null) { Fail("step " + step, "the mid-trip save has no trip to test with"); yield break; }
            step++;

            // Resume the test save from the main menu as KSP does (loading a named save straight from the main menu froze
            // KSP), then load the save made mid-trip as F9 does.
            try
            {
                ConfigNode persistent = GamePersistence.LoadSFSFile("persistent", SaveFolder);
                HighLogic.CurrentGame = GamePersistence.LoadGameCfg(persistent, SaveFolder, true, false);
                GamePersistence.UpdateScenarioModules(HighLogic.CurrentGame);
                GamePersistence.SaveGame(HighLogic.CurrentGame, "persistent", SaveFolder, SaveMode.OVERWRITE);
                GameEvents.onGameStatePostLoad.Fire(persistent);
                HighLogic.SaveFolder = SaveFolder;
                Log($"step {step}: resuming the test save from the main menu (it starts in {HighLogic.CurrentGame.startScene})");
                HighLogic.CurrentGame.Start();
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                yield break;
            }

            float until = Time.realtimeSinceStartup + 300;
            while (Time.realtimeSinceStartup < until &&
                   !(HighLogic.LoadedScene == GameScenes.SPACECENTER || (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed)))
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);
            Log($"step {step}: resumed into {HighLogic.LoadedScene}");
            step++;
            Vessel resumedOn = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;

            try
            {
                ConfigNode node = GamePersistence.LoadSFSFile(MidTripSave, SaveFolder);
                Game game = GamePersistence.LoadGameCfg(node, MidTripSave, true, false);
                if (game?.flightState == null) { Fail("step " + step, "KSP couldn't load the save"); yield break; }
                HighLogic.SaveFolder = SaveFolder;
                GamePersistence.UpdateScenarioModules(game);
                GameEvents.onGameStatePostLoad.Fire(node);
                Log($"step {step}: loading '{MidTripSave}' without Flip and Burn");
                FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + 300;
            Vessel v = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                v = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (v != null && !ReferenceEquals(v, resumedOn) && v.vesselName == Ship && !v.packed) break;
                v = null;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            if (v == null) { Fail("step " + step, "never got into flight on " + Ship); yield break; }
            step++;
            yield return new WaitForSecondsRealtime(10f);
            MechJebCore core = v.GetMasterMechJeb();
            int ours = core == null ? -1 : core.GetComputerModules<ComputerModule>().Count(m => m.GetType().Name.Contains("FlipAndBurn"));
            Log($"step {step}: in flight on '{v.vesselName}', {v.situation} {v.mainBody.bodyName}, {v.parts.Count} parts (saved {partsSaved}), " +
                $"MechJeb core {(core != null ? "present" : "MISSING")}, Flip and Burn modules in MechJeb {ours}, throttle {v.ctrlState.mainThrottle:F3}, " +
                $"acceleration {v.geeForce:F3} g, state {v.state}");
            bool shipOk = v.parts.Count == partsSaved && core != null && ours == 0 && v.state != Vessel.State.DEAD;
            if (!shipOk) { Fail("step " + step, "the ship didn't load as saved"); yield break; }
            step++;

            // Save again (as F5 does, under the test's own name): MechJeb writes its node without the trip.
            if (FlightGlobals.ClearToSave(false) != ClearToSaveStatus.CLEAR) { Fail("step " + step, "KSP won't save: " + FlightGlobals.ClearToSave(false)); yield break; }
            Game g = HighLogic.CurrentGame.Updated();
            g.startScene = GameScenes.FLIGHT;
            GamePersistence.SaveGame(g, AfterSave, SaveFolder, SaveMode.OVERWRITE);
            yield return new WaitForSecondsRealtime(1f);
            ConfigNode after = ShipNode(AfterSave);
            ConfigNode mj = after == null ? null : Find(after, "MechJebLocalSettings");
            bool tripLeft = after != null && Find(after, TripNode) != null;
            string afterSha = Sha(file);
            Log($"step {step}: saved again as '{AfterSave}.sfs': '{Ship}' {(after != null ? "present" : "MISSING")} with {after?.GetNodes("PART").Length ?? 0} parts, " +
                $"MechJeb's settings {(mj != null ? "present" : "MISSING")}, trip data {(tripLeft ? "still there" : "dropped by MechJeb (Flip and Burn isn't there to keep it)")}; " +
                $"'{MidTripSave}.sfs' unchanged: {afterSha == before} (sha {afterSha})");
            if (after == null || mj == null || afterSha != before) { Fail("step " + step, "the save didn't come through"); yield break; }
            step++;
            Log($"step {step}: done, quitting KSP");
            Application.Quit();
        }
    }
}
