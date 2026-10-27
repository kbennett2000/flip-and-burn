using System;

namespace FlipAndBurn.Core.Guidance
{
    /// <summary>
    /// The player's physics warp rates, saved at Start so a trip can put them back exactly (ADR 0003, "Time warp").
    /// </summary>
    /// <remarks>
    /// Keeps the array itself and a copy of its values. The array matters as well as the values: BetterTimeWarp hands KSP
    /// its own preset array, so the game's rates and the preset are one object. Restoring hands back that same object with
    /// the values it had at Start, even if something wrote into it during the trip.
    /// </remarks>
    public sealed class WarpRatesSnapshot
    {
        private readonly float[] values;

        private WarpRatesSnapshot(float[] array)
        {
            Array = array;
            values = (float[])array.Clone();
        }

        /// <summary>The player's own array, as it was handed to the game.</summary>
        public float[] Array { get; }

        /// <summary>Null when there are no rates to save.</summary>
        public static WarpRatesSnapshot Take(float[] current) => current == null ? null : new WarpRatesSnapshot(current);

        /// <summary>Put the saved values back into the player's array and return it, for the game to use again.</summary>
        public float[] Restore()
        {
            if (Array.Length == values.Length) values.CopyTo(Array, 0);
            return Array;
        }

        /// <summary>True when <paramref name="now"/> is the player's array with the values it had at Start.</summary>
        public bool Matches(float[] now)
        {
            if (!ReferenceEquals(now, Array) || now.Length != values.Length) return false;
            for (int i = 0; i < values.Length; i++)
                if (now[i] != values[i]) return false;
            return true;
        }

        public override string ToString() => "[" + string.Join(",", values) + "]";
    }
}
