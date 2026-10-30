using System;
using System.Collections;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;

namespace FlipAndBurn.TestHarness
{
    // Cycle 13: engines that burn only electric charge ("electric"). The test copy's "Nuclear Deep Space Probe" (in Kerbin
    // orbit) carries three of Kris's Jibar Side Thruster GIANTs, a KSP Interstellar molten salt reactor and generator, and
    // batteries.
    //  1. What the planner reads: the engines, the power, the rows and notes for a 1 g full burn to Duna.
    //  2. The electric charge the engines really draw at full and part throttle (KSP's own fuel flow, and the batteries),
    //     against the planner's figure. Then the save is loaded again (the bursts change the orbit).
    //  3. A trip: the whole way if the power allows it; otherwise speed-up, flip and Abort.
    public partial class TestHarness
    {
        private const string ElectricShip = "Nuclear Deep Space Probe";
        // The test copy's quicksave: KSP rewrites "persistent" during a run (switching ships, changing scenes), the quicksave
        // only on F5, so every load here starts from the ship exactly as Kris saved it.
        private const string ElectricSave = "quicksave";

        private IEnumerator ProofElectric()
        {
            // ---- 1 and 2: the reading, and the draw measured
            var f = new Flown();
            yield return PlanOnShip(ElectricShip, null, null, f, ElectricSave);
            if (!f.Ok) yield break;
            Vessel v = f.Ship;
            CheatOptions.InfinitePropellant = false;
            CheatOptions.IgnoreMaxTemperature = false;
            foreach (ModuleEngines e in ElectricEngines(v).Where(e => !e.EngineIgnited)) e.Activate();
            yield return new WaitForSecondsRealtime(2f);
            // As saved: the reactor running (KSP Interstellar takes a few seconds to bring it up after a load).
            f.Module.AccelerationG.Val = 1.0;
            f.Module.SelectTripType(TripType.FullBurn);
            yield return new WaitForSecondsRealtime(15f);
            f.Module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            LogElectric(f, "at load");

            ElectricDrive drive = f.Module.Planner.Ship?.Electric;
            if (drive == null) { Fail("electric", "the planner didn't read the ship as electric"); failed = true; yield break; }
            foreach (float throttle in new[] { 1f, 0.3f })
            {
                FlightInputHandler.state.mainThrottle = throttle;
                yield return new WaitForSecondsRealtime(0.5f);
                double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, ShipEngines.ElectricCharge);
                double flow = 0, thrust = 0;
                int n = 0;
                while (Planetarium.GetUniversalTime() - ut0 < 1.0)
                {
                    yield return new WaitForFixedUpdate();
                    flow += ElectricEngines(v).Sum(e => (double)e.fuelFlowGui);
                    thrust += ElectricEngines(v).Sum(e => (double)e.finalThrust);
                    n++;
                }

                double dt = Planetarium.GetUniversalTime() - ut0, ec1 = Amount(v, ShipEngines.ElectricCharge);
                FlightInputHandler.state.mainThrottle = 0;
                flow /= Math.Max(1, n);
                thrust /= Math.Max(1, n);
                double planned = drive.ChargeFor(thrust * 1000);
                Log($"electric: throttle {throttle:F1}: thrust {thrust:F1} kN; KSP's own draw (fuel flow) {flow:F1} electric charge a second; " +
                    $"planner {planned:F1} a second for that thrust ({(flow / planned - 1) * 100:+0.00;-0.00}%); batteries {ec0:F0} -> {ec1:F0}: " +
                    $"net {(ec0 - ec1) / dt:F1} a second over {dt:F2} s");
                yield return new WaitForSecondsRealtime(2f);
            }

            // ---- 2b: what reaches the batteries from the reactor, against the planner's reading: at a throttle the reactor
            // covers, what the engines draw less how fast the batteries fall is what the ship really makes.
            f.Module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            drive = f.Module.Planner.Ship?.Electric;
            if (drive != null && drive.Supply > 0)
            {
                double small = Math.Min(1, 0.8 * drive.Supply / Math.Max(1, drive.ChargeAtFull));
                foreach (float throttle in new[] { (float)small, (float)Math.Min(1, 2 * small) })
                {
                    FlightInputHandler.state.mainThrottle = throttle;
                    yield return new WaitForSecondsRealtime(3f);
                    double ut0 = Planetarium.GetUniversalTime(), ec0 = Amount(v, ShipEngines.ElectricCharge), flow = 0;
                    int n = 0;
                    while (Planetarium.GetUniversalTime() - ut0 < 2.0)
                    {
                        yield return new WaitForFixedUpdate();
                        flow += ElectricEngines(v).Sum(e => (double)e.fuelFlowGui);
                        n++;
                    }

                    double dt = Planetarium.GetUniversalTime() - ut0, ec1 = Amount(v, ShipEngines.ElectricCharge);
                    FlightInputHandler.state.mainThrottle = 0;
                    flow /= Math.Max(1, n);
                    double net = (ec0 - ec1) / dt;
                    Log($"electric: reactor on, throttle {throttle:F4}: engines draw {flow:F1} a second; batteries {(net >= 0 ? "fall" : "rise")} {Math.Abs(net):F1} a second; " +
                        $"so the ship makes {flow - net:F1} a second; the planner reads {drive.Supply:F1} ({((flow - net) / drive.Supply - 1) * 100:+0.0;-0.0}%)");
                    yield return new WaitForSecondsRealtime(2f);
                }
            }
            else Log("electric: the planner reads no power from the reactor as loaded");

