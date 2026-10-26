using System;

namespace FlipAndBurn.Core
{
    /// <summary>What the ship's engines and fuel can do, as the planner needs it.</summary>
    public sealed class ShipPerformance
    {
        /// <summary>KSP's standard gravity (PhysicsGlobals.GravitationalAcceleration), used in the rocket equation.</summary>
        public const double StandardGravity = 9.80665;

        public double FullThrust { get; }        // N, all active engines at 100 %
        public double VacuumIsp { get; }         // s, at full thrust
        /// <summary>
        /// How Isp falls at part throttle: Isp = VacuumIsp * throttle^k. 0 for stock engines. KSP Interstellar Extended's
        /// fusion drives (DaedalusEngineController, which the Kerbstein drive uses) set k = ispThrottleExponent, 0.5 by
        /// default.
        /// </summary>
        public double IspThrottleExponent { get; }
        public double StartMass { get; }         // kg
        public double EndMass { get; }           // kg, with all usable propellant burned

        public ShipPerformance(double fullThrust, double vacuumIsp, double ispThrottleExponent, double startMass, double endMass)
        {
            FullThrust = fullThrust;
            VacuumIsp = vacuumIsp;
            IspThrottleExponent = ispThrottleExponent;
            StartMass = startMass;
            EndMass = endMass;
        }

        public bool HasThrust => FullThrust > 0 && VacuumIsp > 0 && StartMass > 0;

        /// <summary>Highest acceleration the ship can make right now (m/s^2).</summary>
        public double MaxAcceleration => StartMass > 0 ? FullThrust / StartMass : 0;

        /// <summary>Delta-v burning everything at full thrust (m/s).</summary>
        public double FullThrustDeltaV => HasThrust && EndMass > 0 && EndMass < StartMass
            ? VacuumIsp * StandardGravity * Math.Log(StartMass / EndMass)
            : 0;

        /// <summary>
        /// Delta-v while throttling to hold a constant <paramref name="acceleration"/> (m/s^2) until the propellant runs out.
        /// Returns 0 when the ship can't reach that acceleration at all.
        /// </summary>
        /// <remarks>
        /// Holding acceleration a needs throttle t = a m / F, which falls as the ship gets lighter. With
        /// Isp = Isp0 * t^k, dv = -g0 Isp0 (a m / F)^k dm / m, so
        /// delta-v = g0 Isp0 (a / F)^k (m0^k - m1^k) / k, and g0 Isp0 ln(m0 / m1) when k = 0.
        /// </remarks>
        public double DeltaVAtAcceleration(double acceleration)
        {
            if (!HasThrust || !(acceleration > 0) || !(EndMass > 0) || EndMass >= StartMass) return 0;
            if (acceleration > MaxAcceleration * (1 + 1e-12)) return 0;

            double k = IspThrottleExponent;
            double g0Isp = StandardGravity * VacuumIsp;
            if (Math.Abs(k) < 1e-12) return g0Isp * Math.Log(StartMass / EndMass);
            return g0Isp * Math.Pow(acceleration / FullThrust, k) * (Math.Pow(StartMass, k) - Math.Pow(EndMass, k)) / k;
        }
    }
}
