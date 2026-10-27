using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>What to do with the engines this physics step.</summary>
    public enum EngineAction
    {
        /// <summary>Burn at <see cref="EngineCommand.Throttle"/>.</summary>
        Burn,
        /// <summary>Thrust is wanted but the ship isn't pointed yet: engines stay lit, throttle at zero.</summary>
        HoldAtZero,
        /// <summary>No thrust wanted (the cut in a cut, flip, relight; the coast): shut the engines down.</summary>
        Cut,
    }

    /// <summary>
    /// The engine decision each physics step (ADR 0003, "Engine off means off"), kept free of KSP so it can be tested.
    /// The autopilot applies it to MechJeb's throttle and to each engine's own throttle (the Kerbstein drive doesn't always
    /// follow the ship's throttle by itself).
    /// </summary>
    public struct EngineCommand
    {
        public EngineAction Action;
        /// <summary>0 to 1: the share of full thrust that gives the commanded acceleration.</summary>
        public float Throttle;

        /// <param name="commandedAcceleration">From the guidance (m/s^2); 0 means no thrust wanted.</param>
        /// <param name="flipping">In the flip: thrust (a skew flip) goes on while turning, whatever the pointing.</param>
        /// <param name="pointingErrorDeg">Angle between where the ship points and the commanded direction.</param>
        /// <param name="toleranceDeg">Outside the flip, thrust only within this angle.</param>
        /// <param name="fullThrustAcceleration">All running engines at 100 % over the ship's mass (m/s^2).</param>
        public static EngineCommand Decide(double commandedAcceleration, bool flipping, double pointingErrorDeg, double toleranceDeg,
            double fullThrustAcceleration)
        {
            if (!(commandedAcceleration > 0)) return new EngineCommand { Action = EngineAction.Cut, Throttle = 0 };
            bool pointed = flipping || pointingErrorDeg <= toleranceDeg;
            if (!pointed) return new EngineCommand { Action = EngineAction.HoldAtZero, Throttle = 0 };
            float throttle = fullThrustAcceleration > 0
                ? (float)Math.Max(0, Math.Min(1, commandedAcceleration / fullThrustAcceleration))
                : 0f;
            return new EngineCommand { Action = EngineAction.Burn, Throttle = throttle };
        }
    }
}
