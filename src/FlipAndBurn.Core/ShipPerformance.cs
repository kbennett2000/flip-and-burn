using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipAndBurn.Core
{
    /// <summary>
    /// One stretch of burning with a fixed set of engines: from <see cref="StartMass"/> until the first propellant they
    /// need runs out, at <see cref="EndMass"/>.
    /// </summary>
    public sealed class BurnPhase
    {
        public double Thrust { get; }               // N, at 100 %
        public double VacuumIsp { get; }            // s, at full thrust
        public double IspThrottleExponent { get; }  // see ShipPerformance.IspThrottleExponent
        public double StartMass { get; }            // kg
        public double EndMass { get; }              // kg

        public BurnPhase(double thrust, double vacuumIsp, double ispThrottleExponent, double startMass, double endMass)
        {
            Thrust = thrust;
            VacuumIsp = vacuumIsp;
            IspThrottleExponent = ispThrottleExponent;
            StartMass = startMass;
            EndMass = endMass;
        }
    }

    /// <summary>What the ship's engines and fuel can do, as the planner needs it.</summary>
    /// <remarks>
    /// Usually one burn phase. With several kinds of engine on different propellants, one kind can run dry first and
    /// the others carry on with less thrust: each such stretch is a phase (see <see cref="ShipEngines"/>).
    /// </remarks>
    public sealed class ShipPerformance
    {
        /// <summary>KSP's standard gravity (PhysicsGlobals.GravitationalAcceleration), used in the rocket equation.</summary>
        public const double StandardGravity = 9.80665;

        private readonly List<BurnPhase> phases;

        public IReadOnlyList<BurnPhase> Phases => phases;

        /// <summary>All active engines at 100 % (N), at the start.</summary>
        public double FullThrust => phases[0].Thrust;
        /// <summary>Combined vacuum Isp at the start, at full thrust (s).</summary>
        public double VacuumIsp => phases[0].VacuumIsp;
        /// <summary>
        /// How Isp falls at part throttle: Isp = VacuumIsp * throttle^k. 0 for stock engines. KSP Interstellar Extended's
        /// fusion drives (DaedalusEngineController, which the Kerbstein drive uses) set k = ispThrottleExponent, 0.5 by
        /// default.
        /// </summary>
        public double IspThrottleExponent => phases[0].IspThrottleExponent;
        public double StartMass => phases[0].StartMass;               // kg
        public double EndMass => phases[phases.Count - 1].EndMass;   // kg, with all usable propellant burned

        public ShipPerformance(double fullThrust, double vacuumIsp, double ispThrottleExponent, double startMass, double endMass)
            : this(new[] { new BurnPhase(fullThrust, vacuumIsp, ispThrottleExponent, startMass, endMass) })
        {
        }

        public ShipPerformance(IEnumerable<BurnPhase> burnPhases)
        {
            phases = burnPhases?.ToList() ?? new List<BurnPhase>();
            if (phases.Count == 0) throw new ArgumentException("at least one phase", nameof(burnPhases));
        }

        public bool HasThrust => FullThrust > 0 && VacuumIsp > 0 && StartMass > 0;

        /// <summary>Highest acceleration the ship can make right now (m/s^2).</summary>
        public double MaxAcceleration => StartMass > 0 ? FullThrust / StartMass : 0;

        /// <summary>Delta-v burning everything at full thrust (m/s).</summary>
        public double FullThrustDeltaV
        {
            get
            {
                if (!HasThrust) return 0;
                double dv = 0;
                foreach (BurnPhase p in phases)
                    if (p.Thrust > 0 && p.VacuumIsp > 0 && p.EndMass > 0 && p.EndMass < p.StartMass)
                        dv += p.VacuumIsp * StandardGravity * Math.Log(p.StartMass / p.EndMass);
                return dv;
            }
        }

        /// <summary>
        /// Delta-v while throttling to hold a constant <paramref name="acceleration"/> (m/s^2) until the propellant runs out
        /// (or, with several phases, until the remaining engines can't hold it). Returns 0 when the ship can't reach that
        /// acceleration at all.
        /// </summary>
        /// <remarks>
        /// Holding acceleration a needs throttle t = a m / F, which falls as the ship gets lighter. With
        /// Isp = Isp0 * t^k, dv = -g0 Isp0 (a m / F)^k dm / m, so
        /// delta-v = g0 Isp0 (a / F)^k (m0^k - m1^k) / k, and g0 Isp0 ln(m0 / m1) when k = 0.
        /// </remarks>
        public double DeltaVAtAcceleration(double acceleration)
        {
            if (!HasThrust || !(acceleration > 0)) return 0;
            if (acceleration > MaxAcceleration * (1 + 1e-12)) return 0;

            double dv = 0;
            foreach (BurnPhase p in phases)
            {
                if (!(p.Thrust > 0) || !(p.VacuumIsp > 0) || !(p.EndMass > 0) || p.EndMass >= p.StartMass) continue;
                if (acceleration > p.Thrust / p.StartMass * (1 + 1e-12)) break;   // these engines can't hold it
                double k = p.IspThrottleExponent;
                double g0Isp = StandardGravity * p.VacuumIsp;
                dv += Math.Abs(k) < 1e-12
                    ? g0Isp * Math.Log(p.StartMass / p.EndMass)
                    : g0Isp * Math.Pow(acceleration / p.Thrust, k) * (Math.Pow(p.StartMass, k) - Math.Pow(p.EndMass, k)) / k;
            }

            return dv;
        }
    }
}
