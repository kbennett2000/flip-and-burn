using System;
using FlipAndBurn.Core.Guidance;
using Xunit;
using Xunit.Abstractions;

namespace FlipAndBurn.Core.Tests
{
    public class MoonTripExperiment
    {
        private readonly ITestOutputHelper output;
        public MoonTripExperiment(ITestOutputHelper output) { this.output = output; }

        [Fact(Skip = "trace")]
        public void KerbinToMun_Trace()
        {
            SimSystem sys = SimSystem.Around(KerbolSystem.Kerbin, KerbolSystem.Kerbin, 80_000, Math.PI / 2);
            FlightResult f = Flights.Fly("90", sys.TargetRecord(KerbolSystem.Mun), 1.0, FlipManeuver.CutFlipRelight,
                0, 20_000, system: sys, wallSeconds: 60, traceFrom: 2250, traceTo: 2400, traceEvery: 2);
            foreach (string l in f.PhaseLog) output.WriteLine(l);
            foreach (string l in f.Trace) output.WriteLine(l);
            output.WriteLine(f.SolverStats);
        }

        [Fact]
        public void KerbinToMun_Probe()
        {
            for (int k = 0; k < 8; k++)
            {
                double angle = k * Math.PI / 4;
                SimSystem sys = SimSystem.Around(KerbolSystem.Kerbin, KerbolSystem.Kerbin, 80_000, angle);
                FlightResult f = Flights.Fly($"Kerbin->Mun {k * 45} deg", sys.TargetRecord(KerbolSystem.Mun), 1.0, FlipManeuver.CutFlipRelight,
                    0, 20_000, system: sys, wallSeconds: 60);
                output.WriteLine($"{f.Name}: finished {f.Finished} safe {f.EndedInSafeOrbit} {f.SafeOrbitReason}; time {f.TripTime:F0} s (planned {f.PlannedTripTime:F0}); " +
                                 $"dv {f.DeltaVUsed:F0} (planned {f.PlannedDeltaV:F0}); Pe {f.FinalPeriapsisAltitude / 1000:F1} Ap {f.FinalApoapsisAltitude / 1000:F1} km; " +
                                 $"clear of obstacles by {f.ClosestToObstacle / 1000:F1} km; legs {f.Legs}; wall {f.WallSeconds:F1} s");
                foreach (string l in f.PhaseLog) output.WriteLine("   " + l);
            }
        }
    }
}
