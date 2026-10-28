using System;
using System.Collections;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 12: the pictures for the player's guide ("docshots"), and the short in-game check that the planner, trip and
    // arrival windows still work after the window changes. Test copy only; the cheats are on only while flying and are
    // switched off at the end.
    //  1. the MechJeb menu, and the planner three ways: a coast plan, a full burn that's short (with the coast suggestion),
    //     an acceleration the ship can't reach;
    //  2. a coast trip at 1 g to Duna, fuel counted: speeding up, paused, resumed, coasting, slowing down, arrived (window
    //     and map);
    //  3. a full burn at 3 g with each flip style (infinite propellant: no ship carries that much), aborted after the flip;
    //  4. the planner's boil-off line: the electric charge emptied, the tanks left to boil off for two game hours with the
    //     window open, so the loss it shows is measured, not made up.
    public partial class TestHarness
    {
        // Each part starts from the test copy's quicksave: KSP autosaves to "persistent" during a trip, so a reload of that
        // would put the ship wherever the last trip left it.
        private const string DocSave = "quicksave";

        private IEnumerator ProofDocShots()
        {
            // ---- 1. menu and planner
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, DocSave);
            if (!f.Ok) yield break;
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 1.0;
            module.SelectTripType(TripType.CoastInTheMiddle);
            Frame(f.Ship, 0.30f, 0.9f, 1.4f);
            ShowMechJebMenu(f);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            yield return Shot("doc-01-mechjeb-menu.png", f);
            HideMechJebMenu(f);
            // The trip flown next, as the planner shows it before Start.
            yield return PlannerShot(f, 1.0, TripType.CoastInTheMiddle, "doc-02-planner-coast.png");
            yield return PlannerShot(f, 1.0, TripType.FullBurn, "doc-03-planner-coast-suggested.png");
            yield return PlannerShot(f, 4.0, TripType.FullBurn, "doc-04-planner-refused.png");

            // ---- 2. the coast trip, Start to parked at Duna, with the banner shots on the way in
            var t = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, t, DocSave);
            if (!t.Ok) yield break;
            yield return DocCoastTrip(t, bannerShots: true, pause: false);
            if (failed) yield break;

            // ---- 3. both flip styles
            foreach ((int style, string file) in new[] { (0, "doc-12-flip-cut.png"), (1, "doc-13-flip-skew.png") })
            {
                var g = new Flown();
                yield return PlanOnShip(ResumeShip, null, null, g, DocSave);
                if (!g.Ok) yield break;
                yield return DocFlip(g, style, file);
                if (failed) yield break;
            }

            // ---- 4. the boil-off line
            var h = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, h, DocSave);
            if (!h.Ok) yield break;
            yield return DocBoilOff(h);
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
            Log("docs: cheats back off: infinite propellant " + CheatOptions.InfinitePropellant + ", ignore max temperature " + CheatOptions.IgnoreMaxTemperature);
        }

        // The final retakes ("docstart"): the guide's trip again, Start to parked with no pause, showing the realistic
        // reserve; a separate start for the paused and resumed shots; and the boil-off line with the power store emptied too.
        private IEnumerator ProofDocStart()
        {
            var t = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, t, DocSave);
            if (!t.Ok) yield break;
            yield return DocCoastTrip(t, pause: false);
            if (failed) yield break;

            var u = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, u, DocSave);
            if (!u.Ok) yield break;
            yield return DocCoastTrip(u, pause: true);
            if (failed) yield break;

            var h = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, h, DocSave);
            if (!h.Ok) yield break;
            yield return DocBoilOff(h, alsoPowerStore: true);
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
            Log("docs: cheats back off: infinite propellant " + CheatOptions.InfinitePropellant + ", ignore max temperature " + CheatOptions.IgnoreMaxTemperature);
        }

        private IEnumerator PlannerShot(Flown f, double g, TripType trip, string file)
        {
            f.Module.AccelerationG.Val = g;
            f.Module.SelectTripType(trip);
            f.Module.Replan();
            yield return new WaitForSecondsRealtime(3f);   // a few frames: the window re-fits
            Log($"docs: planner at {g} g {trip}: can start {f.Module.Planner.CanStart}; rows " +
                string.Join("; ", f.Module.Planner.TripRows.Concat(f.Module.Planner.ShipRows).Select(r => r.Key + " = " + r.Value)) +
                "; notes " + string.Join(" | ", f.Module.Planner.Notes));
            yield return Shot(file, f);
        }

        // Fuel counted, as a player flies it (the reserve row, the arrival report). Only "ignore max temperature" is on: the
        // Bebop's drive can't keep up 1 g without it, and nothing in the window shows that cheat.
        // pause: Pause and Resume during the speed-up (the paused and resumed shots), then Abort after the resume.
        private IEnumerator DocCoastTrip(Flown f, bool startOnly = false, bool bannerShots = false, bool pause = true)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = true;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 25;
            module.AccelerationG.Val = 1.0;
            module.SelectTripType(TripType.CoastInTheMiddle);
            module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            string problem = module.PressStart();
            if (problem != null) { Fail("docshots", "Start refused: " + problem); failed = true; yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            double start = Planetarium.GetUniversalTime();

            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Accelerate && !pilot.Last.Departing &&
                                         pilot.PointingError < 1 && Planetarium.GetUniversalTime() - start > 600, 900, "docs: speeding up");
            Frame(f.Ship, 0.25f, 2.2f, 1.3f);
            yield return new WaitForSecondsRealtime(2f);
            if (!pause) yield return Shot("doc-05-speeding-up.png", f);
            Log($"docs: speeding up: reserve shown {module.Autopilot.ReserveShare * 100:F2}%; note: {module.Autopilot.ReserveNote ?? "(none)"}");

            if (pause)
            {
                module.PressPause();
                yield return new WaitForSecondsRealtime(4f);
                yield return Shot("doc-06-paused.png", f);
                problem = module.PressResume();
                if (problem != null) { Fail("docshots", "Resume refused: " + problem); failed = true; yield break; }
                yield return new WaitForSecondsRealtime(4f);
                yield return Shot("doc-07-resumed.png", f);
                Log($"docs: after the resume: reserve {module.Autopilot.ReserveShare * 100:F2}%; note: {module.Autopilot.ReserveNote ?? "(none)"}");
                module.PressAbort();
                yield return new WaitForSecondsRealtime(3f);
                yield break;
            }

            // The coast, under warp, once the ship has turned to face its braking direction.
            pilot = module.Autopilot;
            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Coast && !pilot.Last.Correcting &&
                                         TimeWarp.CurrentRate > 10f && pilot.PointingError < 2, 1800, "docs: to the coast");
            Frame(f.Ship, 0.2f, 3.6f, 1.6f);
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot("doc-08-coast.png", f);
            if (startOnly)
            {
                module.PressAbort();
                yield return new WaitForSecondsRealtime(3f);
                yield break;
            }

            double flipEnd = double.NaN;
            yield return Fly(pilot, () =>
            {
                GuidanceCommand c = pilot.Last;
                if (c == null || c.Phase != GuidancePhase.Decelerate) return false;
                if (double.IsNaN(flipEnd)) flipEnd = Planetarium.GetUniversalTime();
                return pilot.PointingError < 1 && !c.Correcting && Planetarium.GetUniversalTime() - flipEnd > 600;
            }, 3600, "docs: to the slow-down");
            Frame(f.Ship, 0.25f, 4.0f, 1.3f);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("doc-09-slowing-down.png", f);

            // Banner candidates: braking hard into Duna, the interface hidden (KSP's own F2), from four sides, so Duna is
            // behind the ship in at least one.
            if (bannerShots)
            {
                yield return Fly(pilot, () => f.Ship.mainBody.bodyName == PlannerTarget && f.Ship.altitude < 2_500_000 && pilot.Last != null &&
                                             pilot.Last.Phase != GuidancePhase.Coast && pilot.PointingError < 2, 3600, "docs: to the banner");
                GameEvents.onHideUI.Fire();
                for (int i = 0; i < 4; i++)
                {
                    Frame(f.Ship, 0.12f, i * (float)Math.PI / 2, 1.1f);
                    yield return new WaitForSecondsRealtime(1.5f);
                    yield return Shot($"doc-banner-{i + 1}.png", f);
                }

                GameEvents.onShowUI.Fire();
                yield return new WaitForSecondsRealtime(1f);
            }

            yield return Fly(pilot, () => false, 3600, "docs: to arrival");
            yield return new WaitForSecondsRealtime(4f);
            Log($"docs: arrival: arrived {pilot.Arrived}; {pilot.StopReason} {pilot.SettingsCheck}");
            if (!pilot.Arrived) { Fail("docshots", "the coast trip didn't arrive: " + pilot.StopReason); failed = true; yield break; }
            Frame(f.Ship, 0.15f, 2.8f, 2.2f);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("doc-11-arrived-window.png", f);
            yield return MapShot(f, "doc-10-arrived-map.png");
        }

        private IEnumerator DocFlip(Flown f, int style, string file)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = true;
            CheatOptions.IgnoreMaxTemperature = true;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 25;
            module.FlipStyleIndex = style;
            module.AccelerationG.Val = 3.0;
            module.SelectTripType(TripType.FullBurn);
            module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            string problem = module.PressStart();
            if (problem != null)
            {
                Fail("docshots", $"Start refused (flip style {style}): {problem} Planner: {module.Planner.SituationMessage} {string.Join(" | ", module.Planner.Notes)}");
                failed = true;
                yield break;
            }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Flip && pilot.PointingError > 60 &&
                                         pilot.PointingError < 120, 2400, $"docs: to the flip (style {style})");
            Frame(f.Ship, 0.35f, 0.5f, 1.4f);
            yield return new WaitForEndOfFrame();
            yield return Shot(file, f);
            module.PressAbort();
            yield return new WaitForSecondsRealtime(3f);
            Log($"docs: after the flip shot (style {style}): {pilot.StopReason}");
        }

        // alsoPowerStore: empty KSP Interstellar's Megajoules store as well (its reactor powers the tank coolers otherwise).
        private IEnumerator DocBoilOff(Flown f, bool alsoPowerStore = false)
        {
            MechJebModuleFlipAndBurn module = f.Module;
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            module.AccelerationG.Val = 0.4;
            module.SelectTripType(TripType.CoastInTheMiddle);
            foreach (Part p in f.Ship.parts)
            {
                PartResource ec = p.Resources.Get("ElectricCharge");
                if (ec != null) ec.amount = 0;
                PartResource mj = alsoPowerStore ? p.Resources.Get("Megajoules") : null;
                if (mj != null) mj.amount = 0;
            }

            yield return new WaitForSecondsRealtime(3f);
            // Normal ("on rails") warp with the window open: the planner samples the tanks once a game minute.
            TimeWarp tw = TimeWarp.fetch;
            int index = Array.FindIndex(tw.warpRates, r => r >= 99f);
            tw.Mode = TimeWarp.Modes.HIGH;
            TimeWarp.SetRate(index < 0 ? 1 : index, false);
            double start = Planetarium.GetUniversalTime();
            float t0 = Time.realtimeSinceStartup;
            while (Planetarium.GetUniversalTime() - start < 2.5 * 3600 && Time.realtimeSinceStartup - t0 < 600)
                yield return new WaitForSecondsRealtime(0.5f);
            TimeWarp.SetRate(0, true);
            yield return new WaitForSecondsRealtime(3f);
            module.Replan();
            yield return new WaitForSecondsRealtime(3f);
            Log($"docs: boil-off measured: {pilot.Loss.LostKg:F3} kg over {pilot.Loss.ObservedSeconds / 3600:F2} game h " +
                $"({pilot.Loss.RateKgPerSecond * 3600:F3} kg/h, measured {pilot.Loss.Measured}); set aside {module.Planner.LossAllowance:F0} m/s; " +
                $"notes: {string.Join(" | ", module.Planner.Notes)}");
            yield return Shot("doc-14-planner-boiloff.png", f);
        }
    }
}
