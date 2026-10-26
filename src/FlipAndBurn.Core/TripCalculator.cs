using System;
using System.Collections.Generic;

namespace FlipAndBurn.Core
{
    /// <summary>A planet the ship can fly to.</summary>
    public sealed class TargetPlanet
    {
        public string Name { get; }
        public IEphemeris Ephemeris { get; }
        public double GravParameter { get; }   // m^3/s^2
        public double Radius { get; }          // m

        public TargetPlanet(string name, IEphemeris ephemeris, double gravParameter, double radius)
        {
            Name = name;
            Ephemeris = ephemeris ?? throw new ArgumentNullException(nameof(ephemeris));
            GravParameter = gravParameter;
            Radius = radius;
        }
    }

    /// <summary>Everything the trip estimate needs. Positions and velocities are relative to the Sun.</summary>
    public sealed class TripRequest
    {
        public Vec3 ShipPosition { get; set; }
        public Vec3 ShipVelocity { get; set; }
        public double StartTime { get; set; }                                       // UT, s
        public TargetPlanet Target { get; set; }
        public double Acceleration { get; set; } = TripCalculator.OneG;           // m/s^2
        public double ParkingAltitude { get; set; }                                 // m above the target's surface
        public double SunGravParameter { get; set; }                                // m^3/s^2; 0 skips the gravity warning
        public double SunSafetyDistance { get; set; } = TripCalculator.DefaultSunSafetyDistance;  // m from the Sun's centre
        public double MaxTripTime { get; set; } = TripCalculator.DefaultMaxTripTime;             // s
    }

    public enum TripProblem
    {
        None,
        InvalidInput,
        NoSolutionWithinMaxTripTime,
    }

    /// <summary>The planned burn: a uniform velocity-matching push plus a flip-at-the-midpoint profile.</summary>
    internal struct BurnProfile
    {
        public double T;
        public Vec3 R0, V0;
        public Vec3 C;    // uniform velocity-matching acceleration
        public Vec3 E;    // unit direction of the flip-and-burn part
        public double Ab; // strength of the flip-and-burn part
        public double DLength;

        /// <summary>Ship position and velocity <paramref name="t"/> seconds after the start (clamped to the trip).</summary>
        public StateVector StateAt(double t)
        {
            if (T <= 0) return new StateVector(R0, V0);
            t = Math.Max(0, Math.Min(T, t));
            double half = T / 2;
            double along, alongSpeed;
            if (t <= half)
            {
                along = 0.5 * Ab * t * t;
                alongSpeed = Ab * t;
            }
            else
            {
                along = DLength - 0.5 * Ab * (T - t) * (T - t);
                alongSpeed = Ab * (T - t);
            }

            Vec3 pos = R0 + V0 * t + C * (0.5 * t * t) + E * along;
            Vec3 vel = V0 + C * t + E * alongSpeed;
            return new StateVector(pos, vel);
        }
    }

    /// <summary>The trip estimate.</summary>
    public sealed class TripPlan
    {
        internal BurnProfile Profile;

        /// <summary>
        /// Where the ship would be <paramref name="secondsAfterStart"/> into the planned burn (gravity ignored, as in the
        /// estimate). Clamped to the trip.
        /// </summary>
        public StateVector ShipStateAt(double secondsAfterStart) => Profile.StateAt(secondsAfterStart);

        public TripProblem Problem { get; internal set; }
        public string ProblemDetail { get; internal set; } = "";
        public bool Feasible => Problem == TripProblem.None;

        public double TripTime { get; internal set; }         // s, start of burn to arrival
        public double TimeToFlip { get; internal set; }       // s after the start; always TripTime / 2
        public double ArrivalTime { get; internal set; }      // UT, s

        /// <summary>
        /// Highest speed during the trip, measured relative to the ship's velocity at the start of the burn. For a ship
        /// that leaves from a parking orbit this is, to within that orbit's speed, the speed relative to the planet it
        /// left.
        /// </summary>
        public double PeakSpeed { get; internal set; }        // m/s

        /// <summary>Delta-v of the burn itself, including matching the target planet's velocity.</summary>
        public double BurnDeltaV { get; internal set; }       // m/s
        /// <summary>Delta-v to go from rest relative to the planet into the circular parking orbit.</summary>
        public double ParkingOrbitDeltaV { get; internal set; }
        public double TotalDeltaV { get; internal set; }

        /// <summary>Thrust acceleration before and after the flip. The stronger one equals the requested acceleration.</summary>
        public double AccelerationBeforeFlip { get; internal set; }
        public double AccelerationAfterFlip { get; internal set; }

        /// <summary>Where the burn ends: the parking orbit's radius on the near side of the planet, at arrival.</summary>
        public Vec3 AimPoint { get; internal set; }
        public Vec3 TargetPositionAtArrival { get; internal set; }
        public Vec3 TargetVelocityAtArrival { get; internal set; }

