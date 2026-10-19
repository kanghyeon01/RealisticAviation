using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>An airport's ATIS as the tower needs it for "with information X".</summary>
    public struct AtisState
    {
        /// <summary>The current information letter: 0 = Alpha ... 25 = Zulu.</summary>
        public int m_Letter;

        /// <summary>The letter before it, or -1 if there was none.</summary>
        public int m_PreviousLetter;

        /// <summary>The QNH of the current information, in hectopascals, for "information Charlie now current, QNH 1009".</summary>
        public int m_Qnh;

        /// <summary>Game minutes since the current letter was recorded.</summary>
        public float m_MinutesSinceChange;
    }

    /// <summary>
    /// The link to an ATIS mod (Aviation Weather Add-on), which registers <see cref="Current"/> when it loads. Realistic
    /// Aviation cannot reference that mod, so the call goes this way round. Without it the radio works as before, with no
    /// "with information X".
    /// </summary>
    public static class AtisLink
    {
        public delegate bool Provider(Entity airport, out AtisState state);

        /// <summary>The airport's ATIS, if it has one yet. Called on the main thread, once per airport each tower update.</summary>
        public static Provider Current;
    }
}
