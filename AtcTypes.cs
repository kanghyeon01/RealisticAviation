using System;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>The weather behind the weather go-around chance (AtcSystem.GetSettings).</summary>
    internal enum WeatherKind : byte
    {
        Clear,
        Rain,
        Snow,
        Fog,
        Thunderstorm,
    }

    /// <summary>Kinds of disaster the tower may close an airport for (chosen in the options).</summary>
    [Flags]
    internal enum HazardKinds : byte
    {
        None = 0,
        Tornado = 1,
        Hail = 2,

        /// <summary>Every other disaster event: thunderstorms, and anything else that puts the airport in danger.</summary>
        Other = 4,
    }

    internal enum Clearance : byte
    {
        /// <summary>Asked for the runway; holding short (on the ground) or holding at the fix (in the air).</summary>
        Waiting,

        /// <summary>Cleared to enter the runway and stop lined up on it, behind traffic still on the runway.</summary>
        LineUp,

        /// <summary>Cleared to land, take off or cross; the runway is reserved for this airplane.</summary>
        Cleared,
    }

    /// <summary>Tower bookkeeping for one airplane that has asked for a runway. Not saved; rebuilt after loading.</summary>
    internal struct Flight
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

        /// <summary>Wake category of the airplane (its size class).</summary>
        public SizeClass m_Size;

        /// <summary>A ground airplane that only crosses the runway (its path leaves the runway before any takeoff lane).</summary>
        public bool m_Crossing;

        /// <summary>The tower has told it to hold (short, or at present position) since its last clearance.</summary>
        public bool m_Told;

        /// <summary>It asked for the runway in the air: an arrival (a departure is never "touched down").</summary>
        public bool m_Arrival;

        /// <summary>The tower has given it the landing clearance (on final) for its current approach clearance.</summary>
        public bool m_ToldLanding;

        /// <summary>The weather go-around chance was already rolled on this approach (once, at the decision height).</summary>
        public bool m_WeatherChecked;

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

        /// <summary>The airplane is flying the traffic pattern after a go-around (see <see cref="CircuitLeg"/>).</summary>
        public bool m_InCircuit;

        /// <summary>
        /// A cleared arrival too high for the glide path descends in a hold at the final course join point (hold in lieu of
        /// procedure turn) before turning onto final (see AtcSystem.FlyToFinalFix). It keeps its clearance.
        /// </summary>
        public bool m_DescentHold;

        public HoldingPattern m_DescentPattern;

        /// <summary>This arrival has already gone around once (the practice go-around only happens once).</summary>
        public bool m_WentAround;

        public CircuitLeg m_CircuitLeg;

        /// <summary>Pattern waypoints: end of upwind, end of crosswind, end of downwind; y is the pattern altitude.</summary>
        public float3 m_UpwindEnd;

        public float3 m_CrosswindEnd;

        public float3 m_DownwindEnd;

        /// <summary>Where the airplane's path enters its runway and the direction it then travels (from the last tick).</summary>
        public float3 m_EntryPosition;

        public float2 m_EntryDirection;

        /// <summary>The last tick the airplane was on its runway.</summary>
        public uint m_OnRunwayTick;
    }

    /// <summary>
    /// One runway: every runway edge connected through shared nodes (so crossing runways count as one),
    /// together with those nodes. Only one airplane may use it at a time.
    /// </summary>
    internal struct RunwayState
    {
        /// <summary>The ground airplane currently cleared to take off from or cross the runway, or Entity.Null.</summary>
        public Entity m_Holder;

        /// <summary>The ground airplane cleared to line up and wait on the runway, or Entity.Null.</summary>
        public Entity m_LineUp;

        /// <summary>The airport this runway belongs to (its <see cref="LineUpSetting"/> and class decide line up and wait).</summary>
        public Entity m_Airport;

        /// <summary>Holding altitudes in use above this runway, one bit per stack level.</summary>
        public ulong m_StackMask;

        /// <summary>Airplanes physically on the runway's lanes; only valid when <see cref="m_OccupiedTick"/> is the current tick.</summary>
        public int m_Occupants;

        /// <summary>Of those, the ones on the ground (not yet lifted off); same tick as <see cref="m_OccupiedTick"/>.</summary>
        public int m_GroundOccupants;

        public uint m_OccupiedTick;

        // The arrival picture, rebuilt every tick from the cleared arrivals still in the air (valid when m_ArrivalTick is
        // the current tick): the soonest and the latest of them, and the cleared arrival rolling out on the runway.
        public uint m_ArrivalTick;

        public float m_NextArrivalEta;

        public float m_LastArrivalEta;

        public SizeClass m_LastArrivalSize;

        public Entity m_LandingTraffic;

        /// <summary>Cleared arrivals still in the air (for "number two" in the landing clearance).</summary>
        public int m_ArrivalCount;

        /// <summary>Why the runway is closed this tick (a disaster event, or the airport when it is in danger), or Entity.Null.</summary>
        public Entity m_ClosedBy;

        public uint m_ClosedTick;

        /// <summary>Frame the current closure began (for the closure time limit).</summary>
        public uint m_ClosedFrame;

        /// <summary>An event whose closure reached the time limit: it no longer closes this runway.</summary>
        public Entity m_WaivedBy;

        /// <summary>
        /// No ground airplane is cleared or lined up, and nobody is on the runway. Arrivals in the air on final don't count:
        /// whether the runway is clear for them is decided at the decision height.
        /// </summary>
        public bool IsFree(uint tick) => m_Holder == Entity.Null && m_LineUp == Entity.Null && Occupants(tick) == 0;

        public int Occupants(uint tick) => m_OccupiedTick == tick ? m_Occupants : 0;

        public int GroundOccupants(uint tick) => m_OccupiedTick == tick ? m_GroundOccupants : 0;

        public bool IsClosed(uint tick) => m_ClosedTick == tick && m_ClosedBy != Entity.Null;

        public float NextArrivalEta(uint tick) => m_ArrivalTick == tick ? m_NextArrivalEta : float.MaxValue;

        public bool HasArrivalInSequence(uint tick) => m_ArrivalTick == tick && m_LastArrivalEta >= 0f;

        public Entity LandingTraffic(uint tick) => m_ArrivalTick == tick ? m_LandingTraffic : Entity.Null;

        /// <summary>Starts this tick's arrival picture if it is still last tick's.</summary>
        public void BeginArrivals(uint tick)
        {
            if (m_ArrivalTick != tick)
            {
                m_ArrivalTick = tick;
                m_NextArrivalEta = float.MaxValue;
                m_LastArrivalEta = -1f;
                m_LastArrivalSize = SizeClass.Small;
                m_LandingTraffic = Entity.Null;
                m_ArrivalCount = 0;
            }
        }

        public void AddArrival(float eta, SizeClass size)
        {
            m_ArrivalCount++;
            m_NextArrivalEta = math.min(m_NextArrivalEta, eta);
            if (eta > m_LastArrivalEta)
            {
                m_LastArrivalEta = eta;
                m_LastArrivalSize = size;
            }
        }
    }

    /// <summary>Airspace class of an airport (phase 4-3), set automatically from its size.</summary>
    public enum AirspaceClass : byte
    {
        /// <summary>A tower only: no approach control; the tower handles arrivals from the edge of its zone.</summary>
        D,
        C,
        B,
    }

    /// <summary>
    /// One airport's airspace (phase 4-3): its class, the centre of its runways and the zone radius. Real zones (B about
    /// 20 NM) don't fit a 14 km map, so the radii are scaled down; zones may overlap, which only matters for the pattern
    /// side. Holds are separated by altitude, one band per class with B highest, as satellite airports' traffic stays
    /// under a hub's.
    /// </summary>
    internal struct AirportSpace
    {
        public const float kContactMargin = 0.5f * Setting.kMetersPerNauticalMile;

        // Lowest holding altitude of each class above the runway, in feet; stack levels go up 1,000 ft from there.
        private const float kHoldBaseD = 2000f;
        private const float kHoldBaseC = 4000f;
        private const float kHoldBaseB = 6000f;

        public float2 m_Center;

        /// <summary>Ground height of the runways, for drawing the zone on the map.</summary>
        public float m_Height;
        public float m_Radius;
        public AirspaceClass m_Class;
        public byte m_Runways;
        public short m_Gates;

        /// <summary>The class was chosen in the airport panel rather than set from the size.</summary>
        public bool m_Chosen;

        /// <summary>Two or more runways or 16 gates: B; 8 gates: C; else D (a one-runway regional airport is D).</summary>
        public static AirspaceClass Classify(int runways, int gates)
        {
            return runways >= 2 || gates >= 16 ? AirspaceClass.B : gates >= 8 ? AirspaceClass.C : AirspaceClass.D;
        }

        /// <summary>Zone radius in metres: B 5 NM, C 3.5 NM, D 2.5 NM.</summary>
        public static float RadiusOf(AirspaceClass airspaceClass)
        {
            float nauticalMiles = airspaceClass == AirspaceClass.B ? 5f : airspaceClass == AirspaceClass.C ? 3.5f : 2.5f;
            return nauticalMiles * Setting.kMetersPerNauticalMile;
        }

        /// <summary>Height above the runway of the lowest hold of this class, in metres.</summary>
        public float HoldBase => (m_Class == AirspaceClass.B ? kHoldBaseB : m_Class == AirspaceClass.C ? kHoldBaseC : kHoldBaseD) * 0.3048f;

        /// <summary>An arrival calls the tower (or approach) once it is this close to the airport.</summary>
        public float ContactDistance => m_Radius + kContactMargin;
    }

    /// <summary>Airspace rules shared by the tower job and the airport panel (phase 4-3).</summary>
    internal static class AirspaceRules
    {
        /// <summary>Other airports this close to a runway matter for its pattern side.</summary>
        public const float kNeighbourDistance = 8000f;

        /// <summary>
        /// The nearest other airport within kNeighbourDistance of <paramref name="from"/> lies to the left of the landing
        /// direction, i.e. on the side of a standard left-hand pattern.
        /// </summary>
        public static bool IsNeighbourOnLeft(NativeParallelHashMap<Entity, AirportSpace> airspaces, Entity airport, float2 from, float2 landing)
        {
            float nearest = kNeighbourDistance * kNeighbourDistance;
            bool left = false;
            foreach (var pair in airspaces)
            {
                if (pair.Key == airport)
                {
                    continue;
                }
                float2 offset = pair.Value.m_Center - from;
                float distance = math.lengthsq(offset);
                if (distance < nearest)
                {
                    nearest = distance;
                    left = math.dot(offset, Colossal.Mathematics.MathUtils.Left(landing)) > 0f;
                }
            }
            return left;
        }
    }

    /// <summary>The mod options, copied into a plain struct so jobs can use them.</summary>
    internal struct AtcSettings
    {
        /// <summary>Straight-leg length of the holding pattern, in metres (the option itself is in nautical miles).</summary>
        public float m_HoldingLegLength;

        public float m_HoldShortSetback;

        /// <summary>A ground airplane may go ahead of an arrival that is at least this many seconds from the runway.</summary>
        public float m_DepartureGapSeconds;

        /// <summary>Gap a taxiing airplane keeps between its nose and the tail of the airplane ahead, in metres.</summary>
        public float m_TaxiSpacing;

        public bool m_Pushback;

        /// <summary>Log each airplane's radio exchanges, events and pushback steps (the "Detailed log" option).</summary>
        public bool m_DetailedLog;

        /// <summary>In metres (the options themselves are in nautical miles).</summary>
        public float m_PatternWidth;

        public float m_FinalLength;

        /// <summary>Minimum time between two arrivals cleared to the same runway.</summary>
        public float m_ArrivalSpacingSeconds;

        /// <summary>Disasters that close an airport.</summary>
        public HazardKinds m_ClosureHazards;

        /// <summary>Longest closure for one event, in simulation frames; 0 = until the event is over.</summary>
        public uint m_MaxClosureFrames;

        /// <summary>Chance that an arrival goes around at the decision height because of the weather (set from the climate).</summary>
        public float m_WeatherGoAroundChance;

        /// <summary>The weather that set <see cref="m_WeatherGoAroundChance"/> (for the log).</summary>
        public WeatherKind m_Weather;

        /// <summary>Straight-in finals from the final approach fix, descending at the glide path angle (the "Glide path" option).</summary>
        public bool m_GlidePath;

        /// <summary>Departures turn onto their airway at a standard rate (the "Smooth departure turns" option).</summary>
        public bool m_SmoothDepartureTurns;

        /// <summary>Tangent of the glide path angle.</summary>
        public float m_GlideTan;

        public AtcSettings(Setting setting)
        {
            m_GlidePath = setting.GlidePath;
            m_SmoothDepartureTurns = setting.SmoothDepartureTurns;
            m_GlideTan = (float)System.Math.Tan(System.Math.Max(3, System.Math.Min(20, setting.GlidePathDegrees)) * System.Math.PI / 180.0);
            m_TaxiSpacing = setting.TaxiSpacing;
            m_Pushback = setting.EnablePushback;
            m_PatternWidth = setting.PatternWidthNm * Setting.kMetersPerNauticalMile;
            m_FinalLength = setting.FinalLengthNm * Setting.kMetersPerNauticalMile;
            m_ArrivalSpacingSeconds = setting.ArrivalSpacingSeconds;
            // Set from the climate by AtcSystem.
            m_WeatherGoAroundChance = 0f;
            m_Weather = WeatherKind.Clear;
            m_ClosureHazards = (setting.CloseForTornado ? HazardKinds.Tornado : HazardKinds.None)
                | (setting.CloseForHail ? HazardKinds.Hail : HazardKinds.None)
                | (setting.CloseForOtherDisasters ? HazardKinds.Other : HazardKinds.None);
            m_MaxClosureFrames = setting.LimitClosureTime ? (uint)(System.Math.Max(5, setting.MaxClosureMinutes) * Setting.kFramesPerGameMinute) : 0u;
            m_DepartureGapSeconds = setting.DepartureGapSeconds;
            m_HoldingLegLength = setting.HoldingLegNm * Setting.kMetersPerNauticalMile;
            m_HoldShortSetback = setting.HoldShortSetback;
            m_DetailedLog = setting.DetailedLog;
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

        /// <summary>Taking off (on the runway or just airborne from it); such an airplane still occupies the runway.</summary>
        public bool m_TakingOff;

        /// <summary>The path crosses the upcoming runway instead of taking off from it (ground only).</summary>
        public bool m_Crossing;

        /// <summary>A new path is being searched, so the path buffers can't be trusted this tick.</summary>
        public bool m_PathUnknown;
    }

    /// <summary>A runway as the airport info panel lists it (main thread only).</summary>
    internal sealed class RunwayInfo
    {
        public RunwayInfo(Entity key, Entity airport, RunwayGeometry geometry)
        {
            Key = key;
            Airport = airport;
            Geometry = geometry;
        }

        public Entity Key { get; }

        public Entity Airport { get; }

        /// <summary>Centerline, extent and the directions it is used in.</summary>
        public RunwayGeometry Geometry { get; }

        public System.Collections.Generic.List<Entity> Edges { get; } = new System.Collections.Generic.List<Entity>();
    }

    /// <summary>A runway's centerline in the horizontal plane: an axis and the extent of the runway along it.</summary>
    internal struct RunwayGeometry
    {
        public float2 m_Origin;

        public float2 m_Direction;

        public float m_Min;

        public float m_Max;

        /// <summary>Directions airplanes land and take off on it: <see cref="kAlong"/> and/or <see cref="kAgainst"/> the axis.</summary>
        public byte m_Landings;

        public const byte kAlong = 1;
        public const byte kAgainst = 2;

        // A runway within this cosine of parallel, whose middle lies less than the pattern width plus the margin to one
        // side, puts that side off limits for the traffic pattern.
        private const float kParallelDot = 0.98f;
        private const float kParallelMargin = 1000f;

        public static bool IsParallelOnSide(in RunwayGeometry ours, in RunwayGeometry other, float2 landing, float2 side, float patternWidth)
        {
            if (math.abs(math.dot(other.m_Direction, landing)) < kParallelDot)
            {
                return false;
            }
            float offset = math.dot(other.Middle - ours.Middle, side);
            return offset > 0f && offset < patternWidth + kParallelMargin;
        }

        public void Include(float2 point)
        {
            float along = math.dot(point - m_Origin, m_Direction);
            m_Min = math.min(m_Min, along);
            m_Max = math.max(m_Max, along);
        }

        public float2 Middle => m_Origin + m_Direction * (0.5f * (m_Min + m_Max));
    }

    internal enum CircuitLeg : byte
    {
        /// <summary>Climbing straight ahead over the runway after the go-around.</summary>
        Upwind,

        /// <summary>Turned towards the pattern side, flying across.</summary>
        Crosswind,

        /// <summary>Parallel to the runway, opposite to the landing direction, abeam until the base turn.</summary>
        Downwind,

        /// <summary>
        /// "Extend downwind": not cleared at the end of downwind, it keeps flying straight on, ready to turn base once cleared,
        /// for at most kDownwindExtension; then the tower holds it.
        /// </summary>
        ExtendedDownwind,
    }

    internal enum PushbackPhase : byte
    {
        /// <summary>Ready at the gate, waiting for ground control: traffic is close to the area it would be pushed into.</summary>
        AwaitingApproval,

        /// <summary>Rolling a short way backwards out of the stand, nose locked towards the gate.</summary>
        Pushing,

        /// <summary>Stopped after the push; the tug swings the tail until the nose points along the path.</summary>
        Turning,

        /// <summary>Facing along the path, standing while the engines start and the tug disconnects.</summary>
        EngineStart,
    }

    /// <summary>An airplane being pushed back from its gate. Not saved; a pushback in progress at load just ends.</summary>
    internal struct Pushback
    {
        public float3 m_Start;

        /// <summary>Direction of travel along the path when the pushback began (the push direction).</summary>
        public float2 m_Axis;

        /// <summary>Current nose direction.</summary>
        public float2 m_Heading;

        public PushbackPhase m_Phase;

        /// <summary>How far the airplane is pushed before it is turned.</summary>
        public float m_MaxDistance;

        /// <summary>Half the airplane's larger dimension plus a margin: the area taxiing airplanes keep clear of.</summary>
        public float m_Radius;

        /// <summary>Ticks in a row without a usable path direction.</summary>
        public byte m_UnknownPathTicks;

        /// <summary>Frame the current phase began (approval wait, engine start).</summary>
        public uint m_PhaseFrame;

        public uint m_LastTick;
    }

    /// <summary>Which airport's controller an airborne airplane is talking to (phase 4-3 handoffs).</summary>
    internal struct ZoneState
    {
        public Entity m_Airport;

        /// <summary>It left that airport's frequency already (frequency change approved): no handoff from it.</summary>
        public bool m_Silent;

        /// <summary>A departure with its own airport's departure control (class B): that airport speaks on DEP.</summary>
        public bool m_Departing;

        /// <summary>
        /// Just took off and still low: it checks in with departure control (<see cref="m_DepartureControl"/>: 2 its own
        /// departure frequency, 1 approach, 0 none) and calls the next airport only once it has climbed (AtcSystem.UpdateZone).
        /// </summary>
        public bool m_Climbing;
        public byte m_DepartureControl;
        public uint m_TakeoffFrame;

        public uint m_Tick;
    }

    /// <summary>A departure turn the tower is flying at a standard rate (see AtcSystem.SmoothDepartureTurn).</summary>
    internal struct DepartureTurn
    {
        /// <summary>The game's own steering target, given back once the airplane points at it.</summary>
        public float3 m_GameTarget;

        /// <summary>The heading the airplane is being turned through (unit vector).</summary>
        public float2 m_Heading;

        public uint m_Tick;
    }

    /// <summary>How long the mod's taxi hold has kept an airplane behind the same other one (see AtcSystem.MayHoldBehind).</summary>
    internal struct TaxiHold
    {
        public Entity m_Ahead;
        public uint m_Since;
        public uint m_LastTick;
        public bool m_Released;
    }

    /// <summary>
    /// An airplane released from a deadlock (AtcSystem.DetectDeadlocks): until <see cref="m_Until"/> it drives through the
    /// other airplanes of the same cycle, and only those.
    /// </summary>
    internal struct DeadlockRelease
    {
        public uint m_Until;
        public uint m_Cycle;
    }

    /// <summary>A ground airplane's last navigation speed that no airborne airplane limited (see AtcSystem.PassAirborneBlocker).</summary>
    internal struct FreeSpeed
    {
        public float m_MaxSpeed;
        public float m_Speed;
        public uint m_Tick;
    }

    /// <summary>
    /// The ground a pushing airplane covers this tick: a capsule from its position along the rest of its push.
    /// Taxiing airplanes stop before entering it.
    /// </summary>
    internal struct PushbackArea
    {
        public Entity m_Entity;

        public float2 m_From;

        public float2 m_To;

        public float m_Radius;
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

        /// <summary>The arrival with the highest ratio can join the sequence now; if not, the others wait behind it.</summary>
        public bool m_AirReady;

        public Entity m_Ground;

        public float m_GroundDistance;

        /// <summary>When <see cref="m_Ground"/> asked: of two departures at the same spot, the earlier one goes first.</summary>
        public uint m_GroundRequestFrame;

        public uint m_OldestGroundRequestFrame;

        /// <summary><see cref="m_Ground"/> was given priority by an add-on (Api.Flights.SetPriority).</summary>
        public bool m_GroundPriority;
    }
}

