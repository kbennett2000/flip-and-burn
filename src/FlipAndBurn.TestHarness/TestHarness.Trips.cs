using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 11: whole trips, Start in Kerbin orbit to the parking orbit around Duna, with nothing touched in between. Each
    // trip is checked from the log: the orbit, the engine off, MechJeb's controls, warp rates and settings back, out of warp
    // before arrival, the arrival message; and, every physics step, the engine throttle while the ship points more than 5
    // degrees off, and what firing up to 5 degrees off costs.
    public partial class TestHarness
    {
        private sealed class TripSpec
        {
            public string Name;          // "A", "B", "C"
            public string Prefix;        // screenshot prefix
            public string What;          // plain words, for the log
            public double AccelG;
            public TripType Trip;
            public int FlipStyle;        // 0 cut, flip, relight; 1 skew flip
            public bool InfinitePropellant, IgnoreMaxTemperature;
            public double MaxPhysicsRate;
            public bool ResumeLeg;       // pause, quicksave and quickload once on the way
        }

        private static readonly TripSpec[] Trips =
        {
            new TripSpec
            {
                Name = "A", Prefix = "trip-a", What = "full burn to Duna at 1 g, cut, flip, relight; infinite propellant and ignore max temperature",
                AccelG = 1.0, Trip = TripType.FullBurn, FlipStyle = 0, InfinitePropellant = true, IgnoreMaxTemperature = true, MaxPhysicsRate = 25,
            },
            new TripSpec
            {
                Name = "B", Prefix = "trip-b", What = "coast trip to Duna at 0.4 g, no cheats; paused, quicksaved and quickloaded once on the way",
                AccelG = 0.4, Trip = TripType.CoastInTheMiddle, FlipStyle = 0, MaxPhysicsRate = 10, ResumeLeg = true,
            },
            new TripSpec
            {
                Name = "C", Prefix = "trip-c", What = "full burn to Duna at 1 g with a skew flip; infinite propellant and ignore max temperature",
                AccelG = 1.0, Trip = TripType.FullBurn, FlipStyle = 1, InfinitePropellant = true, IgnoreMaxTemperature = true, MaxPhysicsRate = 25,
            },
        };

        /// <summary>The arrival tolerance, set before flying: periapsis and apoapsis both within this share of the parking altitude.</summary>
        private const double OrbitTolerance = 0.05;

        // ------------------------------------------------------------------ what the step watch measures

        private sealed class Window
        {
            public string Name;
            public int OffSteps;                // steps pointing more than 5 degrees off (where thrust is not allowed)
            public float MaxOffThrottle;        // the engine's own throttle in those steps
            public int OffThrottleSteps;        // steps in which it wasn't zero
            public double ThrustSeconds;        // game seconds firing within 5 degrees but more than 0.1 off
            public double MaxFiringError;       // the largest pointing error while firing (deg)
            public double Sideways, ForwardLoss, Total;   // m/s: thrust across the burn direction, thrust lost along it, all thrust
        }

        private readonly Dictionary<string, Window> windows = new Dictionary<string, Window>();
        private double lastEventUT = double.NaN, lastWarpUT = double.NaN;
        private string lastEvent = "start";
        private bool warpWhileCircularizing;
        private GuidancePhase lastPhase = GuidancePhase.Done;
        private int lastResumes;

        private Window W(string name)
        {
            if (!windows.TryGetValue(name, out Window w)) windows[name] = w = new Window { Name = name };
            return w;
        }

        private void ResetStepWatch()
        {
            windows.Clear();
            lastEventUT = lastWarpUT = double.NaN;
            lastEvent = "start";
            warpWhileCircularizing = false;
            lastPhase = GuidancePhase.Done;
            lastResumes = 0;
        }

        // Every physics step: is the engine at zero whenever the ship points more than 5 degrees off? And what does firing
        // while up to 5 degrees off cost? Split by what just happened: Start, the relight after the flip, a resume, a course
        // correction, or the steady burn.
        private void WatchStep(MechJebModuleFlipAndBurnAutopilot pilot, Vessel ship, bool skew)
        {
            GuidanceCommand c = pilot.Last;
            if (c == null) return;
            double ut = Planetarium.GetUniversalTime(), dt = TimeWarp.fixedDeltaTime;
            if (double.IsNaN(lastEventUT)) lastEventUT = ut;
            if (lastPhase == GuidancePhase.Flip && c.Phase != GuidancePhase.Flip) { lastEvent = "after the relight"; lastEventUT = ut; }
            if (c.Correcting && lastEvent != "course correction") { lastEvent = "course correction"; lastEventUT = ut; }
            if (pilot.Resumes != lastResumes) { lastResumes = pilot.Resumes; lastEvent = "after a resume"; lastEventUT = ut; }
            lastPhase = c.Phase;
            string window = ut - lastEventUT < 300 ? lastEvent : "steady";

            if (TimeWarp.CurrentRate > 1.01f) lastWarpUT = ut;
            if (c.Circularizing && TimeWarp.CurrentRate > 1.01f) warpWhileCircularizing = true;

            bool exempt = skew && c.Phase == GuidancePhase.Flip;   // a skew flip thrusts while it turns, by design
            float throttle = EngineThrottle(ship);
            double err = pilot.PointingError;
            Window w = W(window);
            if (!exempt && err > MechJebModuleFlipAndBurnAutopilot.PointingTolerance)
            {
                w.OffSteps++;
                w.MaxOffThrottle = Math.Max(w.MaxOffThrottle, throttle);
                if (throttle > 0.001f && ++w.OffThrottleSteps <= 5)
                    Log($"turning watch [{window}]: engine throttle {throttle:F3} while {err:F2} deg off at UT {ut:F2}, phase {c.Phase}, " +
                        $"ship throttle {ship.ctrlState.mainThrottle:F3}, warp x{TimeWarp.CurrentRate:0.#}");
            }

            if (!exempt && throttle > 0.01f && err <= MechJebModuleFlipAndBurnAutopilot.PointingTolerance)
            {
                double a = pilot.MeasuredAcceleration, rad = err * Math.PI / 180;
                w.Total += a * dt;
                w.Sideways += a * Math.Sin(rad) * dt;
                w.ForwardLoss += a * (1 - Math.Cos(rad)) * dt;
                if (err > 0.1) w.ThrustSeconds += dt;
                w.MaxFiringError = Math.Max(w.MaxFiringError, err);
            }
        }

        private void LogStepWatch(string trip)
        {
            foreach (Window w in windows.Values.OrderBy(x => x.Name))
            {
                Log($"trip {trip} turning [{w.Name}]: {w.OffSteps} physics steps pointing more than {MechJebModuleFlipAndBurnAutopilot.PointingTolerance} deg off, " +
                    $"highest engine throttle in them {w.MaxOffThrottle:F3} ({(w.MaxOffThrottle <= 0.001f ? "zero" : $"NOT ZERO in {w.OffThrottleSteps} steps")})");
                Log($"trip {trip} pointing cost [{w.Name}]: firing within {MechJebModuleFlipAndBurnAutopilot.PointingTolerance} deg but more than 0.1 deg off for " +
                    $"{w.ThrustSeconds:F1} game s, largest error while firing {w.MaxFiringError:F2} deg; thrust across the burn direction {w.Sideways:F2} m/s, " +
                    $"lost along it {w.ForwardLoss:F3} m/s, of {w.Total:F0} m/s fired");
            }
        }

        // ------------------------------------------------------------------ one whole trip

        private IEnumerator ProofTrip(TripSpec spec, CaptureResult outcome)
        {
            var f = new Flown();
            yield return PlanOnShip(ResumeShip, null, null, f, SaveFile);
            if (!f.Ok) yield break;
            MechJebModuleFlipAndBurn module = f.Module;
            statusEvery = 10f;
            ResetStepWatch();
            CheatOptions.InfinitePropellant = spec.InfinitePropellant;
            CheatOptions.IgnoreMaxTemperature = spec.IgnoreMaxTemperature;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = spec.MaxPhysicsRate;
            module.FlipStyleIndex = spec.FlipStyle;
            module.AccelerationG.Val = spec.AccelG;
            module.SelectTripType(spec.Trip);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Replan();
            Log($"trip {spec.Name}: {spec.What}; infinite propellant {CheatOptions.InfinitePropellant}, ignore max temperature {CheatOptions.IgnoreMaxTemperature}, " +
                $"physics warp up to x{spec.MaxPhysicsRate}; arrival tolerance: periapsis and apoapsis within {OrbitTolerance * 100:0}% of the parking altitude, " +
                $"both above the atmosphere; planner can start {module.Planner.CanStart}");
            if (!module.Planner.CanStart) { Fail("trip " + spec.Name, "the planner won't start: " + string.Join(" ", module.Planner.Notes)); failed = true; yield break; }
            string settingsBefore = MechJebSettingsFingerprint(f);
            beforeSettingsText = lastSettingsText;
            LogBeforeStart(f);
            HideMechJebMenu(f);
            Frame(f.Ship, 0.30f, 0.9f, 1.4f);
            yield return Shot($"{spec.Prefix}-1-start.png", f);

            string problem = module.PressStart();
            if (problem != null) { Fail("trip " + spec.Name, "Start refused: " + problem); failed = true; yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            double startUT = Planetarium.GetUniversalTime();
            bool skew = spec.FlipStyle == 1;
            Flown cur = f;
            stepWatch = p => WatchStep(p, cur.Ship, skew);

            bool midShot = false, flipShot = false, slowShot = false, coastShot = false, arriveShot = false, resumed = !spec.ResumeLeg;
            double flipEndUT = double.NaN;
            // Without the propellant cheat: the tanks, power and the delta-v reader logged through the trip (every half
            // hour while burning, every 6 h while coasting), as the reserve is checked against them.
            bool tanks = !spec.InfinitePropellant;
            double nextTanks = startUT;
            GuidancePhase tanksPhase = GuidancePhase.Accelerate;
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 4.5f * 3600)
            {
                pilot = cur.Module.Autopilot;
                if (!pilot.Running) break;
                GuidanceCommand c = pilot.Last;
                double ut = Planetarium.GetUniversalTime();
                if (tanks && c != null && (ut >= nextTanks || c.Phase != tanksPhase))
                {
                    tanksPhase = c.Phase;
                    nextTanks = ut + (c.Phase == GuidancePhase.Coast ? 6 * 3600 : 1800);
                    LogTankState(cur.Ship, pilot, $"trip {spec.Name} tanks", $"{(ut - startUT) / 3600:F2} h, {c.Phase}", spec.AccelG * 9.80665);
                }

                if (c != null)
                {
                    bool steady = pilot.PointingError < 1 && !c.Departing && !c.Correcting;
                    if (!midShot && c.Phase == GuidancePhase.Accelerate && steady && ut - startUT > 900)
                    {
                        midShot = true;
                        Frame(cur.Ship, 0.25f, 2.2f, 1.3f);
                        yield return Shot($"{spec.Prefix}-2-mid-burn.png", cur);
                        continue;
                    }

                    if (!resumed && c.Phase == GuidancePhase.Accelerate && ut - startUT > 1800)
                    {
                        resumed = true;
                        yield return ResumeLegOnTheWay(spec, cur, x => cur = x);
                        if (failed) yield break;
                        continue;
                    }

                    if (!flipShot && c.Phase == GuidancePhase.Flip && pilot.PointingError > 50 && pilot.PointingError < 130)
                    {
                        flipShot = true;
                        Frame(cur.Ship, 0.35f, 0.5f, 1.4f);
                        yield return Shot($"{spec.Prefix}-3-flip.png", cur);
                        continue;
                    }

                    if (!coastShot && c.Phase == GuidancePhase.Coast && TimeWarp.CurrentRate > 1.01f && pilot.PointingError < 2)
                    {
                        coastShot = true;
                        Frame(cur.Ship, 0.2f, 3.6f, 1.6f);
                        yield return Shot($"{spec.Prefix}-3-coast.png", cur);
                        continue;
                    }

                    if (c.Phase == GuidancePhase.Decelerate && double.IsNaN(flipEndUT)) flipEndUT = ut;
                    if (!slowShot && c.Phase == GuidancePhase.Decelerate && steady && ut - flipEndUT > 600)
                    {
                        slowShot = true;
                        Frame(cur.Ship, 0.25f, 4.0f, 1.3f);
                        yield return Shot($"{spec.Prefix}-4-slow-down.png", cur);
                        continue;
                    }

                    if (!arriveShot && cur.Ship.mainBody.bodyName == PlannerTarget && cur.Ship.altitude < 1_500_000)
                    {
                        arriveShot = true;
                        Frame(cur.Ship, 0.15f, 2.8f, 2.2f);
                        yield return Shot($"{spec.Prefix}-5-arriving.png", cur);
                        continue;
                    }
                }

                yield return Fly(pilot, () => true, 1, "trip " + spec.Name);
                yield return new WaitForFixedUpdate();
            }

            stepWatch = null;
            yield return new WaitForSecondsRealtime(3f);
            if (tanks) LogTankState(cur.Ship, cur.Module.Autopilot, $"trip {spec.Name} tanks", "arrived", spec.AccelG * 9.80665);
            yield return CheckArrival(spec, cur, settingsBefore, startUT);
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
            Log($"trip {spec.Name}: infinite propellant cheat back off: {!CheatOptions.InfinitePropellant}; ignore max temperature cheat back off: {!CheatOptions.IgnoreMaxTemperature}");
            statusEvery = 2f;
            outcome.Ok = !failed;
        }

        // Pause, quicksave, quickload, carry on: the resume in the middle of a real trip.
        private IEnumerator ResumeLegOnTheWay(TripSpec spec, Flown cur, Action<Flown> now)
        {
            Log($"trip {spec.Name}: on the way: Pause, quicksave, quickload");
            yield return PauseUntilClear(cur, null);
            if (failed) yield break;
            yield return QuickSave("fab-" + spec.Prefix);
            var back = new Flown();
            yield return FlyFromSave("fab-" + spec.Prefix, ResumeShip, back, asQuickload: true);
            if (failed || !back.Ok) yield break;
            HideMechJebMenu(back);
            stepWatch = p => WatchStep(p, back.Ship, spec.FlipStyle == 1);
            yield return WatchResume(back, $"in trip {spec.Name}", $"{spec.Prefix}-2b-resumed.png");
            MechJebModuleFlipAndBurnAutopilot p2 = back.Module.Autopilot;
            Log($"trip {spec.Name}: after the resume: reserve expected at arrival {p2.ReserveAtArrival:F0} m/s ({p2.ReserveShare * 100:F2}%); window note: {p2.ReserveNote ?? "(none: 10% or more)"}");
            now(back);
        }

        // The arrival, checked from the log.
        private IEnumerator CheckArrival(TripSpec spec, Flown f, string settingsBefore, double startUT)
        {
            MechJebModuleFlipAndBurnAutopilot pilot = f.Module.Autopilot;
            Vessel v = f.Ship;
            Orbit o = v.orbit;
            CelestialBody duna = v.mainBody;
            double parking = pilot.Trip.ParkingAltitude, atmosphere = duna.atmosphere ? duna.atmosphereDepth : 0;
            bool around = duna.bodyName == PlannerTarget;
            bool peOk = Math.Abs(o.PeA - parking) <= OrbitTolerance * parking, apOk = Math.Abs(o.ApA - parking) <= OrbitTolerance * parking;
            bool aboveAir = o.PeA > atmosphere && o.ApA > atmosphere;
            Log($"trip {spec.Name} arrival: arrived {pilot.Arrived}; around {duna.bodyName}; periapsis {o.PeA / 1000:F2} km, apoapsis {o.ApA / 1000:F2} km, " +
                $"eccentricity {o.eccentricity:F5}; planned {parking / 1000:F0} km +/- {OrbitTolerance * parking / 1000:F1} km: periapsis {(peOk ? "ok" : "OUT")}, " +
                $"apoapsis {(apOk ? "ok" : "OUT")}; atmosphere top {atmosphere / 1000:F0} km: {(aboveAir ? "both above" : "NOT ABOVE")}");
            Log($"trip {spec.Name} arrival message: {pilot.StopReason}");
            Log($"trip {spec.Name} reserve: expected at arrival (last check) {pilot.ReserveAtArrival:F0} m/s ({pilot.ReserveShare * 100:F2}%); window note: {pilot.ReserveNote ?? "(none)"}");
            Log($"trip {spec.Name} arrival numbers: trip {pilot.ArrivedTripTime:F0} s, planned at Start {pilot.ArrivedPlannedTripTime:F0} s " +
                $"({pilot.ArrivedTripTime - pilot.ArrivedPlannedTripTime:+0;-0} s); delta-v left {pilot.ArrivedDeltaVLeft:F0} m/s");
            double lastWarpBefore = Planetarium.GetUniversalTime() - lastWarpUT;
            Log($"trip {spec.Name} warp: last time warp above x1 ended {lastWarpBefore:F0} game s before the check; warp during the circularizing burn: " +
                $"{(warpWhileCircularizing ? "YES" : "no")}");
            LogStepWatch(spec.Name);
            LogAfterAbort(f);
            // The test tucked MechJeb's menu away for clean pictures: put it back as it was before comparing the settings.
            ShowMechJebMenu(f);
            yield return new WaitForSecondsRealtime(3f);
            MechJebSettingsFingerprint(f);
            string verdict = SettingsVerdict();
            Log($"trip {spec.Name}: MechJeb settings files before Start and after arrival: {verdict} ({lastSettingsText.Count} files)");
            if (settingsBefore != null) LogSettingsDiff();

            // The final orbit, in map view, with the arrival message.
            HideMechJebMenu(f);
            yield return MapShot(f, $"{spec.Prefix}-6-final-orbit-map.png");

            bool engineOff = EngineThrottle(v) <= 0.001f && v.FindPartModulesImplementing<ModuleEngines>().All(e => e.finalThrust < 0.01f);
            bool ok = pilot.Arrived && around && peOk && apOk && aboveAir && engineOff && !warpWhileCircularizing && verdict != "DIFFERENT" &&
                      windows.Values.All(w => w.MaxOffThrottle <= 0.001f) && (pilot.StopReason ?? "").StartsWith("Arrived");
            Log($"trip {spec.Name}: {(ok ? "ARRIVED CLEANLY" : "NOT A CLEAN ARRIVAL")} (engine off {engineOff})");
            step++;
            if (!ok) { Fail("trip " + spec.Name, "the arrival didn't meet the standard (see above)"); failed = true; }
        }

        // ------------------------------------------------------------------ pictures

        // A clean picture: MechJeb's menu tucked away (the test opened it), the Flip and Burn window left showing.
        private static void HideMechJebMenu(Flown f)
        {
            var menu = f.Core.GetComputerModule<MechJebModuleMenu>();
            if (menu != null && menu.windowStat != MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();
        }

        private static void ShowMechJebMenu(Flown f)
        {
            var menu = f.Core.GetComputerModule<MechJebModuleMenu>();
            if (menu != null && menu.windowStat == MechJebModuleMenu.WindowStat.HIDDEN) menu.ShowHideWindow();
        }

        // Frame the ship: pitch and heading in radians, distance as a multiple of the ship's size.
        private static void Frame(Vessel v, float pitch, float heading, float sizeFactor)
        {
            try
            {
                FlightCamera cam = FlightCamera.fetch;
                if (cam == null || MapView.MapIsEnabled) return;
                float size = Math.Max(10f, v.vesselSize.magnitude);
                cam.camPitch = pitch;
                cam.camHdg = heading;
                cam.SetDistanceImmediate(size * sizeFactor);
            }
            catch (Exception e)
            {
                Log("couldn't frame the ship: " + e.Message);
            }
        }

        private IEnumerator MapShot(Flown f, string file)
        {
            try
            {
                MapView.EnterMapView();
            }
            catch (Exception e)
            {
                Log("couldn't open map view: " + e.Message);
            }

            yield return new WaitForSecondsRealtime(2f);
            try
            {
                PlanetariumCamera pc = PlanetariumCamera.fetch;
                pc.SetTarget(f.Ship.mainBody.MapObject);
                pc.SetDistance((float)(f.Ship.mainBody.Radius * 4.5 / 6000));
            }
            catch (Exception e)
            {
                Log("couldn't frame the map: " + e.Message);
            }

            yield return new WaitForSecondsRealtime(3f);
            yield return Shot(file, f);
            try
            {
                MapView.ExitMapView();
            }
            catch (Exception)
            {
                // Leaving map view is only for tidiness.
            }
        }
    }
}
