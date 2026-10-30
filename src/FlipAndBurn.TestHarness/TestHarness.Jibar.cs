using System;
using System.Collections;
using System.Linq;
using KSP.UI;
using KSP.UI.Screens;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Kris's Jibar Electric RCS Block (a part config in his GameData, not part of Flip and Burn): proof that it loads, is
    // listed under Control in the editor, and fires on electric charge at the rate its config says. Test copy only: the
    // test craft lives in the test copy's VAB folder, and it's put in orbit with KSP's own "set orbit" (as the cheat menu
    // does) so the thrust is measured in vacuum. Only when asked: "jibarrcs".
    public partial class TestHarness
    {
        private const string JibarRcs = "Jibar.ElectricRCS";   // KSP turns the config's underscores into dots
        private const double ChargePerKn = 200000 / (320 * 9.80665);   // the Side Thruster GIANT's, in vacuum

        private IEnumerator ProofJibarRcs()
        {
            // ---- 1. loaded
            AvailablePart ap = PartLoader.getPartInfoByName(JibarRcs);
            PartResourceDefinition plasma = PartResourceLibrary.Instance.GetDefinition("JibarPlasma");
            Log($"jibar rcs: part loaded {ap != null}: title '{ap?.title}', category {ap?.category}, tech {ap?.TechRequired}, cost {ap?.cost}; " +
                $"plasma resource {(plasma != null ? $"density {plasma.density} t/unit, flow {plasma.resourceFlowMode}" : "missing")}");
            if (ap == null || plasma == null || ap.category != PartCategories.Control)
            {
                Fail("jibarrcs", "the part or its resource didn't load as expected");
                failed = true;
                yield break;
            }

            // ---- 2. the editor's parts list, Control
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
                if (Time.realtimeSinceStartup > until) { Fail("jibarrcs", "the editor didn't open"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            yield return new WaitForSecondsRealtime(5f);
            PartCategorizer.Category control = PartCategorizer.Instance.subcategoryFunctionControl;
            control?.button?.activeButton?.SetState(UIRadioButton.State.True, UIRadioButton.CallType.USER, null);
            yield return new WaitForSecondsRealtime(3f);
            bool listed = EditorPartList.Instance != null &&
                          EditorPartList.Instance.GetComponentsInChildren<EditorPartIcon>(true).Any(i => i.partInfo != null && i.partInfo.name == JibarRcs);
            Log($"jibar rcs: editor open, Control selected ({control?.button?.categoryName}); Jibar Electric RCS Block in the parts list: {listed}");

            // For the picture: the Control list narrowed to RCS parts (an editor filter, removed afterwards), so the Jibar block
            // sits beside the stock RV-105 it's built on; its icon's place on screen is logged for a close-up.
            const string onlyRcs = "flipandburn-test-rcs-only";
            EditorPartList.Instance.ExcludeFilters.AddFilter(new EditorPartListFilter<AvailablePart>(onlyRcs,
                ap2 => ap2.title.IndexOf("RCS", StringComparison.OrdinalIgnoreCase) >= 0 || ap2.name == JibarRcs));
            EditorPartList.Instance.Refresh();
            yield return new WaitForSecondsRealtime(3f);
            EditorPartIcon[] icons = EditorPartList.Instance.GetComponentsInChildren<EditorPartIcon>(false)
                .Where(i => i.partInfo != null && i.gameObject.activeInHierarchy).ToArray();
            Log($"jibar rcs: Control list narrowed to RCS parts: {string.Join(", ", icons.Select(i => i.partInfo.title))}");
            EditorPartIcon mine = icons.FirstOrDefault(i => i.partInfo.name == JibarRcs);
            if (mine != null)
            {
                var corners = new Vector3[4];
                ((RectTransform)mine.transform).GetWorldCorners(corners);
                Camera ui = UIMasterController.Instance.uiCamera;
                Vector2 lo = RectTransformUtility.WorldToScreenPoint(ui, corners[0]), hi = RectTransformUtility.WorldToScreenPoint(ui, corners[2]);
                Log($"jibar rcs: icon on screen (from the top left): x {lo.x:F0}-{hi.x:F0}, y {Screen.height - hi.y:F0}-{Screen.height - lo.y:F0} of {Screen.width}x{Screen.height}");
            }

            yield return Shot("jibar-1-parts-list.png", null);
            EditorPartList.Instance.ExcludeFilters.RemoveFilter(onlyRcs);
            EditorPartList.Instance.Refresh();

            // ---- 3. the test craft, put straight into orbit. (Launching it from the pad stalled this install twice: KSP
            // went silent for minutes at the pad.) Fly a ship in the test copy, add the craft 700 km above Kerbin with KSP's
            // own ProtoVessel calls (as Contract Configurator spawns vessels), and switch to it.
            var host = new Flown();
            yield return FlyFromSave(SaveFile, ElectricShip, host, false);
            if (!host.Ok) yield break;
            yield return new WaitForSecondsRealtime(3f);
            string craft = KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/Jibar RCS Test.craft";
            CelestialBody kerbin = FlightGlobals.Bodies.First(b => b.bodyName == "Kerbin");
            ProtoVessel spawned;
            try
            {
                spawned = SpawnInOrbit(craft, "Jibar RCS Test", kerbin, 700_000);
            }
            catch (Exception e)
            {
                Fail("jibarrcs", "couldn't put the test craft in orbit: " + e);
                failed = true;
                yield break;
            }

            until = Time.realtimeSinceStartup + 60;
            while (spawned?.vesselRef == null)
            {
                if (Time.realtimeSinceStartup > until) { Fail("jibarrcs", "the test craft didn't appear"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            FlightGlobals.ForceSetActiveVessel(spawned.vesselRef);
            until = Time.realtimeSinceStartup + 120;
            while (!(FlightGlobals.ActiveVessel == spawned.vesselRef && spawned.vesselRef.loaded && !spawned.vesselRef.packed))
            {
                if (Time.realtimeSinceStartup > until) { Fail("jibarrcs", "couldn't switch to the test craft"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            Vessel v = FlightGlobals.ActiveVessel;
            yield return new WaitForSecondsRealtime(5f);
            ModuleRCS rcs = v.FindPartModulesImplementing<ModuleRCS>().FirstOrDefault(m => m.part.partInfo.name == JibarRcs);
            Log($"jibar rcs: in orbit: {v.situation} around {v.mainBody.bodyName}, altitude {v.altitude / 1000:F1} km; parts {v.parts.Count}; " +
                $"block found {rcs != null}; electric charge {Amount(v, "ElectricCharge"):F1}, plasma {Amount(v, "JibarPlasma"):F1}; mass {v.totalMass * 1000:F1} kg");
            if (rcs == null) { Fail("jibarrcs", "the block isn't on the test craft"); failed = true; yield break; }

            // ---- 4. fire: RCS on, translate along each axis in turn, keep the one that uses the block most
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            Vector3 cmd = Vector3.zero;
            FlightInputCallback fly = s => { s.X = cmd.x; s.Y = cmd.y; s.Z = cmd.z; };
            v.OnFlyByWire += fly;
            Vector3 best = Vector3.forward;
            double bestThrust = -1;
            foreach (Vector3 axis in new[] { Vector3.right, Vector3.up, Vector3.forward, -Vector3.right, -Vector3.up, -Vector3.forward })
            {
                cmd = axis;
                yield return new WaitForSecondsRealtime(1f);
                double t = rcs.thrustForces.Sum();
                Log($"jibar rcs: translate ({axis.x:F0},{axis.y:F0},{axis.z:F0}): block thrust {t:F3} kN");
                if (t > bestThrust) { bestThrust = t; best = axis; }
                cmd = Vector3.zero;
                yield return new WaitForSecondsRealtime(1f);
            }

            cmd = Vector3.zero;
            v.OnFlyByWire -= fly;
            yield return new WaitForSecondsRealtime(2f);

            // ---- 5. steady firing, measured
            var fired = new CaptureResult();
            yield return FireBlock("first firing", best, "jibar-2-firing.png", fired);
            if (!fired.Ok) yield break;

            // ---- 6. idle: once the plasma is full the converter stops, so the only draw is the probe core's own
            yield return IdleCheck("idle after firing");
            if (failed) yield break;

            // ---- 7. after time warp (on rails, x100 for 10 real seconds)
            v = FlightGlobals.ActiveVessel;
            double ecBefore = Amount(v, "ElectricCharge"), utBefore = Planetarium.GetUniversalTime();
            TimeWarp.SetRate(4, true);
            yield return new WaitForSecondsRealtime(10f);
            TimeWarp.SetRate(0, true);
            until = Time.realtimeSinceStartup + 60;
            while (TimeWarp.CurrentRate > 1.01f || v.packed)
            {
                if (Time.realtimeSinceStartup > until) break;
                yield return new WaitForSecondsRealtime(0.5f);
            }

            double warped = Planetarium.GetUniversalTime() - utBefore;
            Log($"jibar rcs: time warp: {warped:F0} game s; electric charge {ecBefore:F1} -> {Amount(v, "ElectricCharge"):F1} " +
                $"({(ecBefore - Amount(v, "ElectricCharge")) / warped:F3} a second, the probe core draws {CoreDraw(v):F3}); plasma {Amount(v, "JibarPlasma"):F1}");
            yield return new WaitForSecondsRealtime(3f);
            var afterWarp = new CaptureResult();
            yield return FireBlock("after time warp", best, null, afterWarp);
            if (!afterWarp.Ok) yield break;

            // ---- 8. after a save and a reload (a save of its own in the test copy, deleted afterwards)
            const string check = "jibar-rcs-check";
            yield return IdleCheck("before the save");
            if (failed) yield break;
            GamePersistence.SaveGame(check, SaveFolder, SaveMode.OVERWRITE);
            yield return new WaitForSecondsRealtime(2f);
            Vessel old = FlightGlobals.ActiveVessel;
            ConfigNode saved = GamePersistence.LoadSFSFile(check, SaveFolder);
            Game game2 = saved == null ? null : GamePersistence.LoadGameCfg(saved, check, true, false);
            int index = game2?.flightState?.protoVessels.FindIndex(pv => pv.vesselName == "Jibar RCS Test") ?? -1;
            if (index < 0) { Fail("jibarrcs", "the save didn't have the test craft"); failed = true; yield break; }
            HighLogic.CurrentGame = game2;
            GamePersistence.UpdateScenarioModules(game2);
            GameEvents.onGameStatePostLoad.Fire(saved);
            HighLogic.SaveFolder = SaveFolder;
            Log($"jibar rcs: saved '{check}' and loading it again (vessel index {index})");
            FlightDriver.StartAndFocusVessel(check, index);
            until = Time.realtimeSinceStartup + 300;
            while (true)
            {
                Vessel a = HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (a != null && !ReferenceEquals(a, old) && a.vesselName == "Jibar RCS Test" && a.loaded && !a.packed) break;
                if (Time.realtimeSinceStartup > until) { Fail("jibarrcs", "the save didn't load"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            yield return new WaitForSecondsRealtime(5f);
            v = FlightGlobals.ActiveVessel;
            Log($"jibar rcs: loaded: block found {v.FindPartModulesImplementing<ModuleRCS>().Any(m => m.part.partInfo.name == JibarRcs)}; " +
                $"electric charge {Amount(v, "ElectricCharge"):F1}, plasma {Amount(v, "JibarPlasma"):F1}");
            yield return IdleCheck("after the reload");
            if (failed) yield break;
            var afterLoad = new CaptureResult();
            yield return FireBlock("after save and reload", best, null, afterLoad);
            try
            {
                System.IO.File.Delete(KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/" + check + ".sfs");
            }
            catch (Exception)
            {
                // left in the test copy's folder: harmless
            }

            if (afterLoad.Ok) Log("jibar rcs: WORKS AFTER TIME WARP AND AFTER SAVE AND RELOAD; IDLE DRAWS NOTHING BEYOND THE PROBE CORE");
        }

        // Fire the block along one axis for 10 game seconds (or as given): the electric charge used, the plasma, the module's thrust,
        // and the push measured. One block on one side spins this small craft, so the push turns with it: measured frame
        // by frame (each frame's change of velocity less gravity's; KSP's own accelerometer for comparison).
        private IEnumerator FireBlock(string label, Vector3 axis, string shot, CaptureResult result, double seconds = 10)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            ModuleRCS rcs = v.FindPartModulesImplementing<ModuleRCS>().FirstOrDefault(m => m.part.partInfo.name == JibarRcs);
            if (rcs == null) { Fail("jibarrcs", $"{label}: the block isn't on the active ship"); failed = true; yield break; }
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);
            v.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
            Vector3 cmd = axis;
            FlightInputCallback fly = st => { st.X = cmd.x; st.Y = cmd.y; st.Z = cmd.z; };
            v.OnFlyByWire += fly;
            yield return new WaitForSecondsRealtime(2f);
            double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, "ElectricCharge"), pl0 = Amount(v, "JibarPlasma");
            double thrustSum = 0, accelSum = 0, feltSum = 0, spinSum = 0, mass = v.totalMass;
            int samples = 0;
            Vector3d prevVel = v.obt_velocity;
            double prevUT = ut0;
            while (Planetarium.GetUniversalTime() - ut0 < seconds)
            {
                yield return new WaitForFixedUpdate();
                double ut = Planetarium.GetUniversalTime();
                Vector3d vel = v.obt_velocity;
                if (ut > prevUT)
                {
                    Vector3d a = (vel - prevVel) / (ut - prevUT) - FlightGlobals.getGeeForceAtPosition(v.CoMD);
                    accelSum += a.magnitude;
                    feltSum += v.perturbation.magnitude;
                    spinSum += v.angularVelocityD.magnitude;
                    thrustSum += rcs.thrustForces.Sum();
                    samples++;
                }

                prevVel = vel;
                prevUT = ut;
            }

            double ut1 = Planetarium.GetUniversalTime(), ec1 = Amount(v, "ElectricCharge"), pl1 = Amount(v, "JibarPlasma");
            lastPlasmaStart = pl0;
            lastPlasmaEnd = pl1;
            double dt = ut1 - ut0, n = Math.Max(1, samples), thrust = thrustSum / n, accel = accelSum / n, felt = feltSum / n;
            double spin = spinSum / n * 180 / Math.PI;
            double ecRate = (ec0 - ec1) / dt, expected = ChargePerKn * thrust;
            if (shot != null)
            {
                Frame(v, 0.2f, 1.2f, 2.5f);
                yield return new WaitForSecondsRealtime(0.5f);
                yield return Shot(shot, null);
            }

            cmd = Vector3.zero;
            v.OnFlyByWire -= fly;
            Log($"jibar rcs: {label}: fired ({axis.x:F0},{axis.y:F0},{axis.z:F0}) for {dt:F2} game s: module thrust {thrust:F3} kN; measured acceleration {accel:F4} m/s^2 " +
                $"x mass {mass * 1000:F1} kg = {accel * mass:F3} kN (KSP's accelerometer {felt:F4} m/s^2 = {felt * mass:F3} kN; the craft spinning {spin:F0} deg/s); " +
                $"electric charge {ec0:F1} -> {ec1:F1}: {ecRate:F2} per second (expected {ChargePerKn:F2} x {thrust:F3} kN = {expected:F2}); plasma {pl0:F1} -> {pl1:F1}");
            result.Ok = thrust > 0.1 && Math.Abs(ecRate - expected) / expected < 0.05 && Math.Abs(accel * mass - thrust) / thrust < 0.10;
            Log($"jibar rcs: {label}: {(result.Ok ? "FIRES ON ELECTRIC CHARGE AS CONFIGURED" : "NOT AS CONFIGURED")}");
            if (!result.Ok) { Fail("jibarrcs", $"{label}: the block didn't fire as configured"); failed = true; }
            yield return new WaitForSecondsRealtime(2f);
        }

        // With the thrusters idle and the plasma full: the electric charge used over 30 game seconds, against the probe
        // core's own draw. The converter must add nothing.
        private IEnumerator IdleCheck(string label)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            yield return new WaitForSecondsRealtime(3f);   // the plasma tops up after any firing
            double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, "ElectricCharge"), pl0 = Amount(v, "JibarPlasma");
            while (Planetarium.GetUniversalTime() - ut0 < 30) yield return new WaitForFixedUpdate();
            double dt = Planetarium.GetUniversalTime() - ut0, ec1 = Amount(v, "ElectricCharge"), pl1 = Amount(v, "JibarPlasma");
            double rate = (ec0 - ec1) / dt, core = CoreDraw(v), extra = rate - core;
            string status = string.Join(", ", v.FindPartModulesImplementing<ModuleResourceConverter>().Select(c => $"{c.ConverterName}: {c.status}"));
            Log($"jibar rcs: {label}: idle for {dt:F1} game s: electric charge {ec0:F2} -> {ec1:F2}, {rate:F4} a second; the probe core draws {core:F4}; " +
                $"so the block draws {extra:F4} a second; plasma {pl0:F1} -> {pl1:F1}; converter {status}");
            if (Math.Abs(extra) > 0.01 || pl1 < 199.9) { Fail("jibarrcs", $"{label}: the idle block draws electric charge"); failed = true; }
        }

        // What the probe cores on the ship draw of electric charge a second (their ModuleCommand's rate).
        private static double CoreDraw(Vessel v) =>
            v.FindPartModulesImplementing<ModuleCommand>().Sum(c => c.resHandler.inputResources.Where(r => r.name == "ElectricCharge").Sum(r => r.rate));

        // A craft file as a new vessel in a circular orbit: its parts built, saved as part nodes, then added to the game.
        private static ProtoVessel SpawnInOrbit(string craftPath, string name, CelestialBody body, double altitude)
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

            var orbit = new Orbit(0, 0, body.Radius + altitude, 0, 0, 0, Planetarium.GetUniversalTime(), body);
            ConfigNode node = ProtoVessel.CreateVesselNode(name, VesselType.Probe, orbit, 0, parts);
            Log($"jibar rcs: adding '{name}' ({parts.Length} parts) in a {altitude / 1000:F0} km orbit of {body.bodyName}");
            return HighLogic.CurrentGame.AddVessel(node);
        }

        private static double Amount(Vessel v, string resource) =>
            v.parts.Sum(p => p.Resources.Get(resource)?.amount ?? 0);
    }
}