namespace RealisticAviation
{
    /// <summary>
    /// An airport's operating values set by an add-on (Api.Airports.SetOverrides), for the tower job. NaN (or -1 for the glide
    /// path switch) means the option's value.
    /// </summary>
    internal struct OperatingOverride
    {
        public float m_ArrivalSpacingSeconds;
        public float m_DepartureGapSeconds;
        public float m_FinalLength;
        public float m_PatternWidth;
        public float m_WeatherGoAroundChance;
        public float m_GlideTan;

        /// <summary>-1: the option; 0: off; 1: on.</summary>
        public sbyte m_GlidePath;

        /// <summary>The options' values with these on top.</summary>
        public AtcSettings Apply(AtcSettings settings)
        {
            if (!float.IsNaN(m_ArrivalSpacingSeconds))
            {
                settings.m_ArrivalSpacingSeconds = m_ArrivalSpacingSeconds;
            }
            if (!float.IsNaN(m_DepartureGapSeconds))
            {
                settings.m_DepartureGapSeconds = m_DepartureGapSeconds;
            }
            if (!float.IsNaN(m_FinalLength))
            {
                settings.m_FinalLength = m_FinalLength;
            }
            if (!float.IsNaN(m_PatternWidth))
            {
                settings.m_PatternWidth = m_PatternWidth;
            }
            if (!float.IsNaN(m_WeatherGoAroundChance))
            {
                settings.m_WeatherGoAroundChance = m_WeatherGoAroundChance;
            }
            if (!float.IsNaN(m_GlideTan))
            {
                settings.m_GlideTan = m_GlideTan;
            }
            if (m_GlidePath >= 0)
            {
                settings.m_GlidePath = m_GlidePath != 0;
            }
            return settings;
        }
    }

    /// <summary>An add-on's order to send an arrival around (Api.Flights.GoAround), carried out by the next tower job.</summary>
    internal struct ApiGoAround
    {
        public Entity m_Airplane;
        public Unity.Collections.FixedString128Bytes m_Reason;
    }
}
