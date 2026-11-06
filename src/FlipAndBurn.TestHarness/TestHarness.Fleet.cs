using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 15, stage 1 (ADR 0006): trips that fly on while the player is away. Test copy only; cheats only where said.
    // "fleetmun":  the Bebop to the Mun at 1 g with real heat, left after the first step-down with Leave it flying; a second
    //              ship (the Bebop V2.4, around Duna) starts its own trip and is left too; both fly from the Tracking Station
    //              at high warp; the Bebop arrives (warp stops, message); a save made mid-trip is loaded; the player switches
    //              to the second ship mid-trip and leaves it again; it arrives; each final orbit in map view.
    // "fleetduna": the Bebop from Kerbin orbit to Duna, a coast trip at 0.4 g, left right after Start and flown wholly in the
    //              background from the Tracking Station at the highest warp.
    public partial class TestHarness
    {
        private const string FleetMidSave = "fab-fleet-midtrip";
        private static readonly string[] FleetSecondTargets = { "Dres", "Eeloo", "Kerbin" };

        private IEnumerator ProofFleetMun()
        {
            // 1. The Bebop to the Mun, 1 g, full burn, real heat (no cheats), the player's physics warp choice.
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = double.NaN;
            if (!module.SelectTarget("Mun")) { Fail("fleet", "could not select the Mun"); failed = true; yield break; }
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 1.0;
            module.SelectTripType(TripType.FullBurn);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            string problem = module.Planner.CanStart ? module.PressStart() : "the planner won't start: " + string.Join(" ", module.Planner.Notes);
            if (problem != null) { Fail("fleet", "Start refused: " + problem); failed = true; yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            statusEvery = 30f;
            float t0 = Time.realtimeSinceStartup;
            while (pilot.Running && pilot.Replans == 0 && Time.realtimeSinceStartup - t0 < 40 * 60)
                yield return Fly(pilot, () => pilot.Replans > 0, 10, "fleet");
            if (!pilot.Running || pilot.Replans == 0)
            {
                Fail("fleet", $"no heat step-down to leave after (running {pilot.Running}, step-downs {pilot.Replans}): {pilot.StopReason}");
                failed = true;
                yield break;
            }

            Log($"fleet: step-down 1 at {pilot.CurrentAcceleration / 9.81:F2} g; notice: {pilot.Notice}");
            yield return Shot("fleet-1-step-down.png", f);

            // 2. Leave it flying, then switch to the second ship.
            module.PressLeaveFlying();
            Log($"fleet: Leave it flying pressed: running {pilot.Running}, ready to leave {pilot.ReadyToLeave}; notice: {pilot.PauseNotice}");
            yield return WaitClear();
            if (failed) yield break;
            OpenWindow(f);
            yield return Shot("fleet-2-leave-it-flying.png", f);
            double leftAt = pilot.Record.Acceleration;
            var second = new Flown();
            yield return SwitchTo(SecondShip, second);
            if (failed || !second.Ok) yield break;
            yield return new WaitForSecondsRealtime(3f);
            LogFleet("on the second ship");
            AwayTrip bebop = FleetFlights.Flying.FirstOrDefault(t => t.ShipName == ResumeShip);
            Log($"fleet: the Bebop flies on without the player: {(bebop != null ? $"yes, at {bebop.Record.Acceleration / 9.81:F2} g (left at {leftAt / 9.81:F2} g)" : "NO")}");
            if (bebop == null) { Fail("fleet", "the fleet isn't flying the Bebop"); failed = true; yield break; }

            // 3. The second ship's own trip: two ships on trips at once.
            bool started = false;
            MechJebModuleFlipAndBurn m2 = second.Module;
            OpenWindow(second);
            foreach (string target in FleetSecondTargets)
            {
                if (started || !m2.SelectTarget(target)) continue;
                foreach ((TripType type, double g) in new[] { (TripType.CoastInTheMiddle, 0.4), (TripType.FullBurn, 0.4), (TripType.CoastInTheMiddle, 0.2) })
                {
                    m2.AccelerationG.Val = g;
                    m2.SelectTripType(type);
                    yield return new WaitForSecondsRealtime(1f);
                    m2.Replan();
                    if (!m2.Planner.CanStart) continue;
                    problem = m2.PressStart();
                    Log($"fleet: second ship's trip to {target}, {type} at {g} g: {problem ?? "started"}");
                    started = problem == null;
                    break;
                }
            }

            if (!started) { Fail("fleet", "the second ship couldn't start a trip"); failed = true; yield break; }
            LogPlan(m2.Autopilot);
            yield return BurnFor(m2.Autopilot, 30, "second ship's trip");
            m2.PressLeaveFlying();
            yield return WaitClear();
            if (failed) yield break;

            // 4. To the Tracking Station: both fly on.
            yield return LeaveToTrackingStation();
            if (failed) yield break;
            LogFleet("in the Tracking Station");
            if (FleetFlights.Flying.Count() < 2) { Fail("fleet", "the fleet isn't flying both ships"); failed = true; yield break; }
            yield return Shot("fleet-3-two-ships-tracking-station.png", null);

            // 5. High warp until the Bebop arrives: warp stops, the message names it.
            yield return WarpUntilEnded(ResumeShip, "fleet-4-bebop-arrived-message.png", 20 * 60);
            if (failed) yield break;

            // 6. Save mid-trip (the second ship still flying), load that save on the Bebop, check its orbit.
            if (!SafeToSave(FleetMidSave)) yield break;
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), FleetMidSave, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            Log($"fleet: saved '{FleetMidSave}' in the Tracking Station at UT {Planetarium.GetUniversalTime():F1}");
            LogTripInSave(FleetMidSave, SecondShip, "the second ship's trip in the mid-trip save");
            LogTripInSave(FleetMidSave, ResumeShip, "the Bebop after arriving (no trip)");
            var back = new Flown();
            yield return FlyFromSave(FleetMidSave, ResumeShip, back, asQuickload: false);
            if (!back.Ok) yield break;
            yield return new WaitForSecondsRealtime(5f);
            LogOrbit("the Bebop, flown from the mid-trip save", back.Ship, "Mun", 20_000);
            OpenWindow(back);
            yield return Shot("fleet-5-bebop-at-the-mun.png", back);
            yield return MapShot(back, "fleet-6-bebop-orbit-map.png");
            LogFleet("on the Bebop after loading the mid-trip save");
            if (!FleetFlights.Flying.Any(t => t.ShipName == SecondShip)) { Fail("fleet", "the second ship's trip didn't carry on after the load"); failed = true; yield break; }

            // 7. Switch to the second ship mid-trip: the trip comes back to the player (the jump measured); then leave again.
            yield return new WaitForSecondsRealtime(5f);
            var mid = new Flown();
            yield return SwitchTo(SecondShip, mid);
            if (failed || !mid.Ok) yield break;
            yield return WatchResume(mid, "after the fleet flew it", "fleet-7-welcome-back.png");
            if (failed) yield break;
            MechJebModuleFlipAndBurnAutopilot p2 = mid.Module.Autopilot;
            Log($"fleet: back on the second ship: flown without the player for {p2.LastFleetSeconds:F0} s; handover jump {p2.LastHandoverJump:F2} m, " +
                $"{p2.LastHandoverJumpSpeed:F4} m/s; notice: {p2.Notice}");
            yield return BurnFor(p2, 20, "the second ship, watched again");
            mid.Module.PressLeaveFlying();
            yield return WaitClear();
            if (failed) yield break;
            yield return LeaveToTrackingStation();
            if (failed) yield break;
            LogFleet("left the second ship again, in the Tracking Station");

            // 8. The second ship arrives without the player: warp stops, the message names it. Then fly it.
            yield return WarpUntilEnded(SecondShip, "fleet-8-second-ship-arrived-message.png", 25 * 60);
            if (failed) yield break;
            AwayTrip done = FleetFlights.Ended.LastOrDefault(t => t.ShipName == SecondShip);
            if (!SafeToSave("persistent")) yield break;
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            var last = new Flown();
            yield return FlyFromSave("persistent", SecondShip, last, asQuickload: false);
            if (failed || !last.Ok) yield break;
            yield return new WaitForSecondsRealtime(5f);
            LogOrbit("the second ship after arriving without the player", last.Ship, done?.Target?.bodyName, done?.Record?.ParkingAltitude ?? double.NaN);
            yield return MapShot(last, "fleet-9-second-ship-orbit-map.png");
            Log("fleet mun: done");
            step++;
        }

        private IEnumerator ProofFleetDuna()
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            MechJebModuleFlipAndBurn module = f.Module;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
            if (!module.SelectTarget("Duna")) { Fail("fleet duna", "could not select Duna"); failed = true; yield break; }
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = ResumeAcceleration;
            module.SelectTripType(TripType.CoastInTheMiddle);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            OpenWindow(f);
            yield return Shot("fleet-duna-1-planner.png", f);
            string problem = module.Planner.CanStart ? module.PressStart() : "the planner won't start: " + string.Join(" ", module.Planner.Notes);
            if (problem != null) { Fail("fleet duna", "Start refused: " + problem); failed = true; yield break; }
            LogPlan(module.Autopilot);
            yield return new WaitForSecondsRealtime(3f);
            module.PressLeaveFlying();
            yield return WaitClear();
            if (failed) yield break;
            yield return LeaveToTrackingStation();
            if (failed) yield break;
            LogFleet("Duna coast trip, in the Tracking Station");
            yield return Shot("fleet-duna-2-tracking-station.png", null);
            yield return WarpUntilEnded(ResumeShip, "fleet-duna-3-arrived-message.png", 30 * 60);
            if (failed) yield break;
            AwayTrip done = FleetFlights.Ended.LastOrDefault(t => t.ShipName == ResumeShip);
            if (!SafeToSave("persistent")) yield break;
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            var back = new Flown();
            yield return FlyFromSave("persistent", ResumeShip, back, asQuickload: false);
            if (!back.Ok) yield break;
            yield return new WaitForSecondsRealtime(5f);
            LogOrbit("the Bebop after the Duna coast trip flown without the player", back.Ship, "Duna", done?.Record?.ParkingAltitude ?? 60_000);
            yield return MapShot(back, "fleet-duna-4-orbit-map.png");
            Log("fleet duna: done");
            step++;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The Tracking Station button: save persistent, leave.</summary>
        private IEnumerator LeaveToTrackingStation()
        {
            if (!SafeToSave("persistent")) yield break;
            if (FlightGlobals.ClearToSave(false) != ClearToSaveStatus.CLEAR) { Fail("step " + step, "the Tracking Station button would be locked"); failed = true; yield break; }
            GamePersistence.SaveGame(HighLogic.CurrentGame.Updated(), "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            Log($"step {step}: leaving for the Tracking Station");
            HighLogic.LoadScene(GameScenes.TRACKSTATION);
            float t0 = Time.realtimeSinceStartup;
            while (!(HighLogic.LoadedScene == GameScenes.TRACKSTATION && TimeWarp.fetch != null) && Time.realtimeSinceStartup - t0 < 300)
                yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForSecondsRealtime(5f);
            Log($"step {step}: in the Tracking Station ({FlightGlobals.Vessels.Count} vessels)");
            step++;
        }

        /// <summary>Warp as high as the scene allows until the fleet ends <paramref name="ship"/>'s trip; screenshot the message.</summary>
        private IEnumerator WarpUntilEnded(string ship, string shot, float timeoutReal)
        {
            int ended = FleetFlights.Ended.Count;
            float t0 = Time.realtimeSinceStartup, nextLog = 0;
            int top = TimeWarp.fetch != null ? TimeWarp.fetch.warpRates.Length - 1 : 0;
            Log($"fleet: warping (rails, up to x{(TimeWarp.fetch != null ? TimeWarp.fetch.warpRates[top] : 1):0}) until {ship} arrives");
            while (Time.realtimeSinceStartup - t0 < timeoutReal)
            {
                AwayTrip end = FleetFlights.Ended.Skip(ended).FirstOrDefault(t => t.ShipName == ship);
                if (end != null)
                {
                    yield return null;
                    Log($"fleet: {ship}'s trip ended at UT {end.EndedUT:F0}; time warp now x{TimeWarp.CurrentRate:0.#} (index {TimeWarp.CurrentRateIndex}); message: {end.Report}");
                    yield return Shot(shot, null);
                    if (TimeWarp.CurrentRateIndex != 0) Fail("fleet", "time warp didn't drop to normal at the arrival");
                    yield break;
                }

                if (TimeWarp.fetch != null && TimeWarp.CurrentRateIndex < top) TimeWarp.SetRate(TimeWarp.CurrentRateIndex + 1, false);
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 20;
                    LogFleet($"warping x{TimeWarp.CurrentRate:0}");
                }

                yield return new WaitForSecondsRealtime(0.5f);
            }

            Fail("fleet", $"{ship} didn't arrive within {timeoutReal / 60:0} minutes of warp");
            failed = true;
        }

        private static void LogFleet(string when)
        {
            double now = Planetarium.GetUniversalTime();
            List<AwayTrip> trips = FleetFlights.Flying.ToList();
            Log($"fleet [{when}]: {trips.Count} trip(s) flown without the player" + string.Concat(trips.Select(t =>
                $"; {t.ShipName} to {t.TargetName}: {t.Flight.Last?.Phase}, at {t.Record.Acceleration / 9.81:F2} g, {Math.Max(0, now - t.Flight.UT):F1} s behind, " +
                $"delta-v used {t.Record.DeltaVUsed:F0} m/s, propellant {t.Flight.PropellantUsed:F1} kg, arrival in {t.Record.PlannedArrivalUT - now:F0} s, " +
                $"handover jump {t.HandoverJump:F2} m / {t.HandoverJumpSpeed:F4} m/s, orbit around {t.Vessel?.mainBody?.bodyName}")));
        }

        private static void LogOrbit(string what, Vessel v, string expectBody, double parking)
        {
            Orbit o = v.orbit;
            bool there = expectBody == null || v.mainBody.bodyName == expectBody;
            double tol = 0.05 * parking;
            bool pe = Math.Abs(o.PeA - parking) <= tol, ap = Math.Abs(o.ApA - parking) <= tol;
            Log($"fleet arrival: {what}: around {v.mainBody.bodyName}; periapsis {o.PeA / 1000:F2} km, apoapsis {o.ApA / 1000:F2} km, eccentricity {o.eccentricity:F5}; " +
                $"planned {parking / 1000:F0} km +/- {tol / 1000:F1} km: periapsis {(pe ? "ok" : "OUT")}, apoapsis {(ap ? "ok" : "OUT")}; " +
                $"{(there && pe && ap ? "ARRIVED TO STANDARD" : "NOT TO STANDARD")}");
            if (!(there && pe && ap)) Fail("fleet", what + " isn't in its parking orbit");
        }
    }
}
