using System;

namespace FlipAndBurn.Core.Guidance
{
    public enum WarpMode
    {
        /// <summary>Normal speed.</summary>
        None,
        /// <summary>Physics warp: the only warp an engine can fire in (KSP Interstellar drives would fire at full
        /// thrust in normal warp; stock engines can't fire at all).</summary>
        Physics,
        /// <summary>Normal ("on rails") warp: engine off.</summary>
        Rails,
    }

    public struct WarpDecision
    {
        public WarpMode Mode;
        /// <summary>The highest warp rate allowed right now (1 = normal speed).</summary>
        public double MaxRate;
        /// <summary>Plain words for the window: what warp is doing and why.</summary>
        public string Reason;
    }

    /// <summary>
    /// How fast Flip and Burn may warp time during a trip (ADR 0003, "Time warp").
    /// </summary>
    /// <remarks>
    /// Physics warp only while the engine is firing; normal warp only while coasting with the engine off. Either only once
    /// the ship points where it should: it can't turn on rails, and turns badly in coarse physics-warp steps. The rate is capped so the next event (the flip,
    /// the braking burn, the arrival) is always at least a set number of real seconds away: warp falls steadily as an
    /// event nears, and is back to normal speed in time. Turning (the flip, a course correction) and the last
    /// circularizing burn run at normal speed.
    /// </remarks>
    public static class WarpPolicy
    {
        /// <summary>Physics warp keeps the next event at least this many real seconds away.</summary>
        public const double PhysicsLeadSeconds = 30;
        /// <summary>Normal warp keeps the braking burn at least this many real seconds away (leaving rails takes a moment).</summary>
        public const double RailsLeadSeconds = 60;

        public static WarpDecision Decide(GuidanceCommand cmd, bool pointed)
        {
            if (cmd == null) return Normal("not flying");
            switch (cmd.Phase)
            {
                case GuidancePhase.Done:
                    return Normal("arrived");
                case GuidancePhase.Flip:
                    return Normal("flipping");
            }

            if (cmd.SafeOrbit) return Normal("making the orbit safe");
            if (cmd.Circularizing) return Normal("final burn into the parking orbit");
            if (cmd.Correcting) return Normal("course correction");

            // Turning needs normal speed: under physics warp the attitude control gets coarse steps and the ship falls behind.
            if (!pointed) return Normal(cmd.Phase == GuidancePhase.Coast ? "turning to the braking direction" : "turning to the burn direction");

            switch (cmd.Phase)
            {
                case GuidancePhase.Coast:
                    return Capped(WarpMode.Rails, cmd.TimeToBurn, RailsLeadSeconds, "coasting, engine off", "braking burn");
                case GuidancePhase.Accelerate:
                    return Capped(WarpMode.Physics, cmd.TimeToFlip, PhysicsLeadSeconds, "speeding up", "flip");
                default: // Decelerate, FinalApproach
                    return Capped(WarpMode.Physics, cmd.TimeToArrival, PhysicsLeadSeconds, "slowing down", "arrival");
            }
        }

        private static WarpDecision Normal(string why) => new WarpDecision { Mode = WarpMode.None, MaxRate = 1, Reason = why };

        private static WarpDecision Capped(WarpMode mode, double timeToEvent, double lead, string doing, string eventName)
        {
            double rate = timeToEvent > 0 ? timeToEvent / lead : 0;
            if (!(rate > 1)) return Normal($"{doing}; {eventName} coming up");
            return new WarpDecision { Mode = mode, MaxRate = rate, Reason = $"{doing}; {eventName} in {timeToEvent:N0} s" };
        }

        /// <summary>The highest of <paramref name="rates"/> (index 0 = 1x) not above <paramref name="maxRate"/> and <paramref name="cap"/>.</summary>
        public static int PickIndex(float[] rates, double maxRate, double cap = double.MaxValue)
        {
            int best = 0;
            if (rates == null) return 0;
            for (int i = 1; i < rates.Length; i++)
                if (rates[i] <= maxRate && rates[i] <= cap && rates[i] > rates[best]) best = i;
            return best;
        }
    }
}
