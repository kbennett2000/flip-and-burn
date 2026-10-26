using System;
using FlipAndBurn.Core;
using FlipAndBurn.Core.Guidance;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    /// <summary>Fast checks of the pieces the guidance is built from.</summary>
    public class GuidancePartsTests
    {
        [Fact]
        public void TwoBody_MatchesKeplerOrbit()
        {
            KeplerOrbit duna = KerbolSystem.Duna.Orbit;
            StateVector start = duna.StateAt(1000);
            foreach (double dt in new[] { 60.0, 3600, 86_400, 0.37 * duna.Period, 1.9 * duna.Period })
            {
                StateVector propagated = TwoBody.Propagate(start.Position, start.Velocity, KerbolSystem.SunGravParameter, dt);
                StateVector expected = duna.StateAt(1000 + dt);
                Assert.True((propagated.Position - expected.Position).Length < 1e-6 * duna.SemiMajorAxis, $"position after {dt} s");
                Assert.True((propagated.Velocity - expected.Velocity).Length < 1e-6 * expected.Velocity.Length, $"velocity after {dt} s");
            }
        }

        [Fact]
        public void TwoBody_HyperbolaKeepsEnergyAndAngularMomentum()
        {
            double mu = KerbolSystem.Jool.GravParameter;
            var r0 = new Vec3(5e8, 1e7, 0);
            var v0 = new Vec3(-12_000, 300, 50);
            double e0 = v0.LengthSquared / 2 - mu / r0.Length;
            Vec3 h0 = Vec3.Cross(r0, v0);
            Assert.True(e0 > 0);
            foreach (double dt in new[] { 1000.0, 30_000, 60_000, 200_000 })
            {
                StateVector s = TwoBody.Propagate(r0, v0, mu, dt);
                double e = s.Velocity.LengthSquared / 2 - mu / s.Position.Length;
                Assert.True(Math.Abs(e / e0 - 1) < 1e-8, $"energy after {dt} s");
                Assert.True((Vec3.Cross(s.Position, s.Velocity) - h0).Length < 1e-8 * h0.Length, $"angular momentum after {dt} s");
            }
        }

        [Fact]
        public void TwoBody_FastFlybyFarOut()
        {
            // A coasting ship entering Jool's sphere of influence at 55 km/s: a hyperbola with eccentricity in the
            // thousands. Compare an hour's propagation with a finely stepped integration.
            double mu = KerbolSystem.Jool.GravParameter;
            foreach ((Vec3 r0, Vec3 v0) in new[]
            {
                (new Vec3(1.5e9, 2e7, 1e6), new Vec3(-55_600, -300, 20)),
                // Aimed almost straight at Jool (from the simulator, where plain Newton ran away).
                (new Vec3(-857941975.0636597, -973991422.2126846, -2823612.8127851486), new Vec3(36662.689142536234, 41811.18259189171, 122.33117572269087)),
            })
            foreach (double dt in new[] { 600.0, 3600, 7200 })
            {
                StateVector s = TwoBody.Propagate(r0, v0, mu, dt);
                Vec3 r = r0, v = v0;
                int n = (int)(dt / 0.5);
                double h = dt / n;
                for (int i = 0; i < n; i++)
                {
                    Vec3 Acc(Vec3 p) => p * (-mu / Math.Pow(p.Length, 3));
                    Vec3 k1v = Acc(r), k1r = v;
                    Vec3 k2v = Acc(r + k1r * (h / 2)), k2r = v + k1v * (h / 2);
                    Vec3 k3v = Acc(r + k2r * (h / 2)), k3r = v + k2v * (h / 2);
                    Vec3 k4v = Acc(r + k3r * h), k4r = v + k3v * h;
                    r += (k1r + k2r * 2 + k3r * 2 + k4r) * (h / 6);
                    v += (k1v + k2v * 2 + k3v * 2 + k4v) * (h / 6);
                }

                Assert.True((s.Position - r).Length < 1.0, $"position after {dt} s off by {(s.Position - r).Length} m");
                Assert.True((s.Velocity - v).Length < 1e-3, $"velocity after {dt} s off by {(s.Velocity - v).Length} m/s");
            }
        }

        [Fact]
        public void TwoBody_ShapeOfACircularOrbit()
        {
            double mu = KerbolSystem.Kerbin.GravParameter, r = 700_000;
            OrbitShape shape = TwoBody.Shape(new Vec3(r, 0, 0), new Vec3(0, Math.Sqrt(mu / r), 0), mu);
            Assert.True(shape.Eccentricity < 1e-12);
            Assert.Equal(r, shape.PeriapsisRadius, 6);
            Assert.True(shape.Normal.Z > 0.999999, "prograde about +Z");
        }

        [Fact]
        public void TwoArcSolver_IsTheTextbookFlipAndBurnAtRest()
        {
            // Start and end at rest, 1e10 m apart: t1 = t2 = sqrt(d / a), equal and opposite thrust.
            const double a = 9.81, d = 1e10;
            var solver = new TwoArcSolver(_ => new StateVector(new Vec3(d, 0, 0), Vec3.Zero), a, a);
            TwoArcSolver.Arcs arcs = solver.Solve(0, Vec3.Zero, Vec3.Zero);
            Assert.True(arcs.Valid);
            Assert.Equal(Math.Sqrt(d / a), arcs.T1, 3);
            Assert.Equal(Math.Sqrt(d / a), arcs.T2, 3);
            Assert.Equal(a, arcs.A1.Length, 9);
            Assert.True(arcs.A1.X > 0 && arcs.A2.X < 0);
        }

        [Fact]
        public void TwoArcSolver_WithACoastStillArrives()
        {
            const double a = 9.81, d = 1e10, coast = 40;
            var end = new StateVector(new Vec3(d, 0, 0), Vec3.Zero);
            var solver = new TwoArcSolver(_ => end, a, 0.99 * a);
            TwoArcSolver.Arcs arcs = solver.Solve(0, Vec3.Zero, new Vec3(0, 3000, 0), coast);
            Assert.True(arcs.Valid);
            Assert.Equal(a, arcs.A1.Length, 6);
            Assert.Equal(0.99 * a, arcs.A2.Length, 6);

            // Fly it exactly: arc, coast, arc.
            Vec3 r = Vec3.Zero, v = new Vec3(0, 3000, 0);
            r += v * arcs.T1 + arcs.A1 * (0.5 * arcs.T1 * arcs.T1);
            v += arcs.A1 * arcs.T1;
            r += v * arcs.Tc;
            r += v * arcs.T2 + arcs.A2 * (0.5 * arcs.T2 * arcs.T2);
            v += arcs.A2 * arcs.T2;
            Assert.True((r - end.Position).Length < 1e-3, $"ends {(r - end.Position).Length} m away");
            Assert.True(v.Length < 1e-6, $"ends moving {v.Length} m/s");
        }

        [Fact]
        public void Guidance_ReportsPhasesInOrder()
        {
            // A quick flight at high acceleration with big steps, just to see the phase sequence.
            FlightResult r = Flights.Fly("phases", KerbolSystem.Duna, 20.0, FlipManeuver.CutFlipRelight, 0, 60_000, cruiseWarp: 50);
            string log = string.Join("\n", r.PhaseLog);
            int accelerate = log.IndexOf("Accelerate", StringComparison.Ordinal);
            int flip = log.IndexOf("Flip", StringComparison.Ordinal);
            int decelerate = log.IndexOf("Decelerate", StringComparison.Ordinal);
            int final = log.IndexOf("FinalApproach", StringComparison.Ordinal);
            int done = log.IndexOf("Done", StringComparison.Ordinal);
            Assert.True(accelerate >= 0 && accelerate < flip && flip < decelerate && decelerate < final && final < done, log);
            Assert.True(r.Finished);
        }
    }
}