        public int ArrivalIterations { get; internal set; }
        /// <summary>Trip time after each arrival-time iteration, first guess first. Shows the iteration converging.</summary>
        public IReadOnlyList<double> ArrivalTimeHistory { get; internal set; } = Array.Empty<double>();
        public bool ArrivalConverged { get; internal set; }

        public double ClosestSunDistance { get; internal set; }    // m from the Sun's centre
        public double ClosestSunTime { get; internal set; }        // s after the start
        public bool PassesNearSun { get; internal set; }

        /// <summary>True when the acceleration is under 10 times the Sun's pull somewhere on the path.</summary>
        public bool GravityNotNegligible { get; internal set; }
    }

    /// <summary>
    /// Estimates a flip-and-burn trip: constant acceleration toward where the target planet will be, flip at the
    /// midpoint, decelerate, arrive at rest relative to the planet, then enter a circular parking orbit.
    /// </summary>
    /// <remarks>
    /// Model (see docs/adr/0002-trip-behaviour.md):
    /// <list type="bullet">
    /// <item>Gravity is ignored during the burn.</item>
    /// <item>The burn has two parts added together. A uniform push c = (vEnd - v0) / T matches the planet's velocity
    /// over the whole trip. On top of it, a flip-at-the-midpoint profile of strength ab = 4|D| / T^2 along ê covers
    /// the remaining displacement D = aim - r0 - (v0 + vEnd) T / 2: +ab ê before the flip, -ab ê after.</item>
    /// <item>The trip time T is the shortest for which the stronger half, max |c ± ab ê|, is at most the requested
    /// acceleration.</item>
    /// <item>The planet moves, so T is found by iterating on the arrival time.</item>
    /// </list>
    /// With the planet held still and the ship starting at rest, this reduces to t = 2 sqrt(d/a) and
    /// delta-v = 2 sqrt(d a).
    /// </remarks>
    public static class TripCalculator
    {
        public const double OneG = 9.81;

        /// <summary>
        /// 4.0e9 m from the Sun's centre (about 15 solar radii). Just inside Moho's perihelion (4.21e9 m), the closest any
        /// stock planet gets to the Sun. Solar flux there is about 15,700 W/m^2, 11.6 times Kerbin's. Reasoning in ADR 0002.
        /// </summary>
        public const double DefaultSunSafetyDistance = 4.0e9;

        /// <summary>About 1,087 Kerbin years. Trips longer than this are reported as having no solution.</summary>
        public const double DefaultMaxTripTime = 1e10;

        public const double GravityWarningRatio = 10;

        private const int MaxArrivalIterations = 100;
        private const int PathSamples = 2000;