            // ---- 2c: the reactor shut down, as its window's Shutdown button does: the planner says so, and what it would take
            foreach (PartModule m in v.parts.SelectMany(p => p.Modules.Cast<PartModule>()).Where(IsReactor))
            {
                System.Reflection.FieldInfo on = null;
                for (Type t = m.GetType(); t != null && on == null; t = t.BaseType) on = t.GetField("IsEnabled");
                on?.SetValue(m, false);
            }

            yield return new WaitForSecondsRealtime(10f);
            f.Module.Replan();
            yield return new WaitForSecondsRealtime(2f);
            LogElectric(f, "reactor shut down");
            Frame(v, 0.30f, 0.9f, 1.4f);
            yield return Shot("electric-0-reactor-off.png", f);

            // ---- 3: a trip, from the ship as saved (the reactor running)
            var g = new Flown();
            yield return PlanOnShip(ElectricShip, null, null, g, ElectricSave);
            if (!g.Ok) yield break;
            v = g.Ship;
            foreach (ModuleEngines e in ElectricEngines(v).Where(e => !e.EngineIgnited)) e.Activate();
            StartReactors(v);
            yield return new WaitForSecondsRealtime(20f);
            MechJebModuleFlipAndBurn module = g.Module;
            module.FlipStyleIndex = 0;
            module.AccelerationG.Val = 1.0;
            module.SelectTripType(TripType.FullBurn);
            yield return new WaitForSecondsRealtime(SettleSeconds);
            module.Replan();
            // At 1 g first (for the picture of what the batteries allow), then at 90 % of what the reactor keeps up with.
            LogElectric(g, "1 g");
            yield return Shot("electric-0-planner-1g.png", g);
            ElectricDrive d1 = module.Planner.Ship?.Electric;
            double sustainG = d1 == null ? 0 : d1.SustainableAcceleration(v.totalMass * 1000) / TripCalculator.OneG;
            double chosen = Math.Min(1.0, Math.Floor(0.9 * sustainG * 100) / 100);   // 1 g if the reactor keeps up with that
            Log($"electric: the reactor sustains {sustainG:F3} g; flying at {chosen:F2} g");
            if (chosen >= 0.01)
            {
                module.AccelerationG.Val = chosen;
                module.Replan();
                yield return new WaitForSecondsRealtime(2f);
            }
            LogElectric(g, "before Start");
            Frame(v, 0.30f, 0.9f, 1.4f);
            yield return Shot("electric-1-planner.png", g);
            bool powered = module.Planner.Ship?.Electric != null && double.IsPositiveInfinity(module.Planner.Ship.DeltaVAtAcceleration(module.AccelerationG.Val * TripCalculator.OneG));
            if (!module.Planner.CanStart)
            {
                Log("electric: the planner won't start a full burn: " + string.Join(" | ", module.Planner.Notes));
                module.SelectTripType(TripType.CoastInTheMiddle);
                module.Replan();
                yield return new WaitForSecondsRealtime(2f);
                LogElectric(g, "coast trip");
                yield return Shot("electric-1b-planner-coast.png", g);
                if (!module.Planner.CanStart) { Fail("electric", "neither trip can be started: " + string.Join(" | ", module.Planner.Notes)); failed = true; yield break; }
            }

            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 25;
            string problem = module.PressStart();
            if (problem != null) { Fail("electric", "Start refused: " + problem); failed = true; yield break; }
            MechJebModuleFlipAndBurnAutopilot pilot = module.Autopilot;
            LogPlan(pilot);
            double start = Planetarium.GetUniversalTime();
            statusEvery = 10f;
            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Accelerate && !pilot.Last.Departing &&
                                         pilot.PointingError < 1 && Planetarium.GetUniversalTime() - start > 600, 900, "electric: speeding up");
            Frame(v, 0.25f, 2.2f, 1.3f);
            yield return new WaitForSecondsRealtime(2f);
            Log($"electric: speeding up: charge {pilot.ChargeShare * 100:F1}%, made {pilot.ChargeMade:F0}, used {pilot.ChargeUsed:F0} a second; replans {pilot.Replans}");
            yield return Shot("electric-2-speeding-up.png", g);

            yield return Fly(pilot, () => pilot.Last != null && pilot.Last.Phase == GuidancePhase.Flip && pilot.PointingError > 60 && pilot.PointingError < 120,
                3600, "electric: to the flip");
            Frame(v, 0.35f, 0.5f, 1.4f);
            yield return new WaitForEndOfFrame();
            yield return Shot("electric-3-flip.png", g);

            if (!powered)
            {
                Log("electric: not powered all the way: Abort after the flip");
                module.PressAbort();
                yield return new WaitForSecondsRealtime(3f);
                statusEvery = 2f;
                MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
                yield break;
            }

