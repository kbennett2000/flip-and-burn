using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using CommNet;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Kris's Jibar Tightbeam Antenna (a part config in his GameData, not part of Flip and Burn): proof that it loads, is
    // listed in the editor, connects to Kerbin from Eeloo's farthest distance and from the edge of the system, relays a probe
    // that has only a small stock antenna, and links to a second Tightbeam across the system. Test copy only.
    // Only when asked: "jibartightbeam".
    public partial class TestHarness
    {
        private const string TightbeamPart = "Jibar.TightbeamAntenna", Communotron16 = "longAntenna";
        private const string TightbeamTest = "Jibar Tightbeam Test", SmallAntennaProbe = "Jibar Small Antenna Probe";
        private const double TightbeamPower = 2e12;
        private bool beamOk = true;

        private void BeamCheck(string what, bool ok, string detail)
        {
            Log($"jibar tightbeam: {what}: {(ok ? "AS SET" : "NOT AS SET")}: {detail}");
            if (!ok) beamOk = false;
        }

        private IEnumerator ProofJibarTightbeam()
        {
            // ---- 1. loaded
            AvailablePart ap = PartLoader.getPartInfoByName(TightbeamPart);
            if (ap == null) { Fail("jibartightbeam", TightbeamPart + " didn't load"); failed = true; yield break; }
            Part prefab = ap.partPrefab;
            ModuleDataTransmitter tx = prefab.FindModuleImplementing<ModuleDataTransmitter>();
            Log($"jibar tightbeam: part {TightbeamPart}: '{ap.title}', category {ap.category}, mass {prefab.mass} t, cost {ap.cost}, tech {ap.TechRequired}; " +
                $"modules {string.Join(", ", prefab.Modules.Cast<PartModule>().Select(m => m.moduleName))}");
            BeamCheck("antenna", tx != null && tx.antennaType == AntennaType.RELAY && Math.Abs(tx.antennaPower - TightbeamPower) < 1 && tx.antennaCombinable,
                tx == null ? "no ModuleDataTransmitter" : $"type {tx.antennaType}, power {tx.antennaPower:G6}, combinable {tx.antennaCombinable}");
            if (tx != null) Log("jibar tightbeam: part info: " + tx.GetInfo().Replace("\n", " | "));

            // ---- 2. the editor's tab
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
                if (Time.realtimeSinceStartup > until) { Fail("jibartightbeam", "the editor didn't open"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            yield return new WaitForSecondsRealtime(5f);
            var cat = typeof(PartCategorizer).GetField("subcategoryFunctionCommunication")?.GetValue(PartCategorizer.Instance) as PartCategorizer.Category;
            cat?.button?.activeButton?.SetState(UIRadioButton.State.True, UIRadioButton.CallType.USER, null);
            yield return new WaitForSecondsRealtime(3f);
            var listed = EditorPartList.Instance.GetComponentsInChildren<EditorPartIcon>(true).Where(i => i.partInfo != null).Select(i => i.partInfo.name).ToList();
            BeamCheck($"VAB tab Communication ({cat?.button?.categoryName})", cat != null && listed.Contains(TightbeamPart), $"{TightbeamPart} listed: {listed.Contains(TightbeamPart)}");
            const string filter = "flipandburn-test-jibar-communication";
            EditorPartList.Instance.ExcludeFilters.AddFilter(new EditorPartListFilter<AvailablePart>(filter, a => a.title.StartsWith("Jibar", StringComparison.Ordinal)));
            EditorPartList.Instance.Refresh();
            yield return new WaitForSecondsRealtime(3f);
            yield return Shot("jibar-tightbeam-1-vab-communication.png", null);
            EditorPartList.Instance.ExcludeFilters.RemoveFilter(filter);
            EditorPartList.Instance.Refresh();
            try
            {
                MethodInfo load = typeof(EditorLogic).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .FirstOrDefault(mi => mi.Name == "LoadShipFromFile" && mi.GetParameters().Length == 1 && mi.GetParameters()[0].ParameterType == typeof(string));
                load?.Invoke(load.IsStatic ? null : EditorLogic.fetch, new object[] { CraftPath(TightbeamTest) });
                Log($"jibar tightbeam: the test craft loaded into the VAB: {(load != null ? "yes" : "no LoadShipFromFile")}; parts {EditorLogic.fetch.ship?.parts?.Count}");
            }
            catch (Exception e) { Log("jibar tightbeam: couldn't load the test craft into the VAB: " + (e.InnerException ?? e).Message); }

            yield return new WaitForSecondsRealtime(5f);
            yield return Shot("jibar-tightbeam-2-vab-test-craft.png", null);

            // ---- 3. the save's CommNet settings and the ground stations' reach
            var host = new Flown();
            yield return FlyFromSave(SaveFile, ElectricShip, host, false);
            if (!host.Ok) yield break;
            yield return new WaitForSecondsRealtime(3f);
            CommNetParams cp = HighLogic.CurrentGame.Parameters.CustomParams<CommNetParams>();
            float level = ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation);
            Log($"jibar tightbeam: save mode {HighLogic.CurrentGame.Mode}; CommNet on {HighLogic.CurrentGame.Parameters.Difficulty.EnableCommNet}; range setting {cp.rangeModifier}; " +
                $"ground station setting {cp.DSNModifier}; extra ground stations {cp.enableGroundStations}; occlusion vacuum {cp.occlusionMultiplierVac}; Tracking Station level {level * 2 + 1:F0} of 3");
            foreach (float l in new[] { 0f, 0.5f, 1f })
            {
                double dsn = GameVariables.Instance.GetDSNRange(l);
                Log($"jibar tightbeam: Tracking Station level {l * 2 + 1:F0}: ground stations' power {dsn:G4}; the game's range to a Tightbeam " +
                    $"{CommNetScenario.RangeModel.GetMaximumRange(TightbeamPower, dsn) / 1e9:F1} Gm at this save's range setting, " +
                    $"{1.5 * Math.Sqrt(TightbeamPower * dsn) / 1e9:F1} Gm at 1.5");
            }

            Log($"jibar tightbeam: the game's range, Tightbeam to Tightbeam {CommNetScenario.RangeModel.GetMaximumRange(TightbeamPower, TightbeamPower) / 1e9:F1} Gm; " +
                $"Communotron 16 to Tightbeam {CommNetScenario.RangeModel.GetMaximumRange(5e5, TightbeamPower) / 1e9:F3} Gm; " +
                $"Communotron 16 to the ground stations {CommNetScenario.RangeModel.GetMaximumRange(5e5, GameVariables.Instance.GetDSNRange(level)) / 1e9:F3} Gm");

            // ---- 4. Tightbeam probes around the Sun: at Eeloo's farthest from Kerbin (146.1 Gm in this install, Eeloo being
            // a moon of Sarnus), at Plock's farthest (689 Gm, the edge of the system), and one far across from that; a probe with
            // only a Communotron 16 beside the first. Each placed off the line through the Sun so the Sun doesn't block it.
            CelestialBody kerbin = FlightGlobals.Bodies.First(b => b.bodyName == "Kerbin");
            Orbit ko = kerbin.orbit;
            double now = Planetarium.GetUniversalTime(), rk = ko.semiMajorAxis;
            Orbit Around(double radius, double degreesFromKerbin, double extraMetres = 0) =>
                new Orbit(ko.inclination, 0, radius, ko.LAN, ko.argumentOfPeriapsis,
                    ko.meanAnomaly + degreesFromKerbin * Math.PI / 180 + extraMetres / radius, now, ko.referenceBody);
            double RadiusFor(double fromKerbin, double degrees)
            {
                double c = Math.Cos(degrees * Math.PI / 180);
                return rk * c + Math.Sqrt(fromKerbin * fromKerbin - rk * rk * (1 - c * c));
            }

            double rA = RadiusFor(146.1e9, 170), rC = RadiusFor(689e9, 100);
            ProtoVessel pA, pB, pC, pD;
            try
            {
                pA = SpawnOnOrbit(CraftPath(TightbeamTest), "Tightbeam at Eeloo distance", Around(rA, 170));
                pB = SpawnOnOrbit(CraftPath(SmallAntennaProbe), "Small antenna probe", Around(rA, 170, 400));
                pC = SpawnOnOrbit(CraftPath(TightbeamTest), "Tightbeam at Plock distance", Around(rC, 100));
                pD = SpawnOnOrbit(CraftPath(TightbeamTest), "Tightbeam across the system", Around(675e9, 270));
            }
            catch (Exception e) { Fail("jibartightbeam", "couldn't place the test probes: " + e); failed = true; yield break; }

            yield return SwitchTo(pA, "jibartightbeam");
            if (failed) yield break;
            Vessel vA = pA.vesselRef, vB = pB.vesselRef, vC = pC.vesselRef, vD = pD.vesselRef;
            foreach (Vessel v in new[] { vA, vB, vC, vD })
                Log($"jibar tightbeam: {v.vesselName}: {v.mainBody.bodyName} orbit {v.orbit.semiMajorAxis / 1e9:F2} Gm, {DistanceFromKerbin(v, kerbin) / 1e9:F2} Gm from Kerbin; " +
                    $"parts {string.Join(", ", v.parts.Count > 0 ? v.parts.Select(p => p.partInfo.name) : v.protoVessel.protoPartSnapshots.Select(p => p.partName))}");
            yield return new WaitForSecondsRealtime(10f);

            // A ground station can be behind Kerbin for a moment; if so, let time run (at most 7 game hours) until one is in sight.
            if (!(vA.Connection?.IsConnected ?? false))
            {
                Log("jibar tightbeam: Eeloo-distance probe not connected yet; letting time run");
                TimeWarp.SetRate(5, true);
                double end = Planetarium.GetUniversalTime() + 7 * 3600;
                while (!(vA.Connection?.IsConnected ?? false) && Planetarium.GetUniversalTime() < end) yield return new WaitForSecondsRealtime(0.5f);
                TimeWarp.SetRate(0, true);
                yield return new WaitForSecondsRealtime(5f);
            }

            CommNetVessel cA = vA.Connection;
            Describe(vA, kerbin);
            BeamCheck("connects to Kerbin from Eeloo's farthest distance", cA != null && cA.IsConnected && cA.ControlPath.Count == 1 && Ends(cA.ControlPath, cA.Comm, true),
                $"{DistanceFromKerbin(vA, kerbin) / 1e9:F2} Gm from Kerbin, connected {cA?.IsConnected}, hops {cA?.ControlPath?.Count}, signal {cA?.SignalStrength:P1}");
            Frame(vA, 0.25f, 0.8f, 1.6f);
            Part dish = vA.parts.FirstOrDefault(p => p.partInfo.name == TightbeamPart);
            if (dish != null) OpenMenu(dish);
            yield return new WaitForSecondsRealtime(3f);
            yield return Shot("jibar-tightbeam-3-eeloo-distance.png", null);
            CloseMenus();
            yield return CommMapShot(vA.mapObject, (float)(DistanceFromKerbin(vA, kerbin) * 0.75 / 6000), "Path", "jibar-tightbeam-4-eeloo-distance-map.png");

            // ---- 5. the small-antenna probe: only reaches Kerbin through the Tightbeam
            yield return SwitchTo(pB, "jibartightbeam");
            if (failed) yield break;
            vB = pB.vesselRef;
            foreach (ModuleDeployableAntenna d in vB.FindPartModulesImplementing<ModuleDeployableAntenna>()) d.Extend();
            yield return new WaitForSecondsRealtime(12f);
            CommNetVessel cB = vB.Connection;
            Describe(vB, kerbin);
            bool viaBeam = cB != null && cB.IsConnected && cB.ControlPath.Count >= 2 && Ends(cB.ControlPath, cB.Comm, false)
                           && cB.ControlPath.Any(l => Has(l, cB.Comm) && Has(l, vA.Connection.Comm)) && cB.ControlPath.Any(l => Has(l, vA.Connection.Comm) && (l.a.isHome || l.b.isHome));
            BeamCheck("a Communotron 16 probe connects to Kerbin through it", viaBeam,
                $"{Vector3d.Distance(vB.GetWorldPos3D(), vA.GetWorldPos3D()):F0} m from the Tightbeam probe, connected {cB?.IsConnected}, hops {cB?.ControlPath?.Count}, signal {cB?.SignalStrength:P1}");
            Frame(vB, 0.25f, 0.5f, 2.5f);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("jibar-tightbeam-5-small-antenna-probe.png", null);
            yield return CommMapShot(vB.mapObject, (float)(DistanceFromKerbin(vB, kerbin) * 0.75 / 6000), "Path", "jibar-tightbeam-6-relayed-path-map.png");

            // ---- 6. the edge of the system, and Tightbeam to Tightbeam across it. CommNet sends a signal by the strongest
            // route, and the test copy has other relays (Kris's ships, and the Eeloo-distance Tightbeam), so the route a far
            // probe takes needn't be its direct link (runs 1 and 3). What is checked here is the links CommNet built: a direct
            // link from each far Tightbeam to a ground station on Kerbin, and one between the two of them.
            Describe(vC, kerbin);
            Describe(vD, kerbin);
            CommNode nC = vC.Connection?.Comm, nD = vD.Connection?.Comm;
            foreach ((string what, CommNode n, Vessel v) in new[] { ("Plock's farthest distance", nC, vC), ("across from it", nD, vD) })
            {
                var home = n == null ? new System.Collections.Generic.List<CommNode>() : n.Keys.Where(k => k.isHome).ToList();
                CommNode best = home.OrderByDescending(k => n[k].signalStrength).FirstOrDefault();
                Log($"jibar tightbeam: {v.vesselName}: direct links {n?.Count}: " + (n == null ? "none" : string.Join(" ; ", n.Keys.Select(k =>
                    $"{k.displayName}{(k.isHome ? " (ground station)" : "")} {Vector3d.Distance(k.position, n.position) / 1e9:F1} Gm, strength {n[k].signalStrength:P1}"))));
                BeamCheck($"a direct link to Kerbin from {what}", best != null,
                    best == null ? $"{DistanceFromKerbin(v, kerbin) / 1e9:F1} Gm from Kerbin, no direct link to a ground station"
                        : $"{DistanceFromKerbin(v, kerbin) / 1e9:F1} Gm from Kerbin, linked straight to {best.displayName} ({Vector3d.Distance(best.position, n.position) / 1e9:F1} Gm, " +
                          $"strength {n[best].signalStrength:P1}); {home.Count} ground stations in reach; connected {v.Connection.IsConnected}");
            }

            double apart = Vector3d.Distance(Where(vC), Where(vD));
            bool linked = nC != null && nD != null && nC.ContainsKey(nD);
            BeamCheck("two Tightbeams link across the system", linked && apart > 1.3e12,
                $"{apart / 1e9:F1} Gm apart, direct link {linked}{(linked ? $", strength {nC[nD].signalStrength:P1}" : "")}");
            yield return CommMapShot(FlightGlobals.Bodies.First(b => b.referenceBody == b || b.orbit == null).MapObject, 2.2e8f, "Network", "jibar-tightbeam-7-system-map.png");

            Log($"jibar tightbeam: {(beamOk ? "ALL AS SET" : "SOME NOT AS SET (see above)")}");
            if (!beamOk) { Fail("jibartightbeam", "some checks didn't come out as set"); failed = true; }
        }

        private static string CraftPath(string craft) => KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/" + craft + ".craft";

        // From the orbits, so it holds for ships that aren't loaded too.
        private static Vector3d Where(Vessel v) => v.loaded ? v.GetWorldPos3D() : v.orbit.getPositionAtUT(Planetarium.GetUniversalTime());

        private static double DistanceFromKerbin(Vessel v, CelestialBody kerbin) => Vector3d.Distance(Where(v), kerbin.position);

        private static bool Has(CommLink l, CommNode n) => l.a == n || l.b == n;

        // The path starts at the vessel and, when "home" is asked, its one link ends at a ground station; otherwise its last
        // link ends at one.
        private static bool Ends(CommPath path, CommNode start, bool direct) =>
            path.Count > 0 && Has(path[0], start) && (path[path.Count - 1].a.isHome || path[path.Count - 1].b.isHome) && (!direct || path.Count == 1);

        private static string PathText(CommPath path) =>
            path == null || path.Count == 0 ? "none" : string.Join(" ; ", path.Select(l =>
                $"{l.a.displayName} <-> {l.b.displayName} {Vector3d.Distance(l.a.position, l.b.position) / 1e9:F4} Gm, strength {l.signalStrength:P1}"));

        private static void Describe(Vessel v, CelestialBody kerbin)
        {
            CommNetVessel c = v.Connection;
            if (c == null) { Log($"jibar tightbeam: {v.vesselName}: no CommNet connection object"); return; }
            Log($"jibar tightbeam: {v.vesselName}: {DistanceFromKerbin(v, kerbin) / 1e9:F3} Gm from Kerbin; connected {c.IsConnected}, signal {c.SignalStrength:P1}, " +
                $"control {c.ControlState}; relay power {c.Comm?.antennaRelay?.power:G4}, transmit power {c.Comm?.antennaTransmit?.power:G4}; path: {PathText(c.ControlPath)}");
        }

        private IEnumerator CommMapShot(MapObject target, float distance, string mode, string file)
        {
            try
            {
                MapView.EnterMapView();
            }
            catch (Exception e) { Log("couldn't open map view: " + e.Message); }

            yield return new WaitForSecondsRealtime(2f);
            try
            {
                FieldInfo f = typeof(CommNetUI).GetField("Mode", BindingFlags.Public | BindingFlags.Static);
                PropertyInfo p = f == null ? typeof(CommNetUI).GetProperty("Mode", BindingFlags.Public | BindingFlags.Static) : null;
                Type t = f?.FieldType ?? p?.PropertyType;
                if (t != null)
                {
                    object value = Enum.Parse(t, mode);
                    if (f != null) f.SetValue(null, value); else p.SetValue(null, value, null);
                }
                else
                    Log("jibar tightbeam: CommNetUI has no Mode; its statics: " + string.Join(", ", typeof(CommNetUI).GetMembers(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name)));
                PlanetariumCamera pc = PlanetariumCamera.fetch;
                pc.maxDistance = Math.Max(pc.maxDistance, distance * 2);
                pc.SetTarget(target);
                pc.SetDistance(distance);
            }
            catch (Exception e) { Log("jibar tightbeam: couldn't frame the map: " + e.Message); }

            yield return new WaitForSecondsRealtime(4f);
            yield return Shot(file, null);
            try { MapView.ExitMapView(); }
            catch (Exception) { /* tidiness only */ }
        }
    }
}