        public static TripPlan Plan(TripRequest request)
        {
            var plan = new TripPlan();
            string invalid = Validate(request);
            if (invalid != null)
            {
                plan.Problem = TripProblem.InvalidInput;
                plan.ProblemDetail = invalid;
                return plan;
            }

            Vec3 r0 = request.ShipPosition, v0 = request.ShipVelocity;
            double t0 = request.StartTime, accel = request.Acceleration, maxT = request.MaxTripTime;
            double parkingRadius = request.Target.Radius + request.ParkingAltitude;

            // Fixed-point iteration on the arrival time: aim at where the planet is at the guessed arrival, solve the
            // trip time for that fixed end point, repeat.
            double tripTime = SolveForArrival(request, parkingRadius, 0);
            var history = new List<double> { tripTime };
            int iterations = 1;
            bool converged = false;
            while (!double.IsNaN(tripTime) && iterations < MaxArrivalIterations)
            {
                double next = SolveForArrival(request, parkingRadius, tripTime);
                iterations++;
                history.Add(next);
                if (double.IsNaN(next))
                {
                    tripTime = double.NaN;
                    break;
                }

                bool done = Math.Abs(next - tripTime) <= 1e-6 + 1e-12 * tripTime;
                tripTime = next;
                if (done)
                {
                    converged = true;
                    break;
                }
            }

            // Fixed-point iteration can fail when the planet moves about as fast as the ship (very low acceleration).
            // Fall back to finding T where SolveForArrival(T) = T by scanning and bisection.
            if (!converged)
            {
                double found = SolveArrivalByBisection(request, parkingRadius, maxT, ref iterations);
                if (!double.IsNaN(found))
                {
                    tripTime = found;
                    converged = true;
                    history.Add(found);
                }
            }

            plan.ArrivalIterations = iterations;
            plan.ArrivalTimeHistory = history;
            plan.ArrivalConverged = converged;
            if (!converged || double.IsNaN(tripTime))
            {
                plan.Problem = TripProblem.NoSolutionWithinMaxTripTime;
                plan.ProblemDetail = $"No trip at {accel} m/s^2 reaches {request.Target.Name} within {maxT:G3} s.";
                return plan;
            }

            StateVector target = request.Target.Ephemeris.StateAt(t0 + tripTime);
            Vec3 aim = AimPoint(r0, target.Position, parkingRadius);
            if (tripTime <= 0)
            {
                // Already at the aim point with the planet's velocity: nothing to burn but the parking orbit.
                plan.ArrivalTime = t0;
                plan.AimPoint = aim;
                plan.TargetPositionAtArrival = target.Position;
                plan.TargetVelocityAtArrival = target.Velocity;
                plan.ParkingOrbitDeltaV = Math.Sqrt(request.Target.GravParameter / parkingRadius);
                plan.TotalDeltaV = plan.ParkingOrbitDeltaV;
                plan.ClosestSunDistance = r0.Length;
                plan.PassesNearSun = r0.Length < request.SunSafetyDistance;
                plan.Profile = new BurnProfile { T = 0, R0 = r0, V0 = v0 };
                return plan;
            }

            BurnProfile p = ProfileFor(r0, v0, aim, target.Velocity, tripTime);
            plan.Profile = p;

            plan.TripTime = tripTime;
            plan.TimeToFlip = tripTime / 2;
            plan.ArrivalTime = t0 + tripTime;
            plan.AimPoint = aim;
            plan.TargetPositionAtArrival = target.Position;
            plan.TargetVelocityAtArrival = target.Velocity;
            plan.AccelerationBeforeFlip = (p.C + p.Ab * p.E).Length;
            plan.AccelerationAfterFlip = (p.C - p.Ab * p.E).Length;
            plan.BurnDeltaV = tripTime / 2 * (plan.AccelerationBeforeFlip + plan.AccelerationAfterFlip);
            plan.ParkingOrbitDeltaV = Math.Sqrt(request.Target.GravParameter / parkingRadius);
            plan.TotalDeltaV = plan.BurnDeltaV + plan.ParkingOrbitDeltaV;

            // Speed relative to v0 grows linearly in each half, so its maximum is at the flip or at the end.
            double half = tripTime / 2;
            plan.PeakSpeed = Math.Max(((p.C + p.Ab * p.E) * half).Length, (p.C * tripTime).Length);

            CheckSun(request, p, plan);
            return plan;
        }

        private static string Validate(TripRequest r)
        {
            if (r == null) return "request is null";
            if (r.Target == null) return "no target planet";
            if (!r.ShipPosition.IsFinite || !r.ShipVelocity.IsFinite) return "ship position or velocity is not a finite number";
            if (!IsFinite(r.StartTime)) return "start time is not a finite number";
            if (!(r.Acceleration > 0) || !IsFinite(r.Acceleration)) return "acceleration must be a positive number";
            if (!(r.ParkingAltitude >= 0) || !IsFinite(r.ParkingAltitude)) return "parking altitude must be zero or more";
            if (!(r.Target.Radius >= 0) || !IsFinite(r.Target.Radius)) return "target radius must be zero or more";
            if (!(r.Target.GravParameter > 0) || !IsFinite(r.Target.GravParameter)) return "target gravitational parameter must be positive";
            if (!(r.Target.Radius + r.ParkingAltitude > 0)) return "parking orbit radius must be positive";
            if (!(r.MaxTripTime > 0) || !IsFinite(r.MaxTripTime)) return "maximum trip time must be positive";
            if (!(r.SunSafetyDistance >= 0)) return "Sun safety distance must be zero or more";
            if (!(r.SunGravParameter >= 0) || !IsFinite(r.SunGravParameter)) return "Sun gravitational parameter must be zero or more";
            return null;
        }

        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        /// <summary>Trip time needed if the planet's state at <c>StartTime + guess</c> were the fixed end point.</summary>
        private static double SolveForArrival(TripRequest r, double parkingRadius, double guess)
        {
            StateVector target = r.Target.Ephemeris.StateAt(r.StartTime + guess);
            Vec3 aim = AimPoint(r.ShipPosition, target.Position, parkingRadius);
            return SolveTripTime(r.ShipPosition, r.ShipVelocity, aim, target.Velocity, r.Acceleration, r.MaxTripTime);
        }

        private static double SolveArrivalByBisection(TripRequest r, double parkingRadius, double maxT, ref int evaluations)
        {
            // h(T) = SolveForArrival(T) - T. Find the first sign change on a geometric grid, then bisect.
            double prevT = 1e-3, prevH = Residual(r, parkingRadius, prevT);
            evaluations++;
            for (double t = prevT * 1.05; t <= maxT * 1.05; t *= 1.05)
            {
                double tt = Math.Min(t, maxT);
                double h = Residual(r, parkingRadius, tt);
                evaluations++;
                if (!double.IsNaN(prevH) && !double.IsNaN(h) && (prevH > 0) != (h > 0))
                {
                    double lo = prevT, hi = tt, hLo = prevH;
                    for (int i = 0; i < 200 && hi - lo > 1e-6 + 1e-12 * hi; i++)
                    {
                        double mid = 0.5 * (lo + hi);
                        double hm = Residual(r, parkingRadius, mid);
                        evaluations++;
                        if (double.IsNaN(hm)) return double.NaN;
                        if ((hm > 0) == (hLo > 0)) { lo = mid; hLo = hm; } else hi = mid;
                    }

                    return 0.5 * (lo + hi);
                }

                prevT = tt;
                prevH = h;
                if (tt >= maxT) break;
            }

            return double.NaN;
        }

