using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Kris's Jibar Electric RCS Block under TweakScale: one Scale row, the part stays visible at 50 %, 100 % and 200 %,
    // and its thrust, converter and plasma store scale as expected. Then the 50 % and 200 % blocks fired in orbit.
    // Test copy only (the two scaled craft are saved in its VAB folder and deleted afterwards). Only when asked:
    // "jibarscale".
    public partial class TestHarness
    {
        private double lastPlasmaStart, lastPlasmaEnd;

        private IEnumerator ProofJibarScale()
        {
            // ---- 1. the VAB, with the test craft
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
            while (!(HighLogic.LoadedSceneIsEditor && EditorLogic.fetch != null))
            {
                if (Time.realtimeSinceStartup > until) { Fail("jibarscale", "the editor didn't open"); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(0.5f);
            }

            yield return new WaitForSecondsRealtime(5f);
            string ships = KSPUtil.ApplicationRootPath + "saves/" + SaveFolder + "/Ships/VAB/";
            EditorLogic.LoadShipFromFile(ships + "Jibar RCS Test.craft");
            yield return new WaitForSecondsRealtime(3f);
            Part block = EditorLogic.fetch.ship?.parts.FirstOrDefault(p => p.partInfo.name == JibarRcs);
            if (block == null) { Fail("jibarscale", "the block isn't on the test craft in the editor"); failed = true; yield break; }

            // ---- 2. TweakScale on the block: one module, one Scale row
            PartModule[] ts = block.Modules.Cast<PartModule>().Where(m => m.moduleName == "TweakScale").ToArray();
            var modVariants = block.Modules.GetModule<ModulePartVariants>();
            Log($"jibar scale: TweakScale modules on the block: {ts.Length}; variants: " +
                (modVariants == null ? "none" : string.Join(", ", modVariants.variantList.Select(x => $"{x.DisplayName} (mass {x.Mass}, cost {x.Cost})"))));
            if (ts.Length != 1) { Fail("jibarscale", $"{ts.Length} TweakScale modules on the block"); failed = true; yield break; }
            PartModule scaler = ts[0];
            bool slider = scaler.Fields["tweakScale"].guiActiveEditor, chooser = scaler.Fields["tweakName"].guiActiveEditor;
            FieldInfo tweakScale = scaler.GetType().GetField("tweakScale");
            Log($"jibar scale: Scale rows shown: slider {slider}, chooser {chooser}; type free {Get(scaler, "isFreeScale")}, " +
                $"defaultScale {Get(scaler, "defaultScale")}, now {tweakScale.GetValue(scaler)}");
            if (!(slider ^ chooser)) { Fail("jibarscale", "the block doesn't show exactly one Scale row"); failed = true; yield break; }

            // ---- 3. 100 %, 50 %, 200 %: set the slider's own field (TweakScale's Update rescales the part, as when dragged)
            var baseline = new ScaleReading();
            var readings = new Dictionary<float, ScaleReading>();
            bool ok = true;
            foreach (float pct in new[] { 100f, 50f, 200f })
            {
                tweakScale.SetValue(scaler, pct);
                yield return new WaitForSecondsRealtime(2f);
                ScaleReading r = Read(block);
                readings[pct] = r;
                if (pct == 100f) baseline = r;
                double s = pct / 100.0, s25 = Math.Pow(s, 2.5), s3 = s * s * s;
                bool right = Near(r.Thrust, baseline.Thrust * s25) && Near(r.ChargeIn, baseline.ChargeIn * s25) &&
                             Near(r.PlasmaOut, baseline.PlasmaOut * s25) && Near(r.PlasmaMax, baseline.PlasmaMax * s3) &&
                             r.Visible && Math.Abs(r.Size / baseline.Size - s) < 0.1 * s;
                Log($"jibar scale: at {pct}%: thrust {r.Thrust:F4} kN (expected {baseline.Thrust * s25:F4}); converter takes {r.ChargeIn:F2} " +
                    $"electric charge a second (expected {baseline.ChargeIn * s25:F2}) and makes {r.PlasmaOut:F2} plasma (expected {baseline.PlasmaOut * s25:F2}); " +
                    $"plasma store {r.PlasmaMax:F1} (expected {baseline.PlasmaMax * s3:F1}); firing it would use {ChargePerKn * r.Thrust:F2} electric charge a second; " +
                    $"mass {r.Mass * 1000:F2} kg; visible {r.Visible} ({r.Renderers} renderers on), size {r.Size:F3} m ({r.Size / baseline.Size:F2}x), " +
                    $"model scale {r.ModelScale:F3}: {(right ? "AS EXPECTED" : "NOT AS EXPECTED")}");
                ok &= right;
            }

            // ---- 4. the variants at 200 %: each still picks, stays visible, weighs the same
            if (modVariants != null)
            {
                foreach (PartVariant pv in modVariants.variantList.ToArray())
                {
                    modVariants.SetVariant(pv.Name);
                    yield return new WaitForSecondsRealtime(1f);
                    ScaleReading r = Read(block);
                    Log($"jibar scale: at 200%, variant {pv.DisplayName}: visible {r.Visible}, size {r.Size:F3} m, mass {r.Mass * 1000:F2} kg, thrust {r.Thrust:F4} kN");
                    ok &= r.Visible && Near(r.Thrust, readings[200f].Thrust);
                }

                modVariants.SetVariant(modVariants.variantList[0].Name);
                yield return new WaitForSecondsRealtime(1f);
            }

            // ---- 5. the right-click menu at 200 %
            UIPartActionController.Instance.SpawnPartActionWindow(block);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("jibar-scale-menu.png", null);
            UIPartActionController.Instance.Deselect(true);

            if (!ok) { Fail("jibarscale", "the block didn't scale as expected in the editor"); failed = true; yield break; }

            // ---- 6. the scaled craft saved in the test copy (50 % and 200 %)
            var saved = new List<string>();
            foreach (float pct in new[] { 50f, 200f })
            {
                tweakScale.SetValue(scaler, pct);
                yield return new WaitForSecondsRealtime(2f);
                string name = $"Jibar RCS Test {pct:0}";
                EditorLogic.fetch.ship.shipName = name;
                EditorLogic.fetch.ship.SaveShip().Save(ships + name + ".craft");
                saved.Add(ships + name + ".craft");
            }

            // ---- 7. both in orbit, each fired
            var host = new Flown();
            yield return FlyFromSave(SaveFile, ElectricShip, host, false);
            if (!host.Ok) yield break;
            yield return new WaitForSecondsRealtime(3f);
            CelestialBody kerbin = FlightGlobals.Bodies.First(b => b.bodyName == "Kerbin");
            foreach ((float pct, double altitude, double seconds) in new[] { (50f, 700_000.0, 10.0), (200f, 900_000.0, 3.0) })
            {
                string name = $"Jibar RCS Test {pct:0}";
                ProtoVessel spawned;
                try
                {
                    spawned = SpawnInOrbit(ships + name + ".craft", name, kerbin, altitude);
                }
                catch (Exception e)
                {
                    Fail("jibarscale", "couldn't put the scaled craft in orbit: " + e);
                    failed = true;
                    yield break;
                }

                until = Time.realtimeSinceStartup + 120;
                while (spawned?.vesselRef == null)
                {
                    if (Time.realtimeSinceStartup > until) { Fail("jibarscale", "the scaled craft didn't appear"); failed = true; yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                FlightGlobals.ForceSetActiveVessel(spawned.vesselRef);
                while (!(FlightGlobals.ActiveVessel == spawned.vesselRef && spawned.vesselRef.loaded && !spawned.vesselRef.packed))
                {
                    if (Time.realtimeSinceStartup > until) { Fail("jibarscale", "couldn't switch to the scaled craft"); failed = true; yield break; }
                    yield return new WaitForSecondsRealtime(0.5f);
                }

                yield return new WaitForSecondsRealtime(5f);
                Vessel v = FlightGlobals.ActiveVessel;
                Part flown = v.parts.FirstOrDefault(p => p.partInfo.name == JibarRcs);
                if (flown == null) { Fail("jibarscale", "the block isn't on the scaled craft"); failed = true; yield break; }
                ScaleReading f = Read(flown);
                ScaleReading e2 = readings[pct];
                bool same = Near(f.Thrust, e2.Thrust) && Near(f.ChargeIn, e2.ChargeIn) && Near(f.PlasmaMax, e2.PlasmaMax) && f.Visible;
                Log($"jibar scale: in orbit at {pct}%: thrust {f.Thrust:F4} kN, converter {f.ChargeIn:F2} a second, plasma {Amount(v, "JibarPlasma"):F1} of {f.PlasmaMax:F1}, " +
                    $"visible {f.Visible}, size {f.Size:F3} m: {(same ? "SAME AS THE EDITOR" : "DIFFERENT FROM THE EDITOR")}");
                if (!same) { Fail("jibarscale", $"the {pct}% block loaded differently in flight"); failed = true; yield break; }

                if (pct == 200f)
                {
                    Frame(v, 0.2f, 1.2f, 0.6f);
                    yield return new WaitForSecondsRealtime(1f);
                    yield return Shot("jibar-scale-200.png", null);
                }

                var fired = new CaptureResult();
                yield return FireBlock($"at {pct}%", Vector3.up, null, fired, seconds);
                if (!fired.Ok) yield break;
                bool enough = lastPlasmaEnd >= 0.9 * f.PlasmaMax;
                Log($"jibar scale: at {pct}%: plasma while firing {lastPlasmaStart:F1} -> {lastPlasmaEnd:F1} of {f.PlasmaMax:F1}: " +
                    (enough ? "THE CONVERTER KEPT UP" : "RAN SHORT OF PLASMA"));
                if (!enough) { Fail("jibarscale", $"the {pct}% block ran short of plasma"); failed = true; yield break; }
            }

            foreach (string path in saved)
            {
                try { System.IO.File.Delete(path); }
                catch (Exception) { /* left in the test copy's VAB folder: harmless */ }
            }

            Log("jibar scale: SCALES WITH TWEAKSCALE: ONE SCALE ROW, VISIBLE AT 50/100/200%, THRUST AND CHARGE AS EXPECTED, FIRES AT 50% AND 200%");
        }

        private struct ScaleReading
        {
            public double Thrust, ChargeIn, PlasmaOut, PlasmaMax, Mass, Size, ModelScale;
            public bool Visible;
            public int Renderers;
        }

        private static ScaleReading Read(Part p)
        {
            var r = new ScaleReading
            {
                Thrust = p.Modules.GetModule<ModuleRCS>()?.thrusterPower ?? 0,
                Mass = p.mass + p.GetModuleMass(p.mass),
                PlasmaMax = p.Resources.Get("JibarPlasma")?.maxAmount ?? 0,
            };
            ModuleResourceConverter conv = p.Modules.GetModule<ModuleResourceConverter>();
            r.ChargeIn = conv?.inputList.Where(x => x.ResourceName == "ElectricCharge").Sum(x => x.Ratio) ?? 0;
            r.PlasmaOut = conv?.outputList.Where(x => x.ResourceName == "JibarPlasma").Sum(x => x.Ratio) ?? 0;
            Renderer[] on = p.FindModelComponents<Renderer>().Where(x => x.enabled && x.gameObject.activeInHierarchy && !(x is ParticleSystemRenderer)).ToArray();
            r.Renderers = on.Length;
            if (on.Length > 0)
            {
                Bounds b = on[0].bounds;
                foreach (Renderer x in on) b.Encapsulate(x.bounds);
                r.Size = b.size.magnitude;
            }

            Transform model = p.transform.Find("model");
            r.ModelScale = model != null ? model.localScale.x : double.NaN;
            r.Visible = on.Length > 0 && r.Size > 0.01;
            return r;
        }

        private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.01 * Math.Abs(b) + 1e-6;

        private static object Get(object o, string field) => o.GetType().GetField(field)?.GetValue(o);
    }
}
