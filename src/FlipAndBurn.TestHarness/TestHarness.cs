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
    public partial class TestHarness : MonoBehaviour
    {
        public const string Flag = "-flipandburn-test";

        private const string SaveFolder = "FlipAndBurn-Test";
        private const string SaveFile = "persistent";
        private const string ScreenshotFolder = "FlipAndBurnTest";
        private const string PlannerTarget = "Duna";


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

        private int step = 2;

        private IEnumerator Run()
        {
            Log("step 1: harness active (" + Flag + " given)");
            yield return new WaitForSecondsRealtime(3f);

            // Which proofs to run: all by default; FLIPANDBURN_PROOFS (e.g. "flip,coast") picks some, so a long run that
            // already passed one part needn't repeat it.
            string wanted = Environment.GetEnvironmentVariable("FLIPANDBURN_PROOFS");
            bool Want(string name) => string.IsNullOrEmpty(wanted) ? name != "cuttest" : wanted.Split(',').Contains(name);
            Log("proofs: " + (string.IsNullOrEmpty(wanted) ? "all" : wanted));
            string save = SaveFile;

            // KSP quit and launched again during the resume proof (build.sh): load the save made mid-trip.
            if (Environment.GetEnvironmentVariable("FLIPANDBURN_PHASE") == "relaunch")
            {
                yield return ProofResumeAfterRelaunch();
                if (failed) yield break;
                Log("step " + step + ": done, quitting KSP");
                Application.Quit();
                yield break;
            }

            // Diagnostic: does the engine cut at Abort with the temperature cheat on / after 25x warp / with neither?
            if (Want("cuttest"))
            {
                foreach ((string label, bool cheat, float warp) in new[] { ("A", true, 1f), ("B", true, 25f), ("C", false, 1f) })
                {
                    var flown = new Flown();
                    yield return PlanOnShip("Bebop Refueller", null, null, flown, save);
                    if (!flown.Ok) yield break;
                    yield return CutTest(flown, label, cheat, warp);
                    save = "quicksave";
                }
            }

            // Warp rates and throttle users: Start, a minute of burn, Abort; then leave the flight scene mid-trip; then fly again
            // and read the rates in the new flight scene.
            if (Want("rates"))
            {
                var flown = new Flown();
                yield return PlanOnShip("Bebop Refueller", null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofRates(flown, ok);
                if (!ok.Ok) yield break;   // ProofRates said why
                var again = new Flown();
                yield return PlanOnShip("Bebop Refueller", null, null, again, save);
                if (!again.Ok) yield break;
                Log($"step {step}: back in flight: TimeWarp object {TimeWarp.fetch.GetInstanceID()}, physics rates {Rates(TimeWarp.fetch.physicsWarpRates)}, " +
                    $"values {(SameValues(TimeWarp.fetch.physicsWarpRates) ? "same as" : "DIFFERENT from")} before the first Start {ratesBeforeStart}; " +
                    $"thrust users {Users(again.Core.Thrust)}, attitude users {Users(again.Core.Attitude)}");
                step++;
                save = "quicksave";
            }

            // (a) Overheating: 1 g with infinite propellant only; the autopilot steps down, re-plans and says so; Abort.
            if (Want("overheat"))
            {
                var flown = new Flown();
                yield return PlanOnShip("Bebop Refueller", null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofOverheat(flown, ok);
                if (!ok.Ok) yield break;
                save = "quicksave";   // the next proof starts from the test save's start again
            }

            // (a) The flip, from the same start: 1 g with overheating ignored too; warp probe, the flip, braking, Abort.
            if (Want("flip"))
            {
                var flown = new Flown();
                yield return PlanOnShip("Bebop Refueller", null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofFullBurn(flown, ok);
                if (!ok.Ok) yield break;
                save = "quicksave";
            }

            // (b) Coast trip to Duna without the cheats, from the same starting point: burn, coast under warp, Abort.
            if (Want("coast"))
            {
                var flown = new Flown();
                yield return PlanOnShip("Bebop Refueller", null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofCoast(flown, ok);
                if (!ok.Ok) yield break;
            }

            // Cycle 11 investigation: where propellant goes while the engine isn't firing (only when asked: "drain").
            if ((wanted ?? "").Split(',').Contains("drain"))
            {
                var flown = new Flown();
                yield return PlanOnShip(ResumeShip, null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofDrain(flown, ok);
                if (!ok.Ok || failed) yield break;
            }

            if ((wanted ?? "").Split(',').Contains("coastdrain"))
            {
                var flown = new Flown();
                yield return PlanOnShip(ResumeShip, null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofCoastDrain(flown, ok);
                if (!ok.Ok || failed) yield break;
            }

            // Cycle 12: the pictures for the player's guide (only when asked: "docshots").
            if ((wanted ?? "").Split(',').Contains("docshots"))
            {
                yield return ProofDocShots();
                if (failed) yield break;
            }

            // Cycle 13: engines that burn only electric charge (only when asked: "electric").
            if ((wanted ?? "").Split(',').Contains("electric"))
            {
                yield return ProofElectric();
                if (failed) yield break;
            }

            // Kris's Jibar Electric RCS Block (his part, not Flip and Burn's): loads, listed, fires (only when asked: "jibarrcs").
            if ((wanted ?? "").Split(',').Contains("jibarrcs"))
            {
                yield return ProofJibarRcs();
                if (failed) yield break;
            }

            // Kris's Jibar Electric RCS Block under TweakScale: 50 %, 100 %, 200 % (only when asked: "jibarscale").
            if ((wanted ?? "").Split(',').Contains("jibarscale"))
            {
                yield return ProofJibarScale();
                if (failed) yield break;
            }

            if ((wanted ?? "").Split(',').Contains("docstart"))
            {
                yield return ProofDocStart();
                if (failed) yield break;
            }


            // Cycle 11: the delta-v reader, checked against tools/verify/check_reader.py (only when asked: "readercheck").
            if ((wanted ?? "").Split(',').Contains("readercheck"))
            {
                foreach ((string ship, bool fire) in new[] { ("Bebop Refueller", true), ("CubeSat v3.2", false) })
                {
                    var flown = new Flown();
                    yield return PlanOnShip(ship, null, null, flown, save);
                    if (!flown.Ok) yield break;
                    var ok = new CaptureResult();
                    yield return ProofReaderCheck(flown, ok, fire);
                    if (!ok.Ok || failed) yield break;
                }
            }

            if ((wanted ?? "").Split(',').Contains("lossplan"))
            {
                var flown = new Flown();
                yield return PlanOnShip(ResumeShip, null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofLossPlan(flown, ok);
                if (!ok.Ok || failed) yield break;
            }

            // Cycle 11: whole trips, Start to the parking orbit (tripA, tripB, tripC; not part of "all": each takes an hour or so).
            foreach (TripSpec trip in Trips)
            {
                if (!(wanted ?? "").Split(',').Contains("trip" + trip.Name)) continue;
                var ok = new CaptureResult();
                yield return ProofTrip(trip, ok);
                if (!ok.Ok || failed) yield break;
            }

            // Cycle 10: the trip carries on after leaving the ship, saving and loading (ADR 0004). build.sh then relaunches
            // KSP for the save made mid-trip, and loads that save once more with Flip and Burn removed.
            if (Want("resume"))
            {
                var flown = new Flown();
                yield return PlanOnShip(ResumeShip, null, null, flown, save);
                if (!flown.Ok) yield break;
                var ok = new CaptureResult();
                yield return ProofResume(flown, ok);
                if (!ok.Ok || failed) yield break;
            }

            Log("step " + step + ": done, quitting KSP");
            Application.Quit();
        }

        /// <summary>Fly <paramref name="shipName"/> from the test save, log its delta-v sources, plan a trip to Duna, screenshot it.</summary>
        private IEnumerator PlanOnShip(string shipName, string file, string alsoIgnite, CaptureResult outcome, string saveName = SaveFile)
        {
            // ---- load the test save and fly the ship
            try
            {
                // Same order as KSP's own MainMenu.OnLoadDialogPipelineFinished: the flight scene's Game.Load() reads
                // HighLogic.CurrentGame, so it must be set before switching scenes.
                ConfigNode node = GamePersistence.LoadSFSFile(saveName, SaveFolder);
                Game game = node == null ? null : GamePersistence.LoadGameCfg(node, SaveFolder, true, false);
                if (game == null || game.flightState == null)
                {
                    Fail("step " + step, "could not load saves/" + SaveFolder + "/" + SaveFile + ".sfs");
                    yield break;
                }

                HighLogic.CurrentGame = game;
                GamePersistence.UpdateScenarioModules(game);
                GameEvents.onGameStatePostLoad.Fire(node);
                HighLogic.SaveFolder = SaveFolder;

                int shipIndex = game.flightState.protoVessels.FindIndex(v => v.vesselName == shipName);
                Log("step " + step + ": loaded save '" + SaveFolder + "/" + saveName + "' (" + game.flightState.protoVessels.Count +
                    " vessels, mode " + game.Mode + ")");
                if (shipIndex < 0)
                {
                    Fail("step " + step, "ship '" + shipName + "' not found in the save");
                    yield break;
                }

                // Same call the Tracking Station's "Fly" button makes.
                Log("step " + step + ": switching to flight on '" + shipName + "' (vessel index " + shipIndex + ")");
                FlightDriver.StartAndFocusVessel(saveName, shipIndex);
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                yield break;
            }

            step++;
            MechJebCore core = null;
            float deadline = Time.realtimeSinceStartup + 300f;
            while (true)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail("step " + step, "timed out waiting for flight scene with MechJeb on '" + shipName + "'");
                    yield break;
                }

                Vessel active = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (active != null && active.vesselName == shipName && !active.packed)
                {
                    core = active.GetMasterMechJeb();
                    if (core != null && core.GetComputerModule<MechJebModuleFlipAndBurn>() != null) break;
                }

                yield return new WaitForSecondsRealtime(0.5f);
            }

            Vessel ship = FlightGlobals.ActiveVessel;
            var module = core.GetComputerModule<MechJebModuleFlipAndBurn>();
            Log("step " + step + ": in flight on '" + ship.vesselName + "', " + ship.situation + " " + ship.mainBody.bodyName +
                ", MechJeb core on part '" + core.part.partInfo.name + "', current stage " + ship.currentStage);
            bool listed = core.GetDisplayModules(MechJebModuleMenu.DisplayOrder.instance)
                .Any(m => m == module && !m.Hidden && m.ShowInCurrentScene);
            Log("step " + step + ": MechJeb menu lists '" + module.GetName() + "': " + listed);
            if (!listed)
            {
                Fail("step " + step, "Flip and Burn is not listed in MechJeb's menu");
                yield break;
            }

            step++;
            if (alsoIgnite != null)
            {
                // A second engine type: ignite it alongside the one already running (in memory only; nothing is saved).
                int lit = 0;
                foreach (Part p in ship.parts.Where(p => p.partInfo.name == alsoIgnite))
                foreach (ModuleEngines e in p.Modules.OfType<ModuleEngines>())
                {
                    e.Activate();
                    lit++;
                }

                Log("step " + step + ": ignited " + lit + " engine module(s) on '" + alsoIgnite + "' so two engine types are active");
                yield return new WaitForSecondsRealtime(2f);
                step++;
            }

            // ---- what each delta-v source says about the ship
            yield return Diagnostics(ship, core, module);
            step++;

            // ---- open the planner
            try
            {
                var menu = core.GetComputerModule<MechJebModuleMenu>();
                if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();

                // MechJeb opens every window at the same top-left spot; put ours in an empty area for the screenshot.
                float scale = GuiUtils.Scale > 0 ? GuiUtils.Scale : 1f;
                module.WindowVector = new Vector4(Screen.width / scale * 0.38f, Screen.height / scale * 0.18f, 0, 0);
                module.Enabled = true;
                module.SelectTripType(TripType.FullBurn);
                Log("step " + step + ": opened the '" + module.GetName() + "' window");
            }
            catch (Exception e)
            {
                Fail("step " + step, e.ToString());
                yield break;
            }

            step++;
            if (!module.SelectTarget(PlannerTarget))
            {
                Fail("step " + step, "could not select " + PlannerTarget);
                yield break;
            }

            Log("step " + step + ": selected " + PlannerTarget);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            if (outcome is Flown f)
            {
                f.Core = core;
                f.Module = module;
                f.Ship = ship;
            }

            if (file == null)
            {
                step++;
                outcome.Ok = true;
                yield break;
            }

            var result = new CaptureResult();
            int shotStep = step;
            yield return Capture(step, file, result, () => LogPlanner(shotStep, module, ship));
            if (!result.Ok)
            {
                Fail("step " + step, result.Error);
                yield break;
            }

            step++;
            outcome.Ok = true;
        }

        // ------------------------------------------------------------------ diagnostics

        private static IEnumerator Diagnostics(Vessel ship, MechJebCore core, MechJebModuleFlipAndBurn module)
        {
            // Let KSPIE's engine controller run some physics ticks: it rewrites the engine's thrust and Isp each tick.
            yield return new WaitForSecondsRealtime(3f);
            Log("diag: delta-v sources for '" + ship.vesselName + "' at UT " + F(Planetarium.GetUniversalTime()) + ", current stage " + ship.currentStage);

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
            try
            {
                if (dv != null) dv.SetCalcsDirty(true);
            }
            catch (Exception e)
            {
                Log("diag stock: SetCalcsDirty failed: " + e.Message);
            }

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
                try
                {
                    stats.RequestUpdate();
                }
                catch (Exception e)
                {
                    // MechJeb's own simulation can fail on a ship (it does on one of the test CubeSats); that's MechJeb's
                    // business, and it mustn't stop the test.
                    Log("diag MechJeb: its stage simulation failed on this ship (" + e.GetType().Name.Replace("Exception", "") + ": " + e.Message + ")");
                    stats = null;
                    break;
                }

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
                module.Replan();
                ShipPerformance perf = module.Planner.Ship;
                Log("diag FlipAndBurn: FullThrust=" + F(perf.FullThrust) + " N VacuumIsp=" + F(perf.VacuumIsp) + " s IspThrottleExponent=" +
                    F(perf.IspThrottleExponent) + " StartMass=" + F(perf.StartMass) + " kg EndMass=" + F(perf.EndMass) + " kg");
                Log("diag FlipAndBurn: FullThrustDeltaV=" + F(perf.FullThrustDeltaV) + " m/s DeltaVAt1g=" + F(perf.DeltaVAtAcceleration(9.81)) +
                    " m/s MaxAcceleration=" + F(perf.MaxAcceleration) + " m/s^2");
                ShipReading reading = module.Planner.Reading;
                if (reading != null)
                {
                    foreach (EngineReading e in reading.Engines)
                        Log("diag FlipAndBurn engine: " + e.Name + " Thrust=" + F(e.Thrust) + " N VacuumIsp=" + F(e.VacuumIsp) + " s k=" + F(e.IspThrottleExponent) +
                            " understood=" + e.Understood + " propellants=" + string.Join(",", e.Propellants.Select(p => p.Resource + ":" + F(p.Ratio) + "@" + F(p.Density))));
                    for (int i = 0; i < perf.Phases.Count; i++)
                    {
                        BurnPhase ph = perf.Phases[i];
                        Log("diag FlipAndBurn phase " + (i + 1) + ": Thrust=" + F(ph.Thrust) + " N Isp=" + F(ph.VacuumIsp) + " s StartMass=" + F(ph.StartMass) +
                            " kg EndMass=" + F(ph.EndMass) + " kg");
                    }

                    Log("diag FlipAndBurn: estimated=" + reading.Report.Estimated + " stagesBelow=" + reading.StagesBelow + " electricDemand=" +
                        F(reading.Report.ElectricDemand) + " powerSupply=" + F(reading.Report.PowerSupply) + " notes=" + string.Join(" | ", reading.Report.Notes));
                }

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
            Log("  Start button enabled: " + p.CanStart);
        }

        // ------------------------------------------------------------------ screenshots

        private class CaptureResult
        {
            public bool Ok;
            public string Error;
        }

        /// <summary>
        /// Saves the finished frame (3D view plus MechJeb's IMGUI windows) to FlipAndBurnTest/<paramref name="file"/>.
        /// <paramref name="onCaptured"/> runs in the same frame the pixels were read, so what it logs is what the image shows.
        /// Falls back to asking build.sh for an external grab when the in-game read comes back blank.
        /// </summary>
        private IEnumerator Capture(int step, string file, CaptureResult result, Action onCaptured, bool retried = false)
        {
            string folder = Path.Combine(KSPUtil.ApplicationRootPath, ScreenshotFolder);
            string path = Path.GetFullPath(Path.Combine(folder, file));
            Directory.CreateDirectory(folder);
            if (File.Exists(path)) File.Delete(path);
            // KSP only draws while its window is on screen; a capture taken while it's hidden comes back blank, and the
            // external grab then shows whatever is in front instead.
            if (!Application.isFocused)
                Log("step " + step + ": note: the KSP window isn't the active window (fine if it's still on screen)");

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
            // Blank in game: KSP isn't drawing, most likely because its window is hidden. Freeze the moment (pause the
            // game) and wait for the window to come back, then try again, rather than capture nothing.
            if (!retried && HighLogic.LoadedSceneIsFlight)
            {
                Log("step " + step + ": in-game capture blank: pausing the game until the KSP window is back on screen (up to 15 minutes)");
                FlightDriver.SetPause(true, false);
                float waitUntil = Time.realtimeSinceStartup + 900f;
                while (!Application.isFocused && Time.realtimeSinceStartup < waitUntil) yield return new WaitForSecondsRealtime(1f);
                FlightDriver.SetPause(false, false);
                yield return new WaitForSecondsRealtime(1f);
                yield return Capture(step, file, result, onCaptured, true);
                yield break;
            }

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
                    string hash = BitConverter.ToString(System.Security.Cryptography.SHA256.Create().ComputeHash(File.ReadAllBytes(path)));
                    if (hash == lastExternalHash)
                        Log("step " + step + ": WARNING: this external capture is identical to the previous one: the KSP window probably wasn't on screen");
                    lastExternalHash = hash;
                    Log("step " + step + ": screenshot saved to " + path + " (" + new FileInfo(path).Length + " bytes, method external-x11grab)");
                    result.Ok = true;
                    yield break;
                }

                break;
            }

            result.Error = "every capture was blank (mean brightness below " + MinBrightness + "/255)";
        }

        private static string lastExternalHash;

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
