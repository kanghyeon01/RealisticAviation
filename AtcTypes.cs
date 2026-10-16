using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    public enum Clearance : byte
    {
        /// <summary>Asked for the runway; holding short (on the ground) or holding at the fix (in the air).</summary>
        Waiting,

        /// <summary>Cleared to enter the runway and stop lined up on it, behind traffic still on the runway.</summary>
        LineUp,

        /// <summary>Cleared to land, take off or cross; the runway is reserved for this airplane.</summary>
        Cleared,
    }

    /// <summary>Tower bookkeeping for one airplane that has asked for a runway. Not saved; rebuilt after loading.</summary>
    public struct Flight
    {
        /// <summary>Key of the runway (see <see cref="AtcSystem"/>) this airplane asked for.</summary>
        public Entity m_Runway;

        /// <summary>Running request number, for the log.</summary>
        public uint m_Sequence;

        public uint m_RequestFrame;

        public uint m_ClearedFrame;

        public uint m_LastTick;

        /// <summary>Path distance to the runway in the last tick (diagnostics only).</summary>
        public float m_LastDistance;

        public bool m_LastFlying;

        /// <summary>Estimated seconds until an airborne airplane reaches the runway, from the last tick.</summary>
        public float m_LastEta;

        public Clearance m_Clearance;

        /// <summary>The airplane has been on the runway since it was cleared.</summary>
        public bool m_SeenOnRunway;

        /// <summary>The airplane is flying the holding pattern below.</summary>
        public bool m_Holding;

        /// <summary>A cleared arrival has touched down (line up and wait may then start behind it).</summary>
        public bool m_TouchedDown;

        public byte m_StackLevel;

        public HoldingPattern m_Pattern;

        /// <summary>The game's own steering target and direction when the hold began; restored when it ends.</summary>
        public float3 m_ResumeTarget;

        public float3 m_ResumeDirection;

        /// <summary>Where a ground airplane's path enters the runway and the direction it then rolls (from the last tick).</summary>
        public float3 m_EntryPosition;

        public float2 m_EntryDirection;

        /// <summary>The last tick the airplane was on its runway.</summary>
        public uint m_OnRunwayTick;
    }

    /// <summary>
    /// One runway: every runway edge connected through shared nodes (so crossing runways count as one),
    /// together with those nodes. Only one airplane may use it at a time.
    /// </summary>
    public struct RunwayState
    {
        /// <summary>The ground airplane currently cleared to take off from or cross the runway, or Entity.Null.</summary>
        public Entity m_Holder;

        /// <summary>
        /// The airplane cleared to land, or Entity.Null. While it is still far out, ground airplanes may be cleared
        /// ahead of it (see <see cref="AtcSettings.m_DepartureGapSeconds"/>).
        /// </summary>
        public Entity m_Arrival;

        /// <summary>The ground airplane cleared to line up and wait on the runway, or Entity.Null.</summary>
        public Entity m_LineUp;

        /// <summary>The airport this runway belongs to; its <see cref="LineUpAndWaitAirport"/> tag enables line up and wait.</summary>
        public Entity m_Airport;

        /// <summary>Holding altitudes in use above this runway, one bit per stack level.</summary>
        public ulong m_StackMask;

        /// <summary>Airplanes physically on the runway's lanes; only valid when <see cref="m_OccupiedTick"/> is the current tick.</summary>
        public int m_Occupants;

        public uint m_OccupiedTick;

        /// <summary>
        /// No ground airplane is cleared or lined up, and nobody is physically on the runway (a cleared arrival may be inbound).
        /// </summary>
        public bool IsFree(uint tick) => m_Holder == Entity.Null && m_LineUp == Entity.Null && Occupants(tick) == 0;

        public int Occupants(uint tick) => m_OccupiedTick == tick ? m_Occupants : 0;
    }

    /// <summary>The mod options, copied into a plain struct so jobs can use them.</summary>
    public struct AtcSettings
    {
        /// <summary>In metres (the option itself is in nautical miles).</summary>
        public float m_ContactDistance;

        /// <summary>Straight-leg length of the holding pattern, in metres (the option itself is in nautical miles).</summary>
        public float m_HoldingLegLength;

        public float m_HoldShortSetback;

        /// <summary>A ground airplane may go ahead of an arrival that is at least this many seconds from the runway.</summary>
        public float m_DepartureGapSeconds;

        /// <summary>Gap a taxiing airplane keeps between its nose and the tail of the airplane ahead, in metres.</summary>
        public float m_TaxiSpacing;

        public AtcSettings(Setting setting)
        {
            m_TaxiSpacing = setting.TaxiSpacing;
            m_DepartureGapSeconds = setting.DepartureGapSeconds;
            m_ContactDistance = setting.ContactDistanceNm * Setting.kMetersPerNauticalMile;
            m_HoldingLegLength = setting.HoldingLegNm * Setting.kMetersPerNauticalMile;
            m_HoldShortSetback = setting.HoldShortSetback;
        }
    }

    /// <summary>What one tick found out about one airplane.</summary>
    internal struct FlightScan
    {
        public Entity m_Entity;

        /// <summary>Runway the airplane is on right now, or Entity.Null.</summary>
        public Entity m_Occupied;

        /// <summary>Next runway along the airplane's path (not counting the one it is on), or Entity.Null.</summary>
        public Entity m_Upcoming;

        /// <summary>Path distance to the first lane of <see cref="m_Upcoming"/>.</summary>
        public float m_Distance;

        /// <summary>Path distance at which this airplane asks for its next runway.</summary>
        public float m_RequestDistance;

        /// <summary>Where the path enters <see cref="m_Upcoming"/>, and the unit direction it travels there.</summary>
        public float3 m_EntryPosition;

        public float2 m_EntryDirection;

        public bool m_Flying;

        /// <summary>A new path is being searched, so the path buffers can't be trusted this tick.</summary>
        public bool m_PathUnknown;
    }

    /// <summary>
    /// For one runway: the waiting arrival with the highest response ratio, and the waiting ground airplane closest to the
    /// runway (the head of the taxiway queue; one further back can't go first anyway). The ground queue's ratio uses
    /// the longest wait in it, so airplanes stuck behind the head still age the whole queue.
    /// </summary>
    internal struct Candidates
    {
        public Entity m_Air;

        public float m_AirRatio;

        public Entity m_Ground;

        public float m_GroundDistance;

        public uint m_OldestGroundRequestFrame;
    }
}