            yield return Fly(pilot, () => false, 4.5f * 3600, "electric: to arrival");
            yield return new WaitForSecondsRealtime(4f);
            Log($"electric: arrival: arrived {pilot.Arrived}; {pilot.StopReason}; charge {pilot.ChargeShare * 100:F1}%; replans {pilot.Replans}");
            if (!pilot.Arrived) { Fail("electric", "the trip didn't arrive: " + pilot.StopReason); failed = true; }
            Frame(v, 0.15f, 2.8f, 2.2f);
            yield return new WaitForSecondsRealtime(2f);
            yield return Shot("electric-4-arrived.png", g);
            yield return MapShot(g, "electric-5-arrived-map.png");
            statusEvery = 2f;
            MechJebModuleFlipAndBurnAutopilot.TestMaxPhysicsRate = 10;
        }

        // A field or property of a mod's module by name (exact case), or null.
        private static object Reflect(object o, string name)
        {
            for (Type t = o.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
            {
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                           System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
                System.Reflection.FieldInfo f = t.GetField(name, all);
                if (f != null) return f.GetValue(o);
                System.Reflection.PropertyInfo p = t.GetProperty(name, all);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(o); }
                    catch (Exception) { return null; }
                }
            }

            return null;
        }

        private static bool IsReactor(PartModule m)
        {
            for (Type t = m.GetType(); t != null && t != typeof(PartModule); t = t.BaseType)
                if (t.Name == "InterstellarReactor") return true;
            return false;
        }

        // KSP Interstellar reactors that are off: switched on with their own "Activate Reactor" (in memory; nothing is saved).
        private static int StartReactors(Vessel v)
        {
            int n = 0;
            foreach (Part part in v.parts)
            foreach (PartModule m in part.Modules)
            {
                if (!IsReactor(m) || Reflect(m, "IsEnabled") is true) continue;
                BaseEvent e = m.Events["ActivateReactor"];
                if (e == null) continue;
                e.Invoke();
                n++;
            }

            return n;
        }

        private static System.Collections.Generic.IEnumerable<ModuleEngines> ElectricEngines(Vessel v) =>
            v.FindPartModulesImplementing<ModuleEngines>().Where(e => e.propellants.Any(p => p.name == ShipEngines.ElectricCharge));

        private static void LogElectric(Flown f, string when)
        {
            Planner p = f.Module.Planner;
            ShipPower power = p.Reading?.Power;
            ElectricDrive d = p.Ship?.Electric;
            Log($"electric [{when}]: engines {string.Join(", ", ElectricEngines(f.Ship).Select(e => $"{e.part.partInfo.title} ({(e.EngineIgnited ? "on" : "off")}, {e.maxThrust:F0} kN)"))}; " +
                $"mass {f.Ship.totalMass:F2} t");
            if (power != null)
                Log($"electric [{when}]: power: stored {power.Stored:F0} of {power.Capacity:F0} (floor {power.Floor:F0}); steady {power.Steady:F1} a second; " +
                    $"solar {power.Solar:F1} at {power.SolarDistance:E3} m; fuel cells {power.FuelCellRate:F1} a second for {power.FuelCellSeconds:F0} s");
            if (d != null)
                Log($"electric [{when}]: drive: {d.FullThrust / 1000:F0} kN, {d.ChargeAtFull:F1} a second at full ({d.ChargeAtFull / (d.FullThrust / 1000):F3} per kN); " +
                    $"weakest Sun distance {d.WeakestSunDistance:E3} m; supply there {d.Supply:F1}; sustains {d.SustainableAcceleration(f.Ship.totalMass * 1000) / TripCalculator.OneG:F3} g");
            foreach (System.Collections.Generic.KeyValuePair<string, string> row in p.TripRows.Concat(p.ShipRows)) Log($"electric [{when}] row: {row.Key.Trim()} = {row.Value}");
            foreach (string note in p.Notes) Log($"electric [{when}] note: {note}");
            Log($"electric [{when}]: can start {p.CanStart}");
            // Every power module on the ship, as KSP has it, so the reading can be checked.
            foreach (Part part in f.Ship.parts)
            foreach (PartModule m in part.Modules)
            {
                string n = m.GetType().Name;
                if (n == "ModuleGenerator" && m is ModuleGenerator gen)
                    Log($"electric [{when}] module: {part.partInfo.title}: ModuleGenerator active {gen.generatorIsActive}, always {gen.isAlwaysActive}, " +
                        string.Join(", ", gen.resHandler.outputResources.Select(r => $"{r.name} {r.rate}")));
                else if (n.Contains("Generator") || n.Contains("Reactor"))
                    Log($"electric [{when}] module: {part.partInfo.title}: {n}: " + string.Join(", ",
                        new[] { "IsEnabled", "IsStarted", "MaximumPower", "MaximumThermalPower", "ongoing_thermal_power_generated", "ongoing_total_power_generated",
                                "maximumElectricPower", "outputPower", "electricdtps" }
                            .Select(k => (k, v: Reflect(m, k))).Where(x => x.v != null).Select(x => $"{x.k} {x.v}")));
            }
        }
    }
}
