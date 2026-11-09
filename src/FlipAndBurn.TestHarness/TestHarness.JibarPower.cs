using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using KSP.UI;
using KSP.UI.Screens;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Kris's Jibar fusion reactors, pellet maker and pellet tank (part configs in his GameData, not part of Flip and Burn):
    // proof that they load, are listed in the editor, make and burn what their configs say, run out cleanly, keep a ship
    // fueled, move pellets between docked ships, and carry on through time warp and a save and reload. Test copy only.
    // Only when asked: "jibarpower".
    public partial class TestHarness
    {
        private const string Pellets = "JibarFuelPellets";
        private const string PowerTest = "Jibar Power Test", PelletCarrier = "Jibar Pellet Carrier";
        private static readonly (string Name, double Out, double Burn, double Load)[] Reactors =
        {
            ("Jibar.FusionReactor.Small", 2000, 2000 / 920160000.0, 20),
            ("Jibar.FusionReactor.Medium", 20000, 20000 / 920160000.0, 200),
            ("Jibar.FusionReactor.Large", 200000, 200000 / 920160000.0, 2000),
        };
        private const string PelletMaker = "Jibar.PelletMaker", PelletTank = "Jibar.PelletTank";
        private const double MakerCharge = 4600.8, MakerPellets = 0.00025;
        private bool powerOk = true;

        private void PowerCheck(string what, bool ok, string detail)
        {
            Log($"jibar power: {what}: {(ok ? "AS SET" : "NOT AS SET")}: {detail}");
            if (!ok) powerOk = false;
        }

        private IEnumerator ProofJibarPower()
        {
            // ---- 1. loaded
            PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(Pellets);
            Log($"jibar power: resource {Pellets}: {(def == null ? "MISSING" : $"'{def.displayName}', density {def.density} t, flow {def.resourceFlowMode}, transfer {def.resourceTransferMode}")}");
            if (def == null) { Fail("jibarpower", "the pellet resource didn't load"); failed = true; yield break; }
            foreach (string name in Reactors.Select(r => r.Name).Concat(new[] { PelletMaker, PelletTank }))
            {
                AvailablePart ap = PartLoader.getPartInfoByName(name);
                if (ap == null) { Fail("jibarpower", name + " didn't load"); failed = true; yield break; }
                Part prefab = ap.partPrefab;
                string res = string.Join(", ", prefab.Resources.Cast<PartResource>().Select(r => $"{r.resourceName} {r.maxAmount}"));
                string conv = string.Join("; ", prefab.Modules.OfType<ModuleResourceConverter>().Select(c =>
                    $"{c.ConverterName}: in {string.Join(", ", c.inputList.Select(x => $"{x.ResourceName} {x.Ratio:G9}"))}, " +
                    $"out {string.Join(", ", c.outputList.Select(x => $"{x.ResourceName} {x.Ratio:G9}"))}"));
                Log($"jibar power: part {name}: '{ap.title}', category {ap.category}, mass {prefab.mass} t, cost {ap.cost}, tech {ap.TechRequired}; " +
                    $"resources {res}; converters {(conv.Length > 0 ? conv : "none")}; modules {string.Join(", ", prefab.Modules.Cast<PartModule>().Select(m => m.moduleName))}");
            }

            // ---- 2. the editor's tabs
            ConfigNode node = GamePersistence.LoadSFSFile(SaveFile, SaveFolder);
            Game game = GamePersistence.LoadGameCfg(node, SaveFolder, true, false);
            HighLogic.CurrentGame = game;
            GamePersistence.UpdateScenarioModules(game);
            GameEvents.onGameStatePostLoad.Fire(node);
            HighLogic.SaveFolder = SaveFolder;
            EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
            EditorDriver.editorFacility = EditorFacility.VAB;
            HighLogic.LoadScene(GameScenes.EDITOR);
            float until = Time.realtimeSinceStartup + 180;
            while (!(HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null && PartCategorizer.Instance != null && PartCategorizer.Ready))
            {
                if (Time.realtimeSinceStartup > until) { Fail("jibarpower", "the editor didn't open"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            yield return new WaitForSecondsRealtime(5f);
            int shot = 1;
            foreach ((string tab, string[] names) in new[]
                     {
                         ("Electrical", Reactors.Select(r => r.Name).ToArray()), ("Utility", new[] { PelletMaker }), ("FuelTank", new[] { PelletTank }),
                     })
            {
                var cat = typeof(PartCategorizer).GetField("subcategoryFunction" + tab)?.GetValue(PartCategorizer.Instance) as PartCategorizer.Category;
                cat?.button?.activeButton?.SetState(UIRadioButton.State.True, UIRadioButton.CallType.USER, null);
                yield return new WaitForSecondsRealtime(3f);
                var listed = EditorPartList.Instance.GetComponentsInChildren<EditorPartIcon>(true).Where(i => i.partInfo != null).Select(i => i.partInfo.name).ToList();
                foreach (string n in names) PowerCheck($"VAB tab {tab} ({cat?.button?.categoryName})", listed.Contains(n), $"{n} listed: {listed.Contains(n)}");
                // For the picture: the tab narrowed to Jibar parts (an editor filter, removed afterwards).
                string filter = "flipandburn-test-jibar-" + tab;
                EditorPartList.Instance.ExcludeFilters.AddFilter(new EditorPartListFilter<AvailablePart>(filter, a => a.title.StartsWith("Jibar", StringComparison.Ordinal)));
                EditorPartList.Instance.Refresh();
                yield return new WaitForSecondsRealtime(3f);
                yield return Shot($"jibar-power-{shot++}-vab-{tab.ToLowerInvariant()}.png", null);
                EditorPartList.Instance.ExcludeFilters.RemoveFilter(filter);
                EditorPartList.Instance.Refresh();
            }

            // The test craft built in the VAB, for a picture of the parts in the editor (the parts list itself sits under other
            // mods' windows in this install).
            try
            {
                string craftPath = KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/" + PowerTest + ".craft";
                MethodInfo load = typeof(EditorLogic).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name == "LoadShipFromFile" && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(string));
                load?.Invoke(load.IsStatic ? null : EditorLogic.fetch, new object[] { craftPath });
                Log($"jibar power: the test craft loaded into the VAB: {(load != null ? "yes" : "no LoadShipFromFile")}; parts in the editor {EditorLogic.fetch.ship?.parts?.Count}");
            }
            catch (Exception e) { Log("jibar power: couldn't load the test craft into the VAB: " + (e.InnerException ?? e).Message); }

            yield return new WaitForSecondsRealtime(5f);
            yield return Shot($"jibar-power-{shot++}-vab-test-craft.png", null);

            // ---- 3. the test craft in orbit
            var host = new Flown();
            yield return FlyFromSave(SaveFile, ElectricShip, host, false);
            if (!host.Ok) yield break;
            yield return new WaitForSecondsRealtime(3f);
            CelestialBody kerbin = FlightGlobals.Bodies.First(b => b.bodyName == "Kerbin");
            ProtoVessel spawned = null;
            try { spawned = SpawnInOrbit(KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/" + PowerTest + ".craft", PowerTest, kerbin, 700_000); }
            catch (Exception e) { Fail("jibarpower", "couldn't put the test craft in orbit: " + e); failed = true; yield break; }
            yield return SwitchTo(spawned, "jibarpower");
            if (failed) yield break;
            Vessel v = FlightGlobals.ActiveVessel;
            yield return new WaitForSecondsRealtime(5f);
            Log($"jibar power: in orbit: {v.vesselName}, {v.situation} around {v.mainBody.bodyName}, {v.altitude / 1000:F1} km; parts {v.parts.Count} " +
                $"({string.Join(", ", v.parts.Select(p => p.partInfo.name))}); electric charge {Amount(v, "ElectricCharge"):F1}/{Capacity(v, "ElectricCharge"):F0}, " +
                $"pellets {Amount(v, Pellets):F2}/{Capacity(v, Pellets):F0}; mass {v.totalMass:F2} t");
            Frame(v, 0.2f, 0.8f, 1.6f);

            // ---- 4. each reactor alone: the charge made and the pellets burned, from empty batteries
            foreach ((string name, double outRate, double burn, double load) in Reactors)
            {
                ModuleResourceConverter c = Converter(v, name);
                StopAll(v);
                yield return WaitPhysics(5);
                OpenMenu(c.part);
                SetAll(v, "ElectricCharge", 0);
                c.StartResourceConverter();
                yield return WaitPhysics(2);
                // Pellets: the whole ship's (a reactor draws from every pellet store on the ship at once, as Ore flows).
                double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, "ElectricCharge"), pl0 = Amount(v, Pellets);
                yield return WaitPhysics(6);
                double dt = Planetarium.GetUniversalTime() - ut0, ec = (Amount(v, "ElectricCharge") - ec0) / dt, pl = (pl0 - Amount(v, Pellets)) / dt;
                PowerCheck($"{name} makes charge", Math.Abs(ec - outRate) / outRate < 0.02,
                    $"{ec:F1} electric charge a second (set {outRate}) over {dt:F2} game s; burns {pl:E4} pellets a second (set {burn:E4}); status '{c.status}'");
                PowerCheck($"{name} burns pellets", Math.Abs(pl - burn) / burn < 0.02, $"{pl:E4} a second (set {burn:E4}): a full load of {load} lasts {load / pl / 21600:F0} Kerbin days");
                // The picture while it works: the charge emptied again just before, so the menu shows it at full load.
                SetAll(v, "ElectricCharge", 0);
                yield return WaitPhysics(3);
                yield return Shot($"jibar-power-{shot++}-{name.Split('.').Last().ToLowerInvariant()}-running.png", null);
                CloseMenus();
            }

            // ---- 5. idling with the batteries full: the large reactor burns only what the ship uses (the probe core)
            {
                StopAll(v);
                ModuleResourceConverter c = Converter(v, Reactors[2].Name);
                c.StartResourceConverter();
                yield return new WaitForSecondsRealtime(3f);
                double ut0 = Planetarium.GetUniversalTime(), pl0 = Amount(v, Pellets);
                while (Planetarium.GetUniversalTime() - ut0 < 20) yield return new WaitForFixedUpdate();
                double dt = Planetarium.GetUniversalTime() - ut0, burned = (pl0 - Amount(v, Pellets)) / dt, use = CoreDraw(v);
                PowerCheck("large reactor idling with full batteries", burned < Reactors[2].Burn * 0.01,
                    $"burns {burned:E3} pellets a second over {dt:F0} game s ({burned * 920160000:F2} charge a second; the probe core draws {use:F3}); " +
                    $"charge {Amount(v, "ElectricCharge"):F0}/{Capacity(v, "ElectricCharge"):F0}; status '{c.status}'");
            }

            // ---- 6. the pellet maker alone, on stored charge
            {
                StopAll(v);
                yield return WaitPhysics(5);
                SetAll(v, "ElectricCharge", double.MaxValue);
                SetPart(v, Reactors[2].Name, Pellets, 1000);
                ModuleResourceConverter m = Converter(v, PelletMaker);
                m.StartResourceConverter();
                yield return WaitPhysics(2);
                double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, "ElectricCharge"), pl0 = Amount(v, Pellets);
                yield return WaitPhysics(25);
                double dt = Planetarium.GetUniversalTime() - ut0, ec = (ec0 - Amount(v, "ElectricCharge")) / dt, pl = (Amount(v, Pellets) - pl0) / dt;
                PowerCheck("pellet maker uses charge", Math.Abs(ec - MakerCharge) / MakerCharge < 0.02, $"{ec:F1} a second (set {MakerCharge}); status '{m.status}'");
                PowerCheck("pellet maker makes pellets", Math.Abs(pl - MakerPellets) / MakerPellets < 0.02, $"{pl:E4} a second (set {MakerPellets:E4}) over {dt:F2} game s");
                OpenMenu(m.part);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot($"jibar-power-{shot++}-pellet-maker.png", null);
                CloseMenus();
            }

            // ---- 7. reactor + pellet maker: keeps itself fueled, with power left over (and running a GIANT at 10 % meanwhile)
            {
                StopAll(v);
                yield return WaitPhysics(5);
                SetPart(v, Reactors[2].Name, Pellets, 1000);
                SetPart(v, PelletTank, Pellets, 0);
                ModuleResourceConverter r = Converter(v, Reactors[2].Name), m = Converter(v, PelletMaker);
                r.StartResourceConverter();
                m.StartResourceConverter();
                yield return new WaitForSecondsRealtime(3f);
                double ut0 = Planetarium.GetUniversalTime(), pl0 = Amount(v, Pellets), ecMin = double.MaxValue;
                while (Planetarium.GetUniversalTime() - ut0 < 30)
                {
                    yield return new WaitForFixedUpdate();
                    ecMin = Math.Min(ecMin, Amount(v, "ElectricCharge"));
                }

                double dt = Planetarium.GetUniversalTime() - ut0, gain = (Amount(v, Pellets) - pl0) / dt;
                PowerCheck("reactor and pellet maker stay fueled", gain > 0,
                    $"pellets {pl0:F5} -> {Amount(v, Pellets):F5} over {dt:F0} game s ({gain:E3} a second: made {MakerPellets:E3}, burned to make them " +
                    $"{MakerCharge / 920160000:E3}); charge never below {ecMin:F0}/{Capacity(v, "ElectricCharge"):F0}; reactor '{r.status}', maker '{m.status}'");
                OpenMenu(r.part);
                OpenMenu(m.part);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot($"jibar-power-{shot++}-reactor-and-maker.png", null);
                CloseMenus();

                // A GIANT at 10 % throttle (19,119 charge a second) on top, for 5 game seconds.
                ModuleEngines giant = v.FindPartModulesImplementing<ModuleEngines>().FirstOrDefault(e => e.part.partInfo.name.Contains("Giant"));
                if (giant != null)
                {
                    giant.Activate();
                    float throttle = 0.1f;
                    FlightInputCallback fly = s => s.mainThrottle = throttle;
                    v.OnFlyByWire += fly;
                    yield return new WaitForSecondsRealtime(1f);
                    double u0 = Planetarium.GetUniversalTime(), min = double.MaxValue, drawSum = 0;
                    int n = 0;
                    while (Planetarium.GetUniversalTime() - u0 < 5)
                    {
                        yield return new WaitForFixedUpdate();
                        min = Math.Min(min, Amount(v, "ElectricCharge"));
                        drawSum += giant.finalThrust / (giant.atmosphereCurve.Evaluate(0) * 9.80665) * 200000;
                        n++;
                    }

                    throttle = 0;
                    yield return WaitPhysics(5);
                    v.OnFlyByWire -= fly;
                    giant.Shutdown();
                    PowerCheck("a GIANT at 10 % on the large reactor, the pellet maker running", min > 0,
                        $"the GIANT drew about {drawSum / Math.Max(1, n):F0} charge a second; charge never below {min:F0}; reactor '{r.status}'");
                }
            }

            // ---- 8. running out: the large reactor with almost no pellets left, nothing else holding any
            {
                StopAll(v);
                yield return WaitPhysics(5);
                SetAll(v, Pellets, 0);
                SetPart(v, Reactors[2].Name, Pellets, Reactors[2].Burn * 0.05);   // about a twentieth of a second at full power
                SetAll(v, "ElectricCharge", 0);
                ModuleResourceConverter r = Converter(v, Reactors[2].Name);
                r.StartResourceConverter();
                yield return WaitPhysics(25);
                double ec0 = Amount(v, "ElectricCharge"), ut0 = Planetarium.GetUniversalTime();
                yield return WaitPhysics(25);
                double rate = (Amount(v, "ElectricCharge") - ec0) / (Planetarium.GetUniversalTime() - ut0);
                PowerCheck("large reactor out of pellets", Amount(v, Pellets) < 1e-9 && rate < 1,
                    $"pellets left {Amount(v, Pellets):E2}; charge made after {rate:F2} a second (the probe core draws {CoreDraw(v):F3}); " +
                    $"charge {Amount(v, "ElectricCharge"):F1}; status '{r.status}'; still switched on {r.IsActivated}");
                OpenMenu(r.part);
                yield return new WaitForSecondsRealtime(1f);
                yield return Shot($"jibar-power-{shot++}-out-of-pellets.png", null);
                CloseMenus();
                // Refill: the reactors full, the tank empty (for the docking proof later).
                foreach (var rr in Reactors) SetPart(v, rr.Name, Pellets, rr.Load);
                SetAll(v, "ElectricCharge", double.MaxValue);
            }

            // ---- 9. the Flip and Burn planner on this reactor-powered ship (a GIANT active): what it says about power
            {
                StopAll(v);
                Converter(v, Reactors[2].Name).StartResourceConverter();
                ModuleEngines giant = v.FindPartModulesImplementing<ModuleEngines>().FirstOrDefault(e => e.part.partInfo.name.Contains("Giant"));
                giant?.Activate();
                MechJebCore core = v.GetMasterMechJeb();
                var module = core?.GetComputerModule<MechJebModuleFlipAndBurn>();
                if (module != null)
                {
                    var menu = core.GetComputerModule<MechJebModuleMenu>();
                    if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();
                    float scale = GuiUtils.Scale > 0 ? GuiUtils.Scale : 1f;
                    module.WindowVector = new Vector4(Screen.width / scale * 0.38f, Screen.height / scale * 0.18f, 0, 0);
                    module.Enabled = true;
                    module.SelectTripType(TripType.FullBurn);
                    module.SelectTarget("Duna");
                    yield return new WaitForSecondsRealtime(SettleSeconds);
                    module.Replan();
                    yield return new WaitForSecondsRealtime(3f);
                    Log("jibar power: Flip and Burn planner on the reactor ship (report only): ship rows: " +
                        string.Join(" | ", module.Planner.ShipRows.Select(kv => $"{kv.Key}: {kv.Value}")));
                    Log("jibar power: Flip and Burn planner: trip rows: " + string.Join(" | ", module.Planner.TripRows.Select(kv => $"{kv.Key}: {kv.Value}")));
                    Log("jibar power: Flip and Burn planner: notes: " + string.Join(" | ", module.Planner.Notes));
                    yield return Shot($"jibar-power-{shot++}-flip-and-burn-planner.png", null);
                    module.Enabled = false;
                    if (menu != null && menu.windowStat != MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();
                }
                else Log("jibar power: Flip and Burn planner: no MechJeb core with Flip and Burn on the test craft");
                giant?.Shutdown();
            }

            // ---- 10. time warp on rails: the large reactor and the pellet maker running, the reactor half full
            {
                StopAll(v);
                SetPart(v, Reactors[2].Name, Pellets, 1000);
                SetAll(v, "ElectricCharge", double.MaxValue);
                ModuleResourceConverter r = Converter(v, Reactors[2].Name), m = Converter(v, PelletMaker);
                r.StartResourceConverter();
                m.StartResourceConverter();
                yield return new WaitForSecondsRealtime(3f);
                double ut0 = Planetarium.GetUniversalTime(), pl0 = Amount(v, Pellets), ec0 = Amount(v, "ElectricCharge");
                TimeWarp.SetRate(5, true);   // 1,000x
                yield return new WaitForSecondsRealtime(15f);
                TimeWarp.SetRate(0, true);
                until = Time.realtimeSinceStartup + 60;
                while ((TimeWarp.CurrentRate > 1.01f || v.packed) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
                double dt = Planetarium.GetUniversalTime() - ut0, made = Amount(v, Pellets) - pl0;
                PowerCheck("through time warp (1,000x on rails)", made > 0,
                    $"{dt:F0} game s: pellets {pl0:F4} -> {Amount(v, Pellets):F4} ({made / dt:E3} a second; the maker alone {MakerPellets:E3}); " +
                    $"charge {ec0:F0} -> {Amount(v, "ElectricCharge"):F0}; reactor '{r.status}' on {r.IsActivated}, maker '{m.status}' on {m.IsActivated}");
            }

            // ---- 11. saved and loaded again: both still on, the amounts kept, and then away and back for 6 game hours
            {
                const string check = "jibar-power-check";
                double pl0 = Amount(v, Pellets), ec0 = Amount(v, "ElectricCharge");
                GamePersistence.SaveGame(check, SaveFolder, SaveMode.OVERWRITE);
                yield return new WaitForSecondsRealtime(2f);
                Vessel old = FlightGlobals.ActiveVessel;
                ConfigNode saved = GamePersistence.LoadSFSFile(check, SaveFolder);
                Game game2 = saved == null ? null : GamePersistence.LoadGameCfg(saved, check, true, false);
                int index = game2?.flightState?.protoVessels.FindIndex(pv => pv.vesselName == PowerTest) ?? -1;
                if (index < 0) { Fail("jibarpower", "the save didn't have the test craft"); failed = true; yield break; }
                HighLogic.CurrentGame = game2;
                GamePersistence.UpdateScenarioModules(game2);
                GameEvents.onGameStatePostLoad.Fire(saved);
                HighLogic.SaveFolder = SaveFolder;
                FlightDriver.StartAndFocusVessel(check, index);
                until = Time.realtimeSinceStartup + 300;
                while (true)
                {
                    Vessel a = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                    if (a != null && !ReferenceEquals(a, old) && a.vesselName == PowerTest && a.loaded && !a.packed) break;
                    if (Time.realtimeSinceStartup > until) { Fail("jibarpower", "the save didn't load"); failed = true; yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                yield return new WaitForSecondsRealtime(5f);
                v = FlightGlobals.ActiveVessel;
                ModuleResourceConverter r = Converter(v, Reactors[2].Name), m = Converter(v, PelletMaker);
                PowerCheck("after a save and reload", r.IsActivated && m.IsActivated && Math.Abs(Amount(v, Pellets) - pl0) < 0.01,
                    $"reactor on {r.IsActivated} ('{r.status}'), maker on {m.IsActivated} ('{m.status}'); pellets {pl0:F4} -> {Amount(v, Pellets):F4}; " +
                    $"charge {ec0:F0} -> {Amount(v, "ElectricCharge"):F0}");

                // Away: the other ship flown while 6 game hours pass on rails, then back.
                Vessel other = FlightGlobals.Vessels.FirstOrDefault(x => x.vesselName == ElectricShip);
                double pl1 = Amount(v, Pellets), ut1 = Planetarium.GetUniversalTime();
                if (other != null)
                {
                    FlightGlobals.ForceSetActiveVessel(other);
                    yield return new WaitForSecondsRealtime(8f);
                    TimeWarp.SetRate(5, true);
                    until = Time.realtimeSinceStartup + 120;
                    while (Planetarium.GetUniversalTime() - ut1 < 21600 && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
                    TimeWarp.SetRate(0, true);
                    yield return new WaitForSecondsRealtime(3f);
                    Vessel back = FlightGlobals.Vessels.First(x => x.vesselName == PowerTest);
                    FlightGlobals.ForceSetActiveVessel(back);
                    until = Time.realtimeSinceStartup + 120;
                    while (!(FlightGlobals.ActiveVessel == back && back.loaded && !back.packed) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
                    yield return new WaitForSecondsRealtime(5f);
                    v = FlightGlobals.ActiveVessel;
                    double away = Planetarium.GetUniversalTime() - ut1;
                    r = Converter(v, Reactors[2].Name);
                    m = Converter(v, PelletMaker);
                    Log($"jibar power: away for {away:F0} game s (another ship flown): pellets {pl1:F4} -> {Amount(v, Pellets):F4} " +
                        $"(the maker running all that time would have made {MakerPellets * away:F2}); charge {Amount(v, "ElectricCharge"):F0}; " +
                        $"reactor on {r.IsActivated} ('{r.status}'), maker on {m.IsActivated} ('{m.status}')");
                }

                try { System.IO.File.Delete(KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/" + check + ".sfs"); }
                catch (Exception) { /* left in the test copy: harmless */ }
            }

            // ---- 12. docking: a second ship with a full pellet tank docks; pellets move to the reactor
            yield return DockAndTransfer();

            Log($"jibar power: {(powerOk ? "ALL AS SET" : "SOME NOT AS SET (see above)")}");
            if (!powerOk) { Fail("jibarpower", "some checks didn't come out as set"); failed = true; }
        }

        private IEnumerator DockAndTransfer()
        {
            Vessel a = FlightGlobals.ActiveVessel;
            StopAll(a);
            foreach (var rr in Reactors) SetPart(a, rr.Name, Pellets, 0);
            SetPart(a, PelletTank, Pellets, 0);
            ProtoVessel pb;
            try { pb = SpawnNear(KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/" + PelletCarrier + ".craft", PelletCarrier, a, 40); }
            catch (Exception e) { PowerCheck("docking", false, "couldn't add the carrier: " + e); yield break; }
            float until = Time.realtimeSinceStartup + 60;
            while ((pb.vesselRef == null || !pb.vesselRef.loaded || pb.vesselRef.packed) && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.5f);
            Vessel b = pb.vesselRef;
            if (b == null || !b.loaded) { PowerCheck("docking", false, "the carrier didn't load next to the test craft"); yield break; }
            yield return new WaitForSecondsRealtime(3f);
            ModuleDockingNode da = a.FindPartModulesImplementing<ModuleDockingNode>().First(), db = b.FindPartModulesImplementing<ModuleDockingNode>().First();
            Log($"jibar power: carrier added {(b.GetWorldPos3D() - a.GetWorldPos3D()).magnitude:F1} m away; carrier pellets {Amount(b, Pellets):F1}; ports '{da.state}' / '{db.state}'");

            // Line the carrier's port up with the test craft's, 20 cm apart, at the same velocity, and let the ports pull together.
            Transform ta = da.nodeTransform, tb = db.nodeTransform;
            Quaternion turn = Quaternion.FromToRotation(tb.forward, -ta.forward);
            b.SetRotation(turn * b.transform.rotation);
            yield return new WaitForFixedUpdate();
            Vector3 shift = (ta.position + ta.forward * 0.2f) - db.nodeTransform.position;
            b.SetPosition(b.transform.position + shift);
            foreach (Part p in b.parts.Where(p => p.Rigidbody != null))
            {
                p.Rigidbody.velocity = a.rootPart.Rigidbody.velocity;
                p.Rigidbody.angularVelocity = Vector3.zero;
            }

            until = Time.realtimeSinceStartup + 20;
            while (!(a.parts.Contains(db.part) || db.vessel == da.vessel) && Time.realtimeSinceStartup < until) yield return new WaitForFixedUpdate();
            bool magnetic = db.vessel == da.vessel;
            if (!magnetic)
            {
                MethodInfo dock = typeof(ModuleDockingNode).GetMethod("DockToVessel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Log($"jibar power: the ports didn't pull together in 20 s ('{da.state}' / '{db.state}', {(da.nodeTransform.position - db.nodeTransform.position).magnitude:F2} m apart); docking with KSP's own DockToVessel");
                try { dock?.Invoke(db, new object[] { da }); }
                catch (Exception e) { Log("jibar power: DockToVessel failed: " + e.InnerException?.Message); }
                yield return new WaitForSecondsRealtime(3f);
            }

            Vessel d = FlightGlobals.ActiveVessel;
            bool docked = db.vessel == da.vessel;
            PowerCheck("docked", docked, $"{(magnetic ? "the ports pulled together and docked" : "docked by KSP's DockToVessel")}; ports '{da.state}' / '{db.state}'; " +
                $"one ship now: '{d.vesselName}', {d.parts.Count} parts, pellets {Amount(d, Pellets):F2}");
            if (!docked) yield break;
            Frame(d, 0.2f, 0.8f, 1.6f);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("jibar-power-docked.png", null);

            // (a) The reactor draws from the other ship's tank by itself: its own pellets and its ship's tank empty.
            Part carrierTank = db.part.vessel.parts.First(p => p.partInfo.name == PelletTank && p.Resources.Get(Pellets).amount > 1000);
            ModuleResourceConverter r = Converter(d, Reactors[2].Name);
            SetAll(d, "ElectricCharge", 0);
            double t0 = carrierTank.Resources.Get(Pellets).amount, ut0 = Planetarium.GetUniversalTime();
            r.StartResourceConverter();
            yield return WaitPhysics(10);
            double used = t0 - carrierTank.Resources.Get(Pellets).amount, dt = Planetarium.GetUniversalTime() - ut0;
            PowerCheck("reactor draws pellets from the docked ship's tank", used > 0,
                $"the carrier's tank {t0:F6} -> {carrierTank.Resources.Get(Pellets).amount:F6} in {dt:F2} game s ({used / dt:E3} a second; the reactor burns {Reactors[2].Burn:E3} at full); status '{r.status}'");
            r.StopResourceConverter();

            // (b) Moving pellets by hand, as Alt + right-click does: 100 from the carrier's tank to the large reactor.
            Part reactor = r.part;
            double tankBefore = carrierTank.Resources.Get(Pellets).amount, reactorBefore = reactor.Resources.Get(Pellets).amount;
            MethodInfo transfer = typeof(Part).GetMethods().FirstOrDefault(mi => mi.Name == "TransferResource" && mi.GetParameters().Length == 3 &&
                                                                               mi.GetParameters()[0].ParameterType == typeof(PartResource));
            string how = "";
            if (transfer != null)
            {
                transfer.Invoke(reactor, new object[] { reactor.Resources.Get(Pellets), 100.0, carrierTank });
                double tankNow = carrierTank.Resources.Get(Pellets).amount, reactorNow = reactor.Resources.Get(Pellets).amount;
                Log($"jibar power: Part.TransferResource(reactor's pellets, 100, carrier's tank): the carrier's tank {tankBefore:F6} -> {tankNow:F6}, " +
                    $"the reactor {reactorBefore:F6} -> {reactorNow:F6}");
                if (Math.Abs((tankBefore - tankNow) - (reactorNow - reactorBefore)) > 0.01)
                {
                    // Not a two-sided move as called: undo it, and move them as a pump does.
                    reactor.Resources.Get(Pellets).amount = reactorBefore;
                    carrierTank.Resources.Get(Pellets).amount = tankBefore;
                    transfer = null;
                }
                else how = "Part.TransferResource (what the stock transfer buttons call)";
            }

            if (transfer == null)
            {
                double moved = carrierTank.RequestResource(Pellets, 100.0, ResourceFlowMode.NO_FLOW);
                reactor.RequestResource(Pellets, -moved, ResourceFlowMode.NO_FLOW);
                how = "taking them from the carrier's tank and giving them to the reactor (each part alone)";
            }

            yield return WaitPhysics(2);
            PowerCheck("100 pellets moved by hand between the docked ships", Math.Abs(reactor.Resources.Get(Pellets).amount - reactorBefore - 100) < 0.01 &&
                       Math.Abs(tankBefore - carrierTank.Resources.Get(Pellets).amount - 100) < 0.01,
                $"by {how}: the carrier's tank {tankBefore:F2} -> {carrierTank.Resources.Get(Pellets).amount:F2}, the reactor {reactorBefore:F2} -> " +
                $"{reactor.Resources.Get(Pellets).amount:F2}; transfer mode {PartResourceLibrary.Instance.GetDefinition(Pellets).resourceTransferMode}");
            OpenMenu(reactor);
            OpenMenu(carrierTank);
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot("jibar-power-pellets-moved.png", null);
            CloseMenus();
        }

        private IEnumerator SwitchTo(ProtoVessel spawned, string step)
        {
            float until = Time.realtimeSinceStartup + 60;
            while (spawned?.vesselRef == null)
            {
                if (Time.realtimeSinceStartup > until) { Fail(step, "the test craft didn't appear"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            FlightGlobals.ForceSetActiveVessel(spawned.vesselRef);
            until = Time.realtimeSinceStartup + 120;
            while (!(FlightGlobals.ActiveVessel == spawned.vesselRef && spawned.vesselRef.loaded && !spawned.vesselRef.packed))
            {
                if (Time.realtimeSinceStartup > until) { Fail(step, "couldn't switch to the test craft"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        // A craft file as a new vessel on another vessel's orbit, a few metres along it (it loads next to that vessel).
        private static ProtoVessel SpawnNear(string craftPath, string name, Vessel near, double metres)
        {
            Orbit o = near.orbit;
            double ahead = metres / o.semiMajorAxis;
            var orbit = new Orbit(o.inclination, o.eccentricity, o.semiMajorAxis, o.LAN, o.argumentOfPeriapsis, o.meanAnomalyAtEpoch + ahead, o.epoch, o.referenceBody);
            return SpawnOnOrbit(craftPath, name, orbit);
        }

        private static ProtoVessel SpawnOnOrbit(string craftPath, string name, Orbit orbit)
        {
            ShipConstruct ship = ShipConstruction.LoadShip(craftPath);
            if (ship == null) throw new InvalidOperationException("couldn't load " + craftPath);
            var proto = new ProtoVessel(new ConfigNode(), null);
            Vessel dummy = new GameObject().AddComponent<Vessel>();
            dummy.parts = ship.parts;
            proto.vesselRef = dummy;
            uint mission = (uint)Guid.NewGuid().GetHashCode(), launch = HighLogic.CurrentGame.launchID++;
            foreach (Part p in ship.parts)
            {
                p.flightID = ShipConstruction.GetUniqueFlightID(HighLogic.CurrentGame.flightState);
                p.missionID = mission;
                p.launchID = launch;
                p.flagURL = HighLogic.CurrentGame.flagURL;
                p.temperature = 1.0;
                proto.protoPartSnapshots.Add(new ProtoPartSnapshot(p, proto));
            }

            foreach (ProtoPartSnapshot snap in proto.protoPartSnapshots) snap.storePartRefs();
            ConfigNode[] parts = proto.protoPartSnapshots.Select(snap =>
            {
                var n = new ConfigNode("PART");
                snap.Save(n);
                return n;
            }).ToArray();
            foreach (Part p in ship.parts) UnityEngine.Object.Destroy(p.gameObject);
            UnityEngine.Object.Destroy(dummy.gameObject);
            ConfigNode node = ProtoVessel.CreateVesselNode(name, VesselType.Probe, orbit, 0, parts);
            Log($"jibar power: adding '{name}' ({parts.Length} parts) around {orbit.referenceBody.bodyName}");
            return HighLogic.CurrentGame.AddVessel(node);
        }

        private static ModuleResourceConverter Converter(Vessel v, string partName) =>
            v.parts.First(p => p.partInfo.name == partName).FindModuleImplementing<ModuleResourceConverter>();

        private static void StopAll(Vessel v)
        {
            foreach (ModuleResourceConverter c in v.FindPartModulesImplementing<ModuleResourceConverter>())
                if (c.IsActivated) c.StopResourceConverter();
        }

        private static void SetAll(Vessel v, string resource, double amount)
        {
            foreach (Part p in v.parts)
            {
                PartResource r = p.Resources.Get(resource);
                if (r != null) r.amount = Math.Min(r.maxAmount, Math.Max(0, amount));
            }
        }

        private static void SetPart(Vessel v, string partName, string resource, double amount)
        {
            foreach (Part p in v.parts.Where(p => p.partInfo.name == partName))
            {
                PartResource r = p.Resources.Get(resource);
                if (r != null) r.amount = Math.Min(r.maxAmount, Math.Max(0, amount));
            }
        }

        private static double Capacity(Vessel v, string resource) => v.parts.Sum(p => p.Resources.Get(resource)?.maxAmount ?? 0);

        private static IEnumerator WaitPhysics(int frames)
        {
            for (int i = 0; i < frames; i++) yield return new WaitForFixedUpdate();
        }

        private static void OpenMenu(Part p)
        {
            try { UIPartActionController.Instance.SpawnPartActionWindow(p); }
            catch (Exception e) { Log("jibar power: couldn't open the part's menu: " + e.Message); }
        }

        private static void CloseMenus()
        {
            try
            {
                MethodInfo deselect = typeof(UIPartActionController).GetMethods().FirstOrDefault(mi => mi.Name == "Deselect" && mi.GetParameters().Length == 1);
                deselect?.Invoke(UIPartActionController.Instance, new object[] { true });
            }
            catch (Exception) { /* nothing open */ }
        }
    }
}
