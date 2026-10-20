namespace RealisticAviation
{
    /// <summary>An airport's ATIS as the tower needs it for "with information X"; given by an <see cref="Api.AtisProvider"/>.</summary>
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
}
