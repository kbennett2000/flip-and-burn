using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using MuMech;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FlipAndBurn
{
    /// <summary>
    /// Trips flown while the player is away from the ship (ADR 0006): in flight on another ship, at the Space Center or in
    /// the Tracking Station. Each one is flown by <see cref="BackgroundFlight"/>, the same guidance as a watched trip,
    /// advanced to the game clock every frame. After each advance the ship's KSP orbit is set to the flown state, the
    /// propellant and electric charge it burned are taken out of its tanks, and its saved trip is brought up to date, so a
    /// save, a scene change or the player coming back all pick the trip up where it is.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.AllGameScenes, false)]
    public sealed class FleetFlights : MonoBehaviour
    {
        /// <summary>Wall-clock time the fleet may use each frame (ms), shared by all its trips.</summary>
        public const double FrameBudgetMs = 4;

        public static FleetFlights Instance { get; private set; }

        // Kept across scene changes, so a trip flies on exactly where it was; checked against the saved trip each scan.
        private static readonly Dictionary<Guid, AwayTrip> Trips = new Dictionary<Guid, AwayTrip>();

        /// <summary>For the test helper and the window: the trips being flown now.</summary>
        public static IEnumerable<AwayTrip> Flying => Trips.Values;

        /// <summary>For the test helper: every trip the fleet ended this session, with its message.</summary>
        public static readonly List<AwayTrip> Ended = new List<AwayTrip>();

        private float nextScan, nextReport;
        private static readonly HashSet<Guid> WaitingNear = new HashSet<Guid>();   // ships told to wait (logged once)
        private readonly Stopwatch clock = new Stopwatch();

        private static bool FleetScene => HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.SPACECENTER ||
                                          HighLogic.LoadedScene == GameScenes.TRACKSTATION;

        public void Awake()
        {
            if (!FleetScene)
            {
                enabled = false;
                return;
            }

            Instance = this;
            GameEvents.onVesselChange.Add(OnVesselChange);
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
            Debug.Log($"[FlipAndBurn] fleet: ready in {HighLogic.LoadedScene} ({Trips.Count} trip(s) carried over)");
        }

        public void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(OnVesselChange);
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
            if (Instance == this) Instance = null;
        }

        // A saved game was loaded: every trip starts again from what that save says. (An instance method: KSP's events can't
        // take a static one, and the add-on then never started.)
        private void OnGameStateLoad(ConfigNode game)
        {
            if (Trips.Count > 0) Debug.Log($"[FlipAndBurn] fleet: a saved game was loaded; {Trips.Count} trip(s) carry on from the save");
            Trips.Clear();
        }

        // The player took over a ship the fleet was flying: hand it back to the autopilot, which resumes it.
        private void OnVesselChange(Vessel now)
        {
            if (now == null || !Trips.TryGetValue(now.id, out AwayTrip t)) return;
            if (t.Vessel == now) HandBack(t, "you switched to it");
            else Trips.Remove(t.Id);   // a new scene: the saved trip is already up to date
        }

        private bool updateFailed;

        public void Update()
        {
            try
            {
                FlyFleet();
            }
            catch (Exception e)
            {
                if (!updateFailed) Debug.LogError("[FlipAndBurn] fleet: update failed: " + e);
                updateFailed = true;
            }
        }

        private void FlyFleet()
        {
            if (Planetarium.fetch == null || FlightGlobals.Vessels == null) return;
            if (HighLogic.LoadedSceneIsFlight && !FlightGlobals.ready) return;
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 1f;
                Scan();
            }

            if (Trips.Count == 0) return;
            double now = Planetarium.GetUniversalTime();
            clock.Restart();
            // The trip furthest behind goes first, so a slow one can't starve the others.
            foreach (AwayTrip t in Trips.Values.OrderBy(t => t.Flight.UT).ToList())
            {
                if (clock.Elapsed.TotalMilliseconds > FrameBudgetMs) break;
                Advance(t, now);
            }

            HoldWarp(now);

            if (Time.realtimeSinceStartup >= nextReport)
            {
                nextReport = Time.realtimeSinceStartup + 30f;
                foreach (AwayTrip t in Trips.Values)
                    Debug.Log($"[FlipAndBurn] fleet: {t.ShipName} to {t.TargetName}: {t.Flight.Last?.Phase}, {Math.Max(0, now - t.Flight.UT):F1} s behind the game clock, " +
                              $"{t.Flight.Steps} steps; delta-v used {t.Record.DeltaVUsed:F0} m/s; arrival planned at UT {t.Record.PlannedArrivalUT:F0}");
            }
        }

        /// <summary>A trip may fall this far behind the game clock (game s) before time warp is held down for it.</summary>
        public const double LagLimit = 60;

        private float nextWarpHold;
        private static float lastWarpHold = -100;

        /// <summary>True for two real seconds after the fleet held time warp down (the test helper doesn't raise it meanwhile).</summary>
        public static bool HoldingWarp => Time.realtimeSinceStartup - lastWarpHold < 2;
        private static readonly HashSet<Guid> HeldFor = new HashSet<Guid>();

        // Time warp faster than the fleet can fly: step it down, a notch at a time, when a trip falls more than a minute behind
        // the game clock or its arrival is a few real seconds away at this warp. So a ship's map position stays right and its
        // arrival (warp to normal, the message) comes when it really arrives.
        private void HoldWarp(double now)
        {
            if (TimeWarp.fetch == null || TimeWarp.CurrentRateIndex <= 0 || Time.realtimeSinceStartup < nextWarpHold) return;
            AwayTrip behind = null;
            foreach (AwayTrip t in Trips.Values)
            {
                if (t.Flight.Finished) continue;
                double lag = now - t.Flight.UT, toArrival = t.Flight.Last?.TimeToArrival ?? double.PositiveInfinity;
                if (lag > LagLimit || (toArrival > 0 && toArrival < 5 * TimeWarp.CurrentRate)) { behind = t; break; }
            }

            if (behind == null) return;
            int from = TimeWarp.CurrentRateIndex;
            TimeWarp.SetRate(from - 1, false);
            nextWarpHold = Time.realtimeSinceStartup + 0.5f;
            lastWarpHold = Time.realtimeSinceStartup;
            if (HeldFor.Add(behind.Id))
                Debug.Log($"[FlipAndBurn] fleet: time warp held down (from x{TimeWarp.fetch.warpRates[from]:0}) to fly {behind.ShipName}'s trip: " +
                          $"{now - behind.Flight.UT:F0} s behind the game clock, arrival in {behind.Flight.Last?.TimeToArrival:F0} s");
        }

        // ------------------------------------------------------------------ finding the trips

        private void Scan()
        {
            var seen = new HashSet<Guid>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null || v.state == Vessel.State.DEAD) continue;
                bool active = HighLogic.LoadedSceneIsFlight && v == FlightGlobals.ActiveVessel;
                if (active)
                {
                    // The player's ship: the autopilot flies its trip.
                    if (Trips.TryGetValue(v.id, out AwayTrip mine))
                    {
                        if (mine.Vessel == v) HandBack(mine, "you are flying it");
                        else Trips.Remove(mine.Id);   // a new scene: the saved trip is already up to date
                    }

                    continue;
                }

                TripRecord r = TripStore.Read(v);
                if (r == null || r.PausedByPlayer || r.Away == null) continue;
                seen.Add(v.id);
                Trips.TryGetValue(v.id, out AwayTrip t);
                if (t != null && v.loaded && !ReferenceEquals(r, t.Record) && t.Matches(r))
                {
                    // The ship loaded (near the one being flown) with the trip as last written: the autopilot and the fleet
                    // share one record from now on, so a save has the newest.
                    TripStore.Adopt(v, t.Record);
                    r = t.Record;
                }

                if (t != null && t.Matches(r))
                {
                    t.Vessel = v;   // a scene change made a new Vessel object
                    t.Record = r;
                    continue;
                }

                if (v.loaded && !v.packed)
                {
                    // Close to the ship being flown, in its physics range: KSP moves it, not the fleet. It flies on once
                    // it is on rails (the player moves away, or time warp).
                    if (WaitingNear.Add(v.id)) Debug.Log($"[FlipAndBurn] fleet: {v.vesselName} waits: it is in the physics range of the ship you're flying");
                    continue;
                }

                WaitingNear.Remove(v.id);
                AwayTrip built = Build(v, r);
                if (built != null) Trips[v.id] = built;
            }

            foreach (Guid id in Trips.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                Debug.Log($"[FlipAndBurn] fleet: {Trips[id].ShipName} is no longer on a trip the fleet flies");
                Trips.Remove(id);
            }
        }

        private static AwayTrip Build(Vessel v, TripRecord r)
        {
            string ship = v.vesselName;
            CelestialBody target = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == r.Target);
            TripFrame frame = target != null ? TripFrame.Saved(r.Centre, target) : null;
            if (frame == null)
            {
                EndTrip(v, r, ship, r.Target, $"{ship} couldn't carry on its trip to {r.Target}: that body isn't in this game. It coasts on its orbit.", false);
                return null;
            }

            string targetName = Planner.BodyName(target);
            double now = Planetarium.GetUniversalTime();
            double ut0 = Math.Min(r.LastFlownUT, now);
            AwayShip a = r.Away;
            bool infinite = CheatOptions.InfinitePropellant;
            double mass = ShipMass(v);
            double ve = infinite ? double.PositiveInfinity : a.ExhaustVelocity;

            ShipPerformance perf = infinite || double.IsPositiveInfinity(ve) || a.ChargePerNewton > 0
                ? null
                : new ShipPerformance(a.FullThrust, ve / ShipPerformance.StandardGravity, a.IspThrottleExponent, mass, Math.Min(mass, a.DryMass));
            TripRequest request = frame.Request(v, ut0, r.Acceleration, r.ParkingAltitude, starting: !r.Departed);
            // The request is from the ship's orbit now; the flight starts where the trip was last flown.
            StateVector start = frame.ShipState(v, ut0);
            request.ShipPosition = start.Position;
            request.ShipVelocity = start.Velocity;
            request.StartTime = ut0;
            double keep = r.Trip == TripType.CoastInTheMiddle ? r.PlannedArrivalUT : double.NaN;
            double startDeltaV = infinite || !(r.DeltaVBudget > 0) ? double.NaN : (r.DeltaVBudget + r.LossAllowance) / (1 - Planner.Reserve);
            ReplanResult check = TripReplanner.Check(request, r.Trip, r.DeltaVLeft, perf, Planner.Reserve, keep, startDeltaV);
            if (!check.Feasible)
            {
                string why = MechJebModuleFlipAndBurnAutopilot.ProblemText(check, target, r.Acceleration, r.Trip);
                EndTrip(v, r, ship, targetName, $"{ship} couldn't carry on its trip to {targetName}: {why}. Nothing was fired; it coasts on " +
                                                $"a {OrbitText(v.orbit)} orbit around {Planner.BodyName(v.mainBody)}.", false);
                return null;
            }

            var config = new GuidanceConfig
            {
                Acceleration = r.Acceleration,
                Trip = r.Trip,
                DeltaVBudget = r.Trip == TripType.CoastInTheMiddle ? check.DeltaVAvailable : r.DeltaVLeft,
                Flip = r.Flip,
                ParkingAltitude = r.ParkingAltitude,
                TurnRate = a.TurnRate,
                CoastArrivalUT = check.Coast != null ? check.Coast.ArrivalUT : double.NaN,
            };
            frame.Configure(config, v, starting: !r.Departed);

            var bodies = new List<GravitySource>();
            void Add(CelestialBody b, bool always)
            {
                if (b == null || b == frame.Centre || b.referenceBody != frame.Centre || bodies.Any(x => x.Name == b.bodyName)) return;
                bodies.Add(new GravitySource
                {
                    Name = b.bodyName, Ephemeris = new KspOrbitEphemeris(b.orbit), GravParameter = b.gravParameter,
                    SphereOfInfluence = b.sphereOfInfluence, Always = always, SafeRadius = TripFrame.SafeRadius(b),
                });
            }

            Add(target, true);
            Add(v.mainBody, false);
            if (frame.PlanetSystem)
                foreach (CelestialBody m in FlightGlobals.Bodies.Where(b => b.referenceBody == frame.Centre))
                    Add(m, false);

            double charge = ResourceAmount(v, ShipEngines.ElectricCharge);
            var flight = new BackgroundFlight(config, bodies, frame.Centre.gravParameter, ut0, start.Position, start.Velocity, a.Facing, mass, ve,
                Math.Min(mass, a.DryMass), TripFrame.SafeRadius(target), a.ChargePerNewton, a.ChargeMade, charge, a.ChargeFloor, a.FullThrust,
                a.IspThrottleExponent);
            if (double.IsNaN(r.FleetSinceUT)) r.FleetSinceUT = ut0;
            var t = new AwayTrip
            {
                Vessel = v, Id = v.id, ShipName = ship, Record = r, Flight = flight, Frame = frame, Target = target, TargetName = targetName,
                UsedBefore = r.DeltaVUsed, StartUT = ut0, StartMass = mass,
            };

            // The jump at the handover: where the ship is now (its orbit, back to the handover time) against where the trip
            // last had it.
            if (!double.IsNaN(r.HandoverUT))
            {
                StateVector then = frame.ShipState(v, r.HandoverUT);
                t.HandoverJump = (then.Position - r.HandoverPosition).Length;
                t.HandoverJumpSpeed = (then.Velocity - r.HandoverVelocity).Length;
            }

            Debug.Log($"[FlipAndBurn] fleet: flying {ship} to {targetName} without the player from UT {ut0:F1} ({now - ut0:F1} s ago): {r.Trip} at " +
                      $"{r.Acceleration / TripCalculator.OneG:F2} g, {(check.KeptArrival ? "keeping the arrival it was flying to" : "fastest plan from here")}, " +
                      $"remaining {check.RemainingTime:F0} s; mass {mass:F0} kg, dry {a.DryMass:F0} kg, exhaust {ve:F0} m/s, turn {a.TurnRate * 180 / Math.PI:F2} deg/s, " +
                      $"propellants {string.Join(", ", a.Propellants.Select(p => $"{p.Key} {p.Value:P0}"))}; handover jump " +
                      $"{t.HandoverJump:F2} m, {t.HandoverJumpSpeed:F4} m/s; bodies {string.Join(", ", bodies.Select(b => b.Name))}");
            return t;
        }

        // ------------------------------------------------------------------ flying

        private void Advance(AwayTrip t, double now)
        {
            Vessel v = t.Vessel;
            if (v == null || v.state == Vessel.State.DEAD) return;
            if (v.loaded && !v.packed) return;   // in the physics range of the ship being flown: KSP moves it
            try
            {
                while (!t.Flight.Finished && t.Flight.UT < now - 1e-9 && clock.Elapsed.TotalMilliseconds < FrameBudgetMs)
                    t.Flight.AdvanceTo(now, 200);
                Write(t);
                if (t.Flight.Finished) Finish(t);
            }
            catch (Exception e)
            {
                Debug.LogError("[FlipAndBurn] fleet: flying " + t.ShipName + " failed: " + e);
                Trips.Remove(t.Id);
                EndTrip(v, t.Record, t.ShipName, t.TargetName, $"{t.ShipName} stopped its trip to {t.TargetName}: Flip and Burn hit an internal error " +
                                                               "(the details are in KSP.log). It coasts on its orbit.", false);
            }
        }

        // The flown state into the game: the orbit, the tanks and the saved trip.
        private static void Write(AwayTrip t)
        {
            Vessel v = t.Vessel;
            BackgroundFlight f = t.Flight;
            SetOrbit(v, t.Frame.Centre, f.UT, f.Position, f.Velocity);

            if (!CheatOptions.InfinitePropellant)
            {
                double kg = f.PropellantUsed - t.PropellantWritten;
                if (kg > 0)
                {
                    foreach (KeyValuePair<string, double> p in t.Record.Away.Propellants)
                    {
                        PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(p.Key);
                        if (def != null && def.density > 0) TakeResource(v, p.Key, kg * p.Value / (def.density * 1000));
                    }

                    t.PropellantWritten = f.PropellantUsed;
                }
            }

            double charge = f.ChargeUsed - t.ChargeWritten;
            if (charge > 0)
            {
                TakeResource(v, ShipEngines.ElectricCharge, charge);
                t.ChargeWritten = f.ChargeUsed;
            }

            TripRecord r = t.Record;
            r.DeltaVUsed = t.UsedBefore + f.DeltaVUsed;
            r.LastFlownUT = f.UT;
            GuidanceCommand last = f.Last;
            if (!double.IsNaN(f.Guidance.CoastArrivalUT)) r.PlannedArrivalUT = f.Guidance.CoastArrivalUT;
            else if (last != null && last.TimeToArrival > 0 && last.Phase != GuidancePhase.Done) r.PlannedArrivalUT = f.UT + last.TimeToArrival;
            if (last != null && last.Phase != GuidancePhase.Waiting) r.Departed = true;
            r.Away.Facing = f.Facing;
            r.HandoverUT = f.UT;
            r.HandoverPosition = f.Position;
            r.HandoverVelocity = f.Velocity;
            TripStore.Write(v, r);
        }

        private static void Finish(AwayTrip t)
        {
            Trips.Remove(t.Id);
            Vessel v = t.Vessel;
            BackgroundFlight f = t.Flight;
            Orbit o = v.orbit;
            string where = Planner.BodyName(v.mainBody);
            double took = f.UT - t.Record.StartUT;
            string report;
            bool arrived = f.End == BackgroundEnd.Arrived;
            switch (f.End)
            {
                case BackgroundEnd.Arrived:
                    report = $"{t.ShipName} arrived at {t.TargetName} without you: in a {OrbitText(o)} orbit (planned: {t.Record.ParkingAltitude / 1000:0} km, circular). " +
                             $"The trip took {Planner.FormatTime(took)}.";
                    break;
                case BackgroundEnd.SafeOrbit:
                    report = $"{t.ShipName} couldn't finish in its planned orbit at {t.TargetName}: {f.Reason}. It made its orbit safe instead: " +
                             $"{OrbitText(o)} around {where}.";
                    break;
                default:
                    report = $"{t.ShipName} stopped its trip to {t.TargetName}: {f.Reason}. It coasts on a {OrbitText(o)} orbit around {where}.";
                    break;
            }

            t.Report = report;
            t.EndedUT = f.UT;
            Debug.Log($"[FlipAndBurn] fleet: trip over for {t.ShipName}: {f.End}; around {v.mainBody.bodyName}, periapsis {o.PeA:F0} m, apoapsis {o.ApA:F0} m, " +
                      $"eccentricity {o.eccentricity:F5}; flown {f.UT - t.StartUT:F0} s without the player, {f.Steps} steps; delta-v {f.DeltaVUsed:F0} m/s, " +
                      $"propellant {f.PropellantUsed:F1} kg (mass {t.StartMass:F0} -> {f.Mass:F0} kg), charge {f.ChargeUsed:F1}; " +
                      $"{Math.Max(0, Planetarium.GetUniversalTime() - f.UT):F1} s behind the game clock");
            EndTrip(v, t.Record, t.ShipName, t.TargetName, report, arrived);
            Ended.Add(t);
        }

        // The trip is over: forget it on the ship, drop time warp, and tell the player (ADR 0006, Kris's decision 3).
        private static void EndTrip(Vessel v, TripRecord r, string ship, string target, string report, bool arrived)
        {
            TripStore.Remove(v, report, arrived);
            if (TimeWarp.fetch != null) TimeWarp.SetRate(0, true);
            ScreenMessages.PostScreenMessage("Flip and Burn: " + report, 20f, ScreenMessageStyle.UPPER_CENTER);
            Debug.Log("[FlipAndBurn] fleet: " + report);
        }

        // The player is taking the ship over: bring the saved trip up to now, then leave it to the autopilot.
        private void HandBack(AwayTrip t, string why)
        {
            double now = Planetarium.GetUniversalTime();
            try
            {
                clock.Restart();
                if (!t.Flight.Finished) t.Flight.AdvanceTo(now, 200_000);
                Write(t);
                if (t.Flight.Finished)
                {
                    Finish(t);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[FlipAndBurn] fleet: handing back " + t.ShipName + " failed: " + e);
            }

            Trips.Remove(t.Id);
            Debug.Log($"[FlipAndBurn] fleet: handing {t.ShipName} back to the player ({why}) at UT {t.Flight.UT:F1} (game clock {now:F1}): " +
                      $"{t.Flight.Last?.Phase}, delta-v used {t.Record.DeltaVUsed:F0} m/s");
        }

        // ------------------------------------------------------------------ the game's side

        /// <summary>Set the ship's orbit to a state relative to the trip's centre, around the body whose sphere it is in.</summary>
        private static void SetOrbit(Vessel v, CelestialBody centre, double ut, Vec3 position, Vec3 velocity)
        {
            CelestialBody body = centre;
            Vec3 bodyPos = Vec3.Zero, bodyVel = Vec3.Zero;
            for (bool deeper = true; deeper;)
            {
                deeper = false;
                foreach (CelestialBody c in body.orbitingBodies)
                {
                    Vec3 p = bodyPos + KspOrbitEphemeris.ToVec(c.orbit.getRelativePositionAtUT(ut));
                    if ((position - p).Length >= c.sphereOfInfluence) continue;
                    bodyVel += KspOrbitEphemeris.ToVec(c.orbit.getOrbitalVelocityAtUT(ut));
                    bodyPos = p;
                    body = c;
                    deeper = true;
                    break;
                }
            }

            Vec3 r = position - bodyPos, w = velocity - bodyVel;
            v.orbitDriver.orbit.UpdateFromStateVectors(new Vector3d(r.X, r.Y, r.Z), new Vector3d(w.X, w.Y, w.Z), body, ut);
            if (!v.loaded && v.protoVessel != null) v.protoVessel.orbitSnapShot = new OrbitSnapshot(v.orbitDriver.orbit);
        }

        private static double ShipMass(Vessel v)
        {
            if (v.loaded) return v.totalMass * 1000.0;
            double t = 0;
            foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
            {
                t += p.mass;
                foreach (ProtoPartResourceSnapshot res in p.resources)
                {
                    PartResourceDefinition def = PartResourceLibrary.Instance.GetDefinition(res.resourceName);
                    if (def != null) t += res.amount * def.density;
                }
            }

            return t * 1000.0;
        }

        private static double ResourceAmount(Vessel v, string name)
        {
            double sum = 0;
            if (v.loaded)
            {
                foreach (Part p in v.parts)
                foreach (PartResource res in p.Resources)
                    if (res.resourceName == name) sum += res.amount;
                return sum;
            }

            foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
            foreach (ProtoPartResourceSnapshot res in p.resources)
                if (res.resourceName == name) sum += res.amount;
            return sum;
        }

        // Take units of a resource out of the ship's parts, in proportion to what each holds.
        private static void TakeResource(Vessel v, string name, double units)
        {
            double total = ResourceAmount(v, name);
            if (!(total > 0) || !(units > 0)) return;
            double share = Math.Min(1, units / total);
            if (v.loaded)
            {
                foreach (Part p in v.parts)
                foreach (PartResource res in p.Resources)
                    if (res.resourceName == name) res.amount = Math.Max(0, res.amount * (1 - share));
                return;
            }

            foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
            foreach (ProtoPartResourceSnapshot res in p.resources)
                if (res.resourceName == name) res.amount = Math.Max(0, res.amount * (1 - share));
        }

        internal static string OrbitText(Orbit o) => $"{o.PeA / 1000:0.0} × {o.ApA / 1000:0.0} km";
    }

    /// <summary>A trip the fleet flies, and what it has written to the ship so far.</summary>
    public sealed class AwayTrip
    {
        public Guid Id;
        public Vessel Vessel;
        public string ShipName, TargetName;
        public TripRecord Record;
        public BackgroundFlight Flight;
        internal TripFrame Frame;
        public CelestialBody Target;
        public double UsedBefore, StartUT, StartMass, PropellantWritten, ChargeWritten;
        public double HandoverJump = double.NaN, HandoverJumpSpeed = double.NaN;
        public string Report;
        public double EndedUT = double.NaN;

        /// <summary>The saved trip is the one this flight last wrote (not an older save loaded since).</summary>
        public bool Matches(TripRecord r) => r != null && Math.Abs(r.LastFlownUT - Flight.UT) <= 1e-6 * Math.Max(1, Math.Abs(Flight.UT));
    }

    /// <summary>
    /// A ship's saved trip, wherever it is: the autopilot's own on a loaded ship, or the node inside the ship's saved MechJeb
    /// settings on an unloaded one (MODULE MechJebCore / MechJebLocalSettings / MechJebModuleFlipAndBurnAutopilot).
    /// </summary>
    internal static class TripStore
    {
        private const string AutopilotNode = "MechJebModuleFlipAndBurnAutopilot";

        private static MechJebModuleFlipAndBurnAutopilot Autopilot(Vessel v)
        {
            MechJebCore core = v.GetMasterMechJeb();
            return core != null ? core.GetComputerModule<MechJebModuleFlipAndBurnAutopilot>() : null;
        }

        private static IEnumerable<ConfigNode> AutopilotNodes(Vessel v)
        {
            if (v.protoVessel == null) yield break;
            foreach (ProtoPartSnapshot p in v.protoVessel.protoPartSnapshots)
            foreach (ProtoPartModuleSnapshot m in p.modules)
            {
                if (m.moduleName != "MechJebCore" || m.moduleValues == null) continue;
                ConfigNode a = m.moduleValues.GetNode("MechJebLocalSettings")?.GetNode(AutopilotNode);
                if (a != null) yield return a;
            }
        }

        public static TripRecord Read(Vessel v)
        {
            if (v.loaded)
            {
                MechJebModuleFlipAndBurnAutopilot ap = Autopilot(v);
                return ap != null && !ap.Running ? ap.Record : null;
            }

            foreach (ConfigNode a in AutopilotNodes(v))
            {
                ConfigNode node = a.GetNode(MechJebModuleFlipAndBurnAutopilot.TripNodeName);
                if (node == null) continue;
                return TripRecord.FromValues(node.values.Cast<ConfigNode.Value>().Select(x => new KeyValuePair<string, string>(x.name, x.value)), out string _);
            }

            return null;
        }

        public static void Write(Vessel v, TripRecord r)
        {
            if (v.loaded)
            {
                Adopt(v, r);   // the autopilot saves it with the ship
                return;
            }

            foreach (ConfigNode a in AutopilotNodes(v))
            {
                ConfigNode node = a.GetNode(MechJebModuleFlipAndBurnAutopilot.TripNodeName);
                if (node == null) continue;
                node.ClearValues();
                foreach (KeyValuePair<string, string> kv in r.ToValues()) node.AddValue(kv.Key, kv.Value);
            }
        }

        public static void Adopt(Vessel v, TripRecord r)
        {
            MechJebModuleFlipAndBurnAutopilot ap = Autopilot(v);
            if (ap != null && !ReferenceEquals(ap.Record, r)) ap.AdoptFleetRecord(r);
        }

        public static void Remove(Vessel v, string report, bool arrived)
        {
            if (v.loaded)
            {
                Autopilot(v)?.AwayTripEnded(report, arrived);
                return;
            }

            foreach (ConfigNode a in AutopilotNodes(v))
                a.RemoveNode(MechJebModuleFlipAndBurnAutopilot.TripNodeName);
        }
    }
}
