using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Dev-only. Drives KSP from the main menu into flight on the test ship, logs what every delta-v source says about
    // it, then plans trips in the Flip and Burn window and screenshots each one, logging each step with a
    // "[FlipAndBurn.Test]" prefix, then quits KSP.
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
        private const string PlannerTarget = "Duna";

        // The three planner screenshots: (file, trip type, infinite propellant on).
        private static readonly (string File, TripType Trip, bool Infinite)[] Scenarios =
        {
            ("planner-duna-full-burn.png", TripType.FullBurn, false),
            ("planner-duna-coast.png", TripType.CoastInTheMiddle, false),
            ("planner-duna-infinite-propellant.png", TripType.FullBurn, true),
        };
        private const float SettleSeconds = 4f;

        private const float MinBrightness = 2f;
        private const float ExternalCaptureTimeout = 60f;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static void Log(string message) => Debug.Log("[FlipAndBurn.Test] " + message);

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

        private void Start() => StartCoroutine(Run());

        private IEnumerator Run()
        {
            Log("step 1: harness active (" + Flag + " given)");
            yield return new WaitForSecondsRealtime(3f);

            // ---- load the test save and fly the test ship
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
            var module = core.GetComputerModule<MechJebModuleFlipAndBurn>();
            Log("step 4: in flight on '" + ship.vesselName + "', " + ship.situation + " " + ship.mainBody.bodyName +
                ", MechJeb core on part '" + core.part.partInfo.name + "'");

            bool listed = core.GetDisplayModules(MechJebModuleMenu.DisplayOrder.instance)
                .Any(m => m == module && !m.Hidden && m.ShowInCurrentScene);
            Log("step 5: MechJeb menu lists '" + module.GetName() + "': " + listed);
            if (!listed)
            {
                Fail("step 5", "Flip and Burn is not listed in MechJeb's menu");
                yield break;
            }

            // ---- what each delta-v source says about the ship
            yield return Diagnostics(ship, core, module);

            // ---- open the planner
            try
            {
                var menu = core.GetComputerModule<MechJebModuleMenu>();
                if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();

                // MechJeb opens every window at the same top-left spot; put ours in an empty area for the screenshot.
                float scale = GuiUtils.Scale > 0 ? GuiUtils.Scale : 1f;
                module.WindowVector = new Vector4(Screen.width / scale * 0.38f, Screen.height / scale * 0.18f, 0, 0);
                module.Enabled = true;
                Log("step 7: opened the '" + module.GetName() + "' window");
            }
            catch (Exception e)
            {
                Fail("step 7", e.ToString());
                yield break;
            }

            int step = 8;
            Log("step " + step + ": infinite propellant cheat before the scenarios: " + CheatOptions.InfinitePropellant);
            if (!module.SelectTarget(PlannerTarget))
            {
                Fail("step " + step, "could not select " + PlannerTarget);
                yield break;
            }

            Log("step " + step + ": selected " + PlannerTarget);
            step++;
            foreach ((string file, TripType trip, bool infinite) in Scenarios)
            {
                // Only this harness turns the cheat on, only here, and turns it off again straight after the screenshot.
                // KSP keeps it in memory only (CheatOptions); it is not written to settings.cfg or to any save.
                CheatOptions.InfinitePropellant = infinite;
                module.SelectTripType(trip);
                Log("step " + step + ": trip type " + trip + ", infinite propellant " + CheatOptions.InfinitePropellant);
                yield return new WaitForSecondsRealtime(SettleSeconds);

                var result = new CaptureResult();
                int shotStep = step;
                yield return Capture(step, file, result, () => LogPlanner(shotStep, module, ship));
                CheatOptions.InfinitePropellant = false;
                if (!result.Ok)
                {
                    Fail("step " + step, result.Error);
                    yield break;
                }

                step++;
            }

            module.SelectTripType(TripType.FullBurn);
            Log("step " + step + ": infinite propellant cheat after the scenarios: " + CheatOptions.InfinitePropellant);
            step++;

            Log("step " + step + ": done, quitting KSP");
            Application.Quit();
        }

        // ------------------------------------------------------------------ diagnostics

        private static IEnumerator Diagnostics(Vessel ship, MechJebCore core, MechJebModuleFlipAndBurn module)
        {
            // Let KSPIE's engine controller run some physics ticks: it rewrites the engine's thrust and Isp each tick.
            yield return new WaitForSecondsRealtime(3f);
            Log("step 6: delta-v sources for '" + ship.vesselName + "' at UT " + F(Planetarium.GetUniversalTime()));

            try
            {
                Log("diag mass: vessel.totalMass = " + F(ship.totalMass) + " t");
                var totals = new Dictionary<string, double>();
                foreach (Part p in ship.parts)
                foreach (PartResource r in p.Resources)
                    totals[r.resourceName] = (totals.TryGetValue(r.resourceName, out double a) ? a : 0) + r.amount;
                foreach (KeyValuePair<string, double> kv in totals.OrderBy(k => k.Key))
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(kv.Key);
                    Log("diag resource: " + kv.Key + " = " + F(kv.Value) + " units, density " + F(def?.density ?? 0) +
                        " t/unit, " + F(kv.Value * (def?.density ?? 0)) + " t");
                }

                foreach (ModuleEngines e in ship.FindPartModulesImplementing<ModuleEngines>())
                {
                    Log("diag engine: part=" + e.part.partInfo.name + " module=" + e.GetType().Name + " ignited=" + e.EngineIgnited +
                        " operational=" + e.isOperational + " flameout=" + e.flameout + " maxThrust=" + F(e.maxThrust) + " kN" +
                        " thrustPercentage=" + F(e.thrustPercentage) + " vacuumIsp=" + F(e.atmosphereCurve.Evaluate(0f)) + " s" +
                        " maxFuelFlow=" + F(e.maxFuelFlow) + " includeinDVCalcs=" + e.includeinDVCalcs +
                        " propellants=" + string.Join(",", e.propellants.Select(p => p.name + ":" + F(p.ratio))));
                }

                foreach (Part p in ship.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (!IsKspieFusion(m.GetType())) continue;
                    var parts = new List<string>();
                    foreach (string name in new[] { "thrustMultiplier", "ispMultiplier", "partMass", "massThrustExp", "massIspExp",
                                 "ispThrottleExponent", "maxThrustMk7", "thrustIspMk7", "timeDilation", "effectiveMaxThrustInKiloNewton",
                                 "maximizeThrust", "fuelLimit", "speedLimit" })
                        parts.Add(name + "=" + Member(m, name));
                    foreach (string name in new[] { "EngineGenerationType", "EngineIsp", "MaximumThrust", "RawEngineIsp" })
                        parts.Add(name + "=" + Member(m, name));
                    Log("diag KSPIE " + m.GetType().Name + " on " + p.partInfo.name + ": part.mass=" + F(p.mass) + " " + string.Join(" ", parts));
                }
            }
            catch (Exception e)
            {
                Log("diag raw failed: " + e);
            }

            // Stock delta-v (computed in the background; wait for it).
            VesselDeltaV dv = ship.VesselDeltaV;
            float until = Time.realtimeSinceStartup + 15f;
            if (dv != null) dv.SetCalcsDirty(true);
            while (dv != null && !dv.IsReady && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(1f);
            try
            {
                if (dv == null) Log("diag stock: no VesselDeltaV");
                else
                {
                    Log("diag stock: ready=" + dv.IsReady + " TotalDeltaVVac=" + F(dv.TotalDeltaVVac) + " m/s TotalDeltaVActual=" + F(dv.TotalDeltaVActual) + " m/s");
                    foreach (DeltaVStageInfo s in dv.OperatingStageInfo)
                        Log("diag stock stage " + s.stage + ": deltaVinVac=" + F(s.deltaVinVac) + " m/s thrustVac=" + F(s.thrustVac) + " kN ispVac=" +
                            F(s.ispVac) + " s startMass=" + F(s.startMass) + " t endMass=" + F(s.endMass) + " t fuelMass=" + F(s.fuelMass) + " t");
                }
            }
            catch (Exception e)
            {
                Log("diag stock failed: " + e.Message);
            }

            // MechJeb's fuel flow simulation (also background).
            MechJebModuleStageStats stats = core.StageStats;
            until = Time.realtimeSinceStartup + 15f;
            while (stats != null && Time.realtimeSinceStartup < until)
            {
                stats.RequestUpdate();
                if (stats.VacStats.Count > 0) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            try
            {
                if (stats == null || stats.VacStats.Count == 0) Log("diag MechJeb: no stage stats");
                else
                {
                    Log("diag MechJeb: total vacuum delta-v=" + F(stats.VacStats.Sum(s => s.DeltaV)) + " m/s");
                    foreach (MechJebLib.FuelFlowSimulation.FuelStats s in stats.VacStats)
                        Log("diag MechJeb stage " + s.KSPStage + ": DeltaV=" + F(s.DeltaV) + " m/s Thrust=" + F(s.Thrust) + " kN Isp=" + F(s.Isp) +
                            " s StartMass=" + F(s.StartMass) + " t EndMass=" + F(s.EndMass) + " t");
                }
            }
            catch (Exception e)
            {
                Log("diag MechJeb failed: " + e.Message);
            }

            // Flip and Burn's own reader.
            try
            {
                module.Planner.Refresh(ship);
                ShipPerformance perf = module.Planner.Ship;
                Log("diag FlipAndBurn: FullThrust=" + F(perf.FullThrust) + " N VacuumIsp=" + F(perf.VacuumIsp) + " s IspThrottleExponent=" +
                    F(perf.IspThrottleExponent) + " StartMass=" + F(perf.StartMass) + " kg EndMass=" + F(perf.EndMass) + " kg");
                Log("diag FlipAndBurn: FullThrustDeltaV=" + F(perf.FullThrustDeltaV) + " m/s DeltaVAt1g=" + F(perf.DeltaVAtAcceleration(9.81)) +
                    " m/s MaxAcceleration=" + F(perf.MaxAcceleration) + " m/s^2");

                Orbit o = ship.orbit;
                Log("diag ship orbit: ref=" + o.referenceBody.bodyName + " sma=" + F(o.semiMajorAxis) + " ecc=" + F(o.eccentricity) + " inc=" +
                    F(o.inclination) + " LAN=" + F(o.LAN) + " argPe=" + F(o.argumentOfPeriapsis) + " meanAnomalyAtEpoch=" + F(o.meanAnomalyAtEpoch) +
                    " epoch=" + F(o.epoch));
                CelestialBody sun = Planetarium.fetch.Sun;
                foreach (CelestialBody b in FlightGlobals.Bodies.Where(x => x != sun && x.referenceBody == sun).OrderBy(x => x.orbit.semiMajorAxis))
                {
                    string name = b.bodyName;
                    Log("diag body " + name + ": Radius=" + F(b.Radius) + " gravParameter=" + F(b.gravParameter) + " atmosphere=" + b.atmosphere +
                        " atmosphereDepth=" + F(b.atmosphereDepth) + " maxTerrain=" +
                        F(b.pqsController != null ? b.pqsController.radiusMax - b.Radius : 0) + " SOI=" + F(b.sphereOfInfluence) +
                        " defaultParking=" + F(Planner.DefaultParkingAltitude(b)));
                }
            }
            catch (Exception e)
            {
                Log("diag FlipAndBurn failed: " + e);
            }
        }

        private static bool IsKspieFusion(Type t)
        {
            for (; t != null; t = t.BaseType)
                if (t.Name == "DaedalusEngineController") return true;
            return false;
        }

        private static string Member(object o, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return Fmt(f.GetValue(o));
                PropertyInfo p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null) return Fmt(p.GetValue(o, null));
            }

            return "?";
        }

        private static string Fmt(object v) => v is double d ? F(d) : v is float f ? F(f) : v?.ToString() ?? "null";

        private static string F(double v) => v.ToString("R", Inv);

        private static void LogPlanner(int step, MechJebModuleFlipAndBurn module, Vessel ship)
        {
            Planner p = module.Planner;
            Log("step " + step + ": window shows (planned at UT " + F(p.PlannedAtUT) + ", target " + p.TargetName + ", acceleration " +
                F(p.AccelerationG) + " g, flip " + p.Flip + ", parking " + F(p.Target != null ? p.ParkingAltitude(p.Target) : 0) + " m, trip " +
                p.Trip + ", infinite propellant " + p.InfinitePropellant + ")");
            if (p.SituationMessage != null) Log("  situation: " + p.SituationMessage);
            foreach (KeyValuePair<string, string> row in p.TripRows.Concat(p.ShipRows)) Log("  " + row.Key + " | " + row.Value);
            foreach (string note in p.Notes) Log("  note: " + note);
            if (p.Plan != null && p.Plan.Feasible)
                Log("  raw: TripTime=" + F(p.Plan.TripTime) + " s TimeToFlip=" + F(p.Plan.TimeToFlip) + " s PeakSpeed=" + F(p.Plan.PeakSpeed) +
                    " m/s BurnDeltaV=" + F(p.Plan.BurnDeltaV) + " ParkingOrbitDeltaV=" + F(p.Plan.ParkingOrbitDeltaV) + " TotalDeltaV=" +
                    F(p.Plan.TotalDeltaV) + " m/s ClosestSun=" + F(p.Plan.ClosestSunDistance) + " m iterations=" + p.Plan.ArrivalIterations);
            if (p.Coast != null)
                Log("  coast raw: Feasible=" + p.Coast.Feasible + " BurnUp=" + F(p.Coast.BurnUpTime) + " s Coast=" + F(p.Coast.CoastTime) +
                    " s BurnDown=" + F(p.Coast.BurnDownTime) + " s TripTime=" + F(p.Coast.TripTime) + " s DeltaV=" + F(p.Coast.DeltaV) +
                    " m/s DeltaVNeeded=" + F(p.Coast.DeltaVNeeded) + " m/s Budget=" + F(p.Coast.Budget) + " m/s");
            if (p.Plan != null && p.Plan.Feasible)
                Log("  detour: UsesSunDetour=" + p.Plan.UsesSunDetour + " StraightClosestSun=" + F(p.Plan.StraightClosestSunDistance) + " m");
            if (p.Ship != null)
                Log("  ship: DeltaVAt=" + F(p.Ship.DeltaVAtAcceleration(p.AccelerationG * Planner.OneG)) + " m/s");
            Log("  shortfall shown: " + p.HasShortfall);
            if (p.Readiness != null)
                Log("  readiness: ready=" + p.Readiness.Ready + " canHold=" + p.Readiness.CanHoldAcceleration + " enoughDeltaV=" +
                    p.Readiness.HasEnoughDeltaV + " available=" + F(p.Readiness.DeltaVAvailable) + " m/s maxAccel=" + F(p.Readiness.MaxAcceleration) + " m/s^2");
            Log("  Start button enabled: False (always, this cycle)");
        }

        // ------------------------------------------------------------------ screenshots

        private sealed class CaptureResult
        {
            public bool Ok;
            public string Error;
        }

        /// <summary>
        /// Saves the finished frame (3D view plus MechJeb's IMGUI windows) to FlipAndBurnTest/<paramref name="file"/>.
        /// <paramref name="onCaptured"/> runs in the same frame the pixels were read, so what it logs is what the image shows.
        /// Falls back to asking build.sh for an external grab when the in-game read comes back blank.
        /// </summary>
        private IEnumerator Capture(int step, string file, CaptureResult result, Action onCaptured)
        {
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, ScreenshotFolder);
            string path = Path.GetFullPath(Path.Combine(folder, file));
            Directory.CreateDirectory(folder);
            if (File.Exists(path)) File.Delete(path);

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
                float brightness = -1f;
                try
                {
                    tex = method == "screen-to-rendertexture" ? CaptureViaRenderTexture() : CaptureViaReadPixels();
                    brightness = Brightness(tex);
                    if (brightness >= MinBrightness)
                    {
                        onCaptured();
                        File.WriteAllBytes(path, tex.EncodeToPNG());
                    }
                }
                catch (Exception e)
                {
                    Log("step " + step + ": " + method + " threw " + e.GetType().Name + ": " + e.Message);
                }
                finally
                {
                    if (tex != null) Destroy(tex);
                    QualitySettings.antiAliasing = savedMsaa;
                }

                Log("step " + step + ": " + method + ": mean brightness " + brightness.ToString("0.00", Inv) + "/255");
                if (brightness >= MinBrightness)
                {
                    Log("step " + step + ": screenshot saved to " + path + " (" + new FileInfo(path).Length + " bytes, method " + method + ")");
                    result.Ok = true;
                    yield break;
                }
            }

            // Ask build.sh to grab the KSP window from outside (ffmpeg x11grab), then check that image.
            onCaptured();
            Log("step " + step + ": in-game capture blank; external capture requested: " + path);
            float deadline = Time.realtimeSinceStartup + ExternalCaptureTimeout;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (!File.Exists(path)) continue;
                var tex = new Texture2D(2, 2);
                float brightness = -1f;
                try
                {
                    if (tex.LoadImage(File.ReadAllBytes(path))) brightness = Brightness(tex);
                }
                finally
                {
                    Destroy(tex);
                }

                Log("step " + step + ": external capture: mean brightness " + brightness.ToString("0.00", Inv) + "/255");
                if (brightness >= MinBrightness)
                {
                    Log("step " + step + ": screenshot saved to " + path + " (" + new FileInfo(path).Length + " bytes, method external-x11grab)");
                    result.Ok = true;
                    yield break;
                }

                break;
            }

            result.Error = "every capture was blank (mean brightness below " + MinBrightness + "/255)";
        }

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
            Log("step 99: done (failed), quitting KSP");
            Application.Quit();
        }
    }
}