        private static double Residual(TripRequest r, double parkingRadius, double t)
        {
            double solved = SolveForArrival(r, parkingRadius, t);
            return double.IsNaN(solved) ? double.NaN : solved - t;
        }

        /// <summary>End the burn at the parking orbit's radius on the side of the planet facing the ship's start.</summary>
        private static Vec3 AimPoint(Vec3 start, Vec3 planet, double parkingRadius)
        {
            return planet + (start - planet).Normalized() * parkingRadius;
        }

        private static BurnProfile ProfileFor(Vec3 r0, Vec3 v0, Vec3 aim, Vec3 vEnd, double t)
        {
            Vec3 d = aim - r0 - (v0 + vEnd) * (t / 2);
            double dl = d.Length;
            return new BurnProfile
            {
                T = t,
                R0 = r0,
                V0 = v0,
                C = (vEnd - v0) / t,
                E = d.Normalized(),
                Ab = 4 * dl / (t * t),
                DLength = dl,
            };
        }

        // The stronger of the two burn halves: sqrt(|c|^2 + ab^2 + 2 ab |c.e|).
        private static double PeakAcceleration(BurnProfile p)
        {
            double ce = Math.Abs(Vec3.Dot(p.C, p.E));
            return Math.Sqrt(p.C.LengthSquared + p.Ab * p.Ab + 2 * p.Ab * ce);
        }

        /// <summary>Shortest trip time whose stronger burn half is at most <paramref name="accel"/>; NaN if none up to maxT.</summary>
        private static double SolveTripTime(Vec3 r0, Vec3 v0, Vec3 aim, Vec3 vEnd, double accel, double maxT)
        {
            // Already there, already matched.
            if ((aim - r0).Length < 1e-9 && (vEnd - v0).Length < 1e-12) return 0;

            // Peak acceleration falls off like 1/T^2 at first but need not be monotonic later, so scan upward for the
            // first T that is fast enough, then bisect back to the boundary.
            double prev = 1e-3;
            for (double t = prev * 1.05; t <= maxT * 1.05; t *= 1.05)
            {
                double tt = Math.Min(t, maxT);
                if (PeakAcceleration(ProfileFor(r0, v0, aim, vEnd, tt)) <= accel)
                {
                    double lo = prev, hi = tt;
                    for (int i = 0; i < 200 && hi - lo > 1e-9 + 1e-14 * hi; i++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (PeakAcceleration(ProfileFor(r0, v0, aim, vEnd, mid)) <= accel) hi = mid; else lo = mid;
                    }

                    return hi;
                }

                prev = tt;
                if (tt >= maxT) break;
            }

            return double.NaN;
        }

        private static void CheckSun(TripRequest r, BurnProfile p, TripPlan plan)
        {
            // Sample the path, then refine around the closest sample with golden-section search.
            double bestT = 0, best = p.StateAt(0).Position.Length;
            for (int i = 1; i <= PathSamples; i++)
            {
                double t = p.T * i / PathSamples;
                double dist = p.StateAt(t).Position.Length;
                if (dist < best)
                {
                    best = dist;
                    bestT = t;
                }
            }

            double lo = Math.Max(0, bestT - p.T / PathSamples), hi = Math.Min(p.T, bestT + p.T / PathSamples);
            const double phi = 0.6180339887498949;
            for (int i = 0; i < 100; i++)
            {
                double a = hi - phi * (hi - lo), b = lo + phi * (hi - lo);
                if (p.StateAt(a).Position.Length < p.StateAt(b).Position.Length) hi = b; else lo = a;
            }

            double refinedT = 0.5 * (lo + hi), refined = p.StateAt(refinedT).Position.Length;
            if (refined < best)
            {
                best = refined;
                bestT = refinedT;
            }

            plan.ClosestSunDistance = best;
            plan.ClosestSunTime = bestT;
            plan.PassesNearSun = best < r.SunSafetyDistance;

            if (r.SunGravParameter > 0)
            {
                double sunPull = best > 0 ? r.SunGravParameter / (best * best) : double.PositiveInfinity;
                plan.GravityNotNegligible = r.Acceleration < GravityWarningRatio * sunPull;
            }
        }
    }
}
