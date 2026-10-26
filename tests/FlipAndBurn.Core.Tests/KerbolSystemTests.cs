using System;
using FlipAndBurn.Core;
using Xunit;

namespace FlipAndBurn.Core.Tests
{
    public class KerbolSystemTests
    {
        // Sphere of influence of each Sun-orbiting body as KSP computed it at runtime, logged by Kopernicus in
        // <KSP>/Logs/Kopernicus/Kopernicus.log ("Found Body: <name>:<index> -> SOI = ..."), run of 2026-10-26.
        [Theory]
        [InlineData("Moho", 9646663.02332811)]
        [InlineData("Eve", 85109364.7382441)]
        [InlineData("Kerbin", 84159286.4796305)]
        [InlineData("Duna", 47921949.369738)]
        [InlineData("Dres", 32832839.5767762)]
        [InlineData("Jool", 2455985185.42347)]
        [InlineData("Sarnus", 2740126708.15753)]
        [InlineData("Urlum", 2562260673.07757)]
        [InlineData("Neidon", 4415723830.49107)]
        [InlineData("Plock", 612762335.918708)]
        public void PlanetData_ReproducesKspRuntimeSphereOfInfluence(string name, double runtimeSoi)
        {
            PlanetRecord p = Array.Find(KerbolSystem.OpmSunPlanets, x => x.Name == name);
            Assert.NotNull(p);
            Assert.True(Math.Abs(p.SphereOfInfluence / runtimeSoi - 1) < 1e-12, $"{name}: {p.SphereOfInfluence:R} vs {runtimeSoi:R}");
        }

        [Fact]
        public void KeplerOrbit_ConservesEnergy_AndRepeatsEachPeriod()
        {
            foreach (PlanetRecord p in KerbolSystem.OpmSunPlanets)
            {
                KeplerOrbit o = p.Orbit;
                double mu = o.ParentGravParameter, expected = -mu / (2 * o.SemiMajorAxis);
                for (double ut = 0; ut < o.Period; ut += o.Period / 37)
                {
                    StateVector s = o.StateAt(ut);
                    double energy = s.Velocity.LengthSquared / 2 - mu / s.Position.Length;
                    Assert.True(Math.Abs(energy / expected - 1) < 1e-9, $"{p.Name} energy at {ut}");

                    StateVector later = o.StateAt(ut + o.Period);
                    Assert.True((later.Position - s.Position).Length < 1e-6 * o.SemiMajorAxis, $"{p.Name} position after one period");
                }
            }
        }

        [Fact]
        public void KeplerOrbit_PerihelionAndAphelion()
        {
            KeplerOrbit moho = KerbolSystem.Moho.Orbit;
            double min = double.MaxValue, max = 0;
            for (double ut = 0; ut < moho.Period; ut += moho.Period / 20000)
            {
                double r = moho.StateAt(ut).Position.Length;
                min = Math.Min(min, r);
                max = Math.Max(max, r);
            }

            Assert.True(Math.Abs(min / (moho.SemiMajorAxis * (1 - moho.Eccentricity)) - 1) < 1e-6);
            Assert.True(Math.Abs(max / (moho.SemiMajorAxis * (1 + moho.Eccentricity)) - 1) < 1e-6);
        }

        [Fact]
        public void KeplerOrbit_InclinationTiltsTheOrbitPlane()
        {
            KeplerOrbit moho = KerbolSystem.Moho.Orbit;  // 7 degrees
            double maxZ = 0;
            for (double ut = 0; ut < moho.Period; ut += moho.Period / 2000)
            {
                Vec3 r = moho.StateAt(ut).Position;
                maxZ = Math.Max(maxZ, Math.Abs(r.Z) / r.Length);
            }

            Assert.True(Math.Abs(maxZ - Math.Sin(7 * Math.PI / 180)) < 1e-4, $"max |z|/r = {maxZ}");
        }
    }
}
