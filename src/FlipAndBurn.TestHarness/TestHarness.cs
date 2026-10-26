using System;
using System.Collections;
using System.IO;
using System.Linq;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Dev-only. Drives KSP from the main menu into flight on the test ship, opens the Flip and Burn window and takes a
    // screenshot, logging each step with a "[FlipAndBurn.Test]" prefix, then quits KSP.
    //
    // It does nothing at all unless KSP was started with -flipandburn-test.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class TestHarness : MonoBehaviour
    {
        public const string Flag = "-flipandburn-test";

        private const string SaveFolder = "FlipAndBurn-Test";
        private const string SaveFile = "persistent";
        private const string ShipName = "Bebop Refueller";
        private const string ScreenshotFolder = "FlipAndBurnTest";
        private const string ScreenshotFile = "flip-and-burn-window.png";
        private const float SettleSeconds = 5f;

        private static void Log(string message)
        {
            Debug.Log("[FlipAndBurn.Test] " + message);
        }

        private void Awake()
        {
            if (!Environment.GetCommandLineArgs().Contains(Flag))
            {
                // Normal play: remove ourselves without a trace.
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            Log("step 1: harness active (" + Flag + " given)");

            // Let the main menu finish building before switching scenes.
            yield return new WaitForSecondsRealtime(3f);

            try
            {
                // Same order as KSP's own MainMenu.OnLoadDialogPipelineFinished: the flight scene's Game.Load() reads
                // HighLogic.CurrentGame, so it must be set before switching scenes.
                ConfigNode node = GamePersistence.LoadSFSFile(SaveFile, SaveFolder);
                Game game = node == null ? null : GamePersistence.LoadGameCfg(node, SaveFolder, true, false);
                if (game == null || game.flightState == null)
                {
                    Fail("step 2", "could not load saves/" + SaveFolder + "/" + SaveFile + ".sfs");
                    yield break;
                }

                HighLogic.CurrentGame = game;
                GamePersistence.UpdateScenarioModules(game);
                GameEvents.onGameStatePostLoad.Fire(node);
                HighLogic.SaveFolder = SaveFolder;

                int shipIndex = game.flightState.protoVessels.FindIndex(v => v.vesselName == ShipName);
                Log("step 2: loaded save '" + SaveFolder + "/" + SaveFile + "' (" + game.flightState.protoVessels.Count +
                    " vessels, mode " + game.Mode + ")");
                if (shipIndex < 0)
                {
                    Fail("step 3", "ship '" + ShipName + "' not found in the save");
                    yield break;
                }

                // Same call the Tracking Station's "Fly" button makes.
                Log("step 3: switching to flight on '" + ShipName + "' (vessel index " + shipIndex + ")");
                FlightDriver.StartAndFocusVessel(SaveFile, shipIndex);
            }
            catch (Exception e)
            {
                Fail("step 2-3", e.ToString());
                yield break;
            }

            // Wait for the flight scene, the ship and its MechJeb.
            MechJebCore core = null;
            float deadline = Time.realtimeSinceStartup + 300f;
            while (true)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail("step 4", "timed out waiting for flight scene with MechJeb");
                    yield break;
                }

                Vessel active = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (active != null && active.vesselName == ShipName && !active.packed)
                {
                    core = active.GetMasterMechJeb();
                    if (core != null && core.GetComputerModule<MechJebModuleFlipAndBurn>() != null) break;
                }

                yield return new WaitForSecondsRealtime(0.5f);
            }

            Vessel ship = FlightGlobals.ActiveVessel;
            Log("step 4: in flight on '" + ship.vesselName + "', " + ship.situation + " " + ship.mainBody.bodyName +
                ", MechJeb core on part '" + core.part.partInfo.name + "'");

            try
            {
                var module = core.GetComputerModule<MechJebModuleFlipAndBurn>();
                bool listed = core.GetDisplayModules(MechJebModuleMenu.DisplayOrder.instance)
                    .Any(m => m == module && !m.Hidden && m.ShowInCurrentScene);
                Log("step 5: MechJeb menu lists '" + module.GetName() + "': " + listed + " (Hidden=" + module.Hidden +
                    ", ShowInCurrentScene=" + module.ShowInCurrentScene + ")");
                if (!listed)
                {
                    Fail("step 5", "Flip and Burn is not listed in MechJeb's menu");
                    yield break;
                }

                // Open MechJeb's main menu too, so the screenshot shows the entry next to the window.
                var menu = core.GetComputerModule<MechJebModuleMenu>();
                if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();

                // MechJeb opens every window at the same top-left spot, so other windows the player left open would sit
                // on top of ours. Put ours in an empty area for the screenshot (MechJeb's settings are restored after
                // the run). Coordinates are in MechJeb's scaled GUI units.
                float scale = GuiUtils.Scale > 0 ? GuiUtils.Scale : 1f;
                module.WindowVector = new Vector4(Screen.width / scale * 0.40f, Screen.height / scale * 0.30f, 0, 0);

                module.Enabled = true;
                Log("step 6: opened the '" + module.GetName() + "' window (Enabled=" + module.Enabled + ", at " +
                    module.WindowVector.x.ToString("0") + "," + module.WindowVector.y.ToString("0") + ")");
            }
            catch (Exception e)
            {
                Fail("step 5-6", e.ToString());
                yield break;
            }

            yield return new WaitForSecondsRealtime(SettleSeconds);

            string folder = Path.Combine(KSPUtil.ApplicationRootPath, ScreenshotFolder);
            string path = Path.GetFullPath(Path.Combine(folder, ScreenshotFile));
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Fail("step 7", e.ToString());
                yield break;
            }

            // The screenshot must include MechJeb's IMGUI windows, which are drawn straight to the screen after the
            // cameras render, so it has to come from the finished frame. Reading the frame back can come out blank on
            // some setups (an earlier run read all zeros with ScreenCapture and with ReadPixels on OpenGLCore + 8x
            // MSAA), so try several ways and keep the first that is not blank. A blank result fails the run.
            Log("step 7: capturing (" + Screen.width + "x" + Screen.height + ", focused=" + Application.isFocused +
                ", mode=" + Screen.fullScreenMode + ", gfx=" + SystemInfo.graphicsDeviceType + ", msaa=" +
                QualitySettings.antiAliasing + ")");
            for (int round = 1; round <= CaptureRounds; round++)
            {
                foreach (string method in new[] { "readpixels", "screen-to-rendertexture", "readpixels-without-msaa" })
                {
                    int savedMsaa = QualitySettings.antiAliasing;
                    if (method == "readpixels-without-msaa")
                    {
                        QualitySettings.antiAliasing = 0;
                        for (int i = 0; i < 5; i++) yield return null;
                    }

                    yield return new WaitForEndOfFrame();

                    Texture2D tex = null;
                    float brightness;
                    try
                    {
                        tex = method == "screen-to-rendertexture" ? CaptureViaRenderTexture() : CaptureViaReadPixels();
                        brightness = Brightness(tex);
                        if (brightness >= MinBrightness) File.WriteAllBytes(path, tex.EncodeToPNG());
                    }
                    catch (Exception e)
                    {
                        Log("step 7: round " + round + " " + method + " threw " + e.GetType().Name + ": " + e.Message);
                        brightness = -1f;
                    }
                    finally
                    {
                        if (tex != null) Destroy(tex);
                        QualitySettings.antiAliasing = savedMsaa;
                    }

                    Log("step 7: round " + round + " " + method + ": mean brightness " + brightness.ToString("0.00") + "/255");
                    if (brightness >= MinBrightness)
                    {
                        Log("step 7: screenshot saved to " + path + " (" + new FileInfo(path).Length + " bytes, method " +
                            method + ")");
                        Log("step 8: done, quitting KSP");
                        Application.Quit();
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(CaptureRetrySeconds);
            }

            // In-game capture came out blank. Ask build.sh to grab the KSP window from outside (ffmpeg x11grab), which
            // sees exactly what is on screen, then check that image too.
            Log("step 7: in-game capture blank; external capture requested");
            float deadline7 = Time.realtimeSinceStartup + ExternalCaptureTimeout;
            while (Time.realtimeSinceStartup < deadline7)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!File.Exists(path)) continue;

                float brightness = -1f;
                var tex = new Texture2D(2, 2);
                try
                {
                    if (tex.LoadImage(File.ReadAllBytes(path))) brightness = Brightness(tex);
                }
                catch (Exception e)
                {
                    Log("step 7: could not read external capture yet: " + e.Message);
                }
                finally
                {
                    Destroy(tex);
                }

                Log("step 7: external capture: mean brightness " + brightness.ToString("0.00") + "/255");
                if (brightness >= MinBrightness)
                {
                    Log("step 7: screenshot saved to " + path + " (" + new FileInfo(path).Length +
                        " bytes, method external-x11grab)");
                    Log("step 8: done, quitting KSP");
                    Application.Quit();
                    yield break;
                }

                break;
            }

            Fail("step 7", "every capture was blank (mean brightness below " + MinBrightness + "/255)");
        }

        private const float ExternalCaptureTimeout = 60f;
        private const int CaptureRounds = 1;
        private const float CaptureRetrySeconds = 5f;
        private const float MinBrightness = 2f;

        // Both must run after WaitForEndOfFrame.
        private static Texture2D CaptureViaReadPixels()
        {
            var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            tex.Apply();
            return tex;
        }

        private static Texture2D CaptureViaRenderTexture()
        {
            var rt = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = previous;
                rt.Release();
                Destroy(rt);
            }
        }

        // Mean of R, G and B over a sample of pixels, 0-255.
        private static float Brightness(Texture2D tex)
        {
            Color32[] pixels = tex.GetPixels32();
            long sum = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += 101)
            {
                sum += pixels[i].r + pixels[i].g + pixels[i].b;
                count++;
            }

            return count == 0 ? 0f : sum / (3f * count);
        }

        private static void Fail(string step, string why)
        {
            Debug.LogError("[FlipAndBurn.Test] FAILED at " + step + ": " + why);
            Log("step 8: done (failed), quitting KSP");
            Application.Quit();
        }
    }
}
