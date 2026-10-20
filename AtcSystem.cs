using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Edge = Game.Net.Edge;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    /// <summary>
    /// The tower. Gives each runway to one airplane at a time, in the order they asked for it.
    /// Airplanes that are waiting stop short of the runway on the ground, or fly a holding pattern in the air.
    /// Runs right after <see cref="AircraftNavigationSystem"/> (same 16-frame tick) as a worker-thread job that only
    /// lowers the speed or redirects the steering target that system just computed, before
    /// <see cref="AircraftMoveSystem"/> uses them.
    /// </summary>
    public partial class AtcSystem : GameSystemBase
    {
        /// <summary>Simulation time between two aircraft navigation updates (16 frames), as used by the game.</summary>
        public const float kTimeStep = 4f / 15f;

        // Weather go-arounds: the chance in clear weather, and for each kind of bad weather the chance at its full intensity;
        // in between it grows with the intensity from the clear-weather chance.
        private const float kClearGoAroundChance = 0.003f;
        private const float kRainGoAroundChance = 0.08f;
        private const float kSnowGoAroundChance = 0.12f;
        private const float kFogGoAroundChance = 0.12f;
        private const float kThunderGoAroundChance = 0.15f;

        // The chance is logged when it changes by at least this much.
        private const float kGoAroundChanceLogStep = 0.005f;
        private float m_LoggedGoAroundChance = -1f;

        private SimulationSystem m_SimulationSystem;
        private ClimateSystem m_ClimateSystem;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_AirplanePrefabQuery;
        private EntityQuery m_AirplaneQuery;
        private EntityQuery m_RunwayEdgeQuery;
        private EntityQuery m_TaxiwayVersionQuery;
        private EntityQuery m_AirplaneStopQuery;
        private EntityQuery m_AirportVersionQuery;
        private EntityQuery m_WeatherEventQuery;
        private EntityQuery m_EventPrefabQuery;

        private ComponentLookup<Owner> m_OwnerData;
        private ComponentLookup<Curve> m_CurveData;
        private ComponentLookup<Game.Net.CarLane> m_CarLaneData;
        private ComponentLookup<PrefabRef> m_PrefabRefData;
        private ComponentLookup<AircraftData> m_PrefabAircraftData;
        private ComponentLookup<AirplaneData> m_PrefabAirplaneData;
        private ComponentLookup<ObjectGeometryData> m_PrefabGeometryData;
        private ComponentLookup<Transform> m_TransformData;
        private ComponentLookup<Moving> m_MovingData;
        private ComponentLookup<PathOwner> m_PathOwnerData;
        private ComponentLookup<AircraftCurrentLane> m_CurrentLaneData;
        private ComponentLookup<AircraftNavigation> m_NavigationData;
        private BufferLookup<AircraftNavigationLane> m_NavigationLanes;
        private BufferLookup<PathElement> m_PathElements;
        private BufferLookup<LaneObject> m_LaneObjects;
        private BufferLookup<LaneOverlap> m_LaneOverlaps;
        private ComponentLookup<LineUpSetting> m_LineUpData;
        private ComponentLookup<RunwayPattern> m_RunwayPatternData;
        private ComponentLookup<Game.Events.WeatherPhenomenon> m_WeatherPhenomenonData;
        private ComponentLookup<Game.Events.Duration> m_DurationData;
        private ComponentLookup<Game.Events.InDanger> m_InDangerData;
        private ComponentLookup<Blocker> m_BlockerData;
        private ComponentLookup<GroundDelaySetting> m_GroundDelayData;
        private ComponentLookup<Game.Common.Target> m_TargetData;
        private ComponentLookup<Game.Routes.Connected> m_ConnectedData;
        private ComponentLookup<Game.Routes.BoardingVehicle> m_BoardingVehicleData;
        private ComponentLookup<Game.Routes.RouteLane> m_RouteLaneData;
        private ComponentLookup<Taxiway> m_TaxiwayData;

        /// <summary>Runway key of every runway edge and runway node. The key is the runway's lowest-index edge or node.</summary>
        private NativeParallelHashMap<Entity, Entity> m_RunwayOf;
        private NativeParallelHashMap<Entity, RunwayState> m_Runways;
        private NativeParallelHashMap<Entity, RunwayGeometry> m_RunwayGeometry;

        /// <summary>Main-thread copy of the runways for the airport info panel; rebuilt with the runway map.</summary>
        private readonly List<RunwayInfo> m_RunwayList = new List<RunwayInfo>();
        private NativeParallelHashMap<Entity, Flight> m_Flights;
        private NativeList<Entity> m_HoldingFlights;
        private NativeParallelHashMap<Entity, Pushback> m_Pushbacks;
        private NativeReference<uint> m_Sequence;

        /// <summary>Airplane prefab names for the log (diagnostics only).</summary>
        private NativeParallelHashMap<Entity, FixedString128Bytes> m_PrefabNames;
        private int m_PrefabNameCount = -1;

        /// <summary>Closed airports and what closed them (a disaster event, or the airport itself), as of the last tower tick.</summary>
        private NativeParallelHashMap<Entity, Entity> m_AirportClosures;

        /// <summary>Kind of every disaster event prefab, for the closure options; rebuilt when event prefabs change.</summary>
        private NativeParallelHashMap<Entity, HazardKinds> m_HazardKinds;

        /// <summary>Each airplane's latest tower transmission, and the recent transmissions of all airports (phase 4-2).</summary>
        private NativeParallelHashMap<Entity, Entity> m_HeldBehind;
        private NativeParallelHashMap<Entity, TaxiHold> m_TaxiHolds;
        private NativeParallelHashMap<Entity, FreeSpeed> m_FreeSpeeds;
        private NativeParallelHashMap<Entity, ZoneState> m_Zones;

        /// <summary>Who each airplane waiting for its gate waits for (last tick), and airplanes released from a deadlock (until frame).</summary>
        private NativeParallelHashMap<Entity, Entity> m_GateHolds;
        private NativeParallelHashMap<Entity, DeadlockRelease> m_Released;

        /// <summary>Airplanes moving on each airport's ground this tick, and the airports under a ground delay (phase 4-5).</summary>
        private NativeParallelHashMap<Entity, int> m_GroundCounts;
        private NativeParallelHashMap<Entity, int> m_GroundDelays;

        /// <summary>Gates (airplane stops) of each airport, for the default ground delay limit; rebuilt with the runway map.</summary>
        private NativeParallelHashMap<Entity, int> m_GateCounts;

        /// <summary>Each airport's airspace (phase 4-3); rebuilt with the runway map.</summary>
        private NativeParallelHashMap<Entity, AirportSpace> m_Airspaces;

        /// <summary>Each airport's ATIS as of this tower update (empty without an ATIS mod), for "with information X".</summary>
        private NativeParallelHashMap<Entity, AtisState> m_AtisStates;
        private NativeParallelHashMap<Entity, AtcMessage> m_LastMessages;
        private NativeList<AtcMessage> m_RadioLog;
        private NativeReference<uint> m_RadioVersion;

        /// <summary>The radio version up to which add-ons have been told (<see cref="RaiseApiEvents"/>).</summary>
        private uint m_RaisedRadioVersion;
        private Game.UI.NameSystem m_NameSystem;
        private readonly AtcRadio m_Radio = new AtcRadio();
        private int m_EventPrefabCount = -1;

        /// <summary>The last scheduled tower job; everything above is owned by it until it completes.</summary>
        private JobHandle m_JobHandle;

        private int m_TaxiwayVersion = -1;
        private int m_AirportVersion = -1;
        private ulong m_RunwaySignature;
        private uint m_Tick;

        internal IReadOnlyList<RunwayInfo> RunwayList => m_RunwayList;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public override int GetUpdateOffset(SystemUpdatePhase phase) => 10;

        /// <summary>Airplanes flying a holding pattern; written by the job behind <paramref name="dependency"/>.</summary>
        internal NativeList<Entity> GetHoldingFlights(out JobHandle dependency)
        {
            dependency = m_JobHandle;
            return m_HoldingFlights;
        }

        /// <summary>The airport is under a ground delay (phase 4-5); <paramref name="count"/> is the airplanes moving on its ground.</summary>
        internal bool GetGroundDelay(Entity airport, out int count)
        {
            m_JobHandle.Complete();
            m_GroundCounts.TryGetValue(airport, out count);
            return m_GroundDelays.ContainsKey(airport);
        }

        /// <summary>The nearest neighbouring airport lies left of <paramref name="landing"/> seen from <paramref name="from"/> (pattern side default).</summary>
        internal bool IsNeighbourOnLeft(Entity airport, float2 from, float2 landing)
        {
            return AirspaceRules.IsNeighbourOnLeft(m_Airspaces, airport, from, landing);
        }

        /// <summary>The airport's airspace (class, radius), if it has runways.</summary>
        private readonly List<(Entity m_Airport, int m_Value)> m_AirspaceClassRequests = new List<(Entity, int)>();

        /// <summary>Queues a class change chosen in the airport panel (-1: automatic); see <see cref="ApplyAirspaceClassRequests"/>.</summary>
        internal void RequestAirspaceClass(Entity airport, int value)
        {
            m_AirspaceClassRequests.Add((airport, value));
        }

        /// <summary>
        /// Applies queued class changes: stores the choice on the airport building and rebuilds the airspaces, so radius, hold
        /// altitudes, approach control and the map follow. Called every frame outside the UI update, also while paused.
        /// Returns true when something changed (the info panel should refresh).
        /// </summary>
        internal bool ApplyAirspaceClassRequests()
        {
            if (m_AirspaceClassRequests.Count == 0)
            {
                return false;
            }
            m_JobHandle.Complete();
            for (int i = 0; i < m_AirspaceClassRequests.Count; i++)
            {
                (Entity airport, int value) = m_AirspaceClassRequests[i];
                if (!EntityManager.Exists(airport))
                {
                    continue;
                }
                if (value < 0)
                {
                    if (EntityManager.HasComponent<AirspaceClassSetting>(airport))
                    {
                        EntityManager.RemoveComponent<AirspaceClassSetting>(airport);
                    }
                }
                else
                {
                    AirspaceClassSetting setting = new AirspaceClassSetting { m_Class = (byte)math.clamp(value, (int)AirspaceClass.D, (int)AirspaceClass.B) };
                    if (EntityManager.HasComponent<AirspaceClassSetting>(airport))
                    {
                        EntityManager.SetComponentData(airport, setting);
                    }
                    else
                    {
                        EntityManager.AddComponentData(airport, setting);
                    }
                }
                Mod.Log.Info($"Airspace class at airport {airport}: {(value < 0 ? "automatic" : ((AirspaceClass)value).ToString())}");
            }
            m_AirspaceClassRequests.Clear();
            RefreshAirspaces();
            return true;
        }

        internal bool TryGetAirspace(Entity airport, out AirportSpace airspace)
        {
            return m_Airspaces.TryGetValue(airport, out airspace);
        }

        /// <summary>The airport's gates (airplane stops), for the default ground delay limit.</summary>
        internal int GetGateCount(Entity airport)
        {
            return m_GateCounts.TryGetValue(airport, out int gates) ? gates : 0;
        }

        /// <summary>What closed this airport as of the last tower tick (a disaster event, or the airport itself), or Entity.Null.</summary>
        internal Entity GetClosure(Entity airport)
        {
            m_JobHandle.Complete();
            return m_AirportClosures.TryGetValue(airport, out Entity closedBy) ? closedBy : Entity.Null;
        }

        // ---- Behind the public Api (main thread only).

        /// <summary>The airport's airspace class, or false if it has no airspace (not an airport, or not found yet).</summary>
        internal bool TryGetAirspaceClass(Entity airport, out AirspaceClass airspaceClass)
        {
            bool found = m_Airspaces.TryGetValue(airport, out AirportSpace space);
            airspaceClass = space.m_Class;
            return found;
        }

        /// <summary>
        /// The airport's runway ends in use for landing, named as on the radio ("27", "09L"), sorted, added to
        /// <paramref name="names"/> (game runways are mostly one-way; both ends of a runway whose lanes say nothing).
        /// </summary>
        internal void GetLandingRunways(Entity airport, List<string> names)
        {
            for (int i = 0; i < m_RunwayList.Count; i++)
            {
                RunwayInfo runway = m_RunwayList[i];
                if (runway.Airport != airport)
                {
                    continue;
                }
                byte ends = RunwayNames.UsedEnds(runway.Geometry);
                if ((ends & RunwayGeometry.kAlong) != 0)
                {
                    names.Add(RunwayNames.Name(m_RunwayList, runway.Key, runway.Geometry.m_Direction));
                }
                if ((ends & RunwayGeometry.kAgainst) != 0)
                {
                    names.Add(RunwayNames.Name(m_RunwayList, runway.Key, -runway.Geometry.m_Direction));
                }
            }
            names.Sort(System.StringComparer.Ordinal);
        }

        /// <summary>Whether the tower has closed the airport, and what closed it (a disaster event, or the airport itself).</summary>
        internal bool IsClosed(Entity airport, out Entity closedBy)
        {
            closedBy = GetClosure(airport);
            return closedBy != Entity.Null;
        }

        internal EntityQuery AirplaneStopQuery => m_AirplaneStopQuery;

        internal PrefabSystem PrefabSystem => m_PrefabSystem;

        internal Game.UI.NameSystem NameSystem => m_NameSystem ??= World.GetOrCreateSystemManaged<Game.UI.NameSystem>();

        /// <summary>
        /// Hands the transmissions of the last tower update to add-ons (<see cref="Api"/> events), in order. Called with the
        /// tower job completed. The log keeps the latest messages only, so after a very busy update the oldest may be missed.
        /// </summary>
        private void RaiseApiEvents()
        {
            uint version = m_RadioVersion.Value;
            uint count = version - m_RaisedRadioVersion;
            m_RaisedRadioVersion = version;
            if (count == 0 || !Api.HasListeners)
            {
                return;
            }
            int first = m_RadioLog.Length - (int)math.min(count, (uint)m_RadioLog.Length);
            for (int i = first; i < m_RadioLog.Length; i++)
            {
                Api.Raise(m_RadioLog[i]);
            }
        }

        /// <summary>Copies each airport's ATIS from the ATIS add-on, if one is registered, for the tower job of this update.</summary>
        private void FillAtisStates()
        {
            m_JobHandle.Complete();
            m_AtisStates.Clear();
            Api.AtisProvider provider = Api.CurrentAtisProvider;
            if (provider == null)
            {
                return;
            }
            foreach (var pair in m_Airspaces)
            {
                if (provider(pair.Key, out AtisState state))
                {
                    m_AtisStates[pair.Key] = state;
                }
            }
        }

        internal AtcRadio Radio => m_Radio;

        /// <summary>Each airport's airspace, for the airspace infoview (read on the main thread; the tower job only reads it too).</summary>
        internal NativeParallelHashMap<Entity, AirportSpace> Airspaces => m_Airspaces;

        /// <summary>
        /// Counts transmissions, so that an open info panel can refresh as soon as there is a new one (the panel otherwise
        /// refreshes only now and then, and the words would show up after the airplane had already acted on them). Only
        /// read when the tower job has finished, so it never waits for it.
        /// </summary>
        internal bool TryGetRadioVersion(out uint version)
        {
            if (!m_JobHandle.IsCompleted)
            {
                version = 0;
                return false;
            }
            m_JobHandle.Complete();
            version = m_RadioVersion.Value;
            return true;
        }

        /// <summary>The airplane's latest tower transmission, if it heard one recently.</summary>
        internal bool TryGetLastMessage(Entity airplane, out AtcMessage message)
        {
            m_JobHandle.Complete();
            return m_LastMessages.TryGetValue(airplane, out message);
        }

        /// <summary>
        /// The latest transmissions on one of the airport's frequencies, newest first, at most <paramref name="max"/>.
        /// Broadcasts to all aircraft are on every frequency.
        /// </summary>
        internal void GetRadioLog(Entity airport, AtcFrequency frequency, List<AtcMessage> messages, int max)
        {
            m_JobHandle.Complete();
            messages.Clear();
            for (int i = m_RadioLog.Length - 1; i >= 0 && messages.Count < max; i--)
            {
                AtcMessage message = m_RadioLog[i];
                if (message.m_Airport == airport && (message.m_Airplane == Entity.Null || message.m_Frequency == frequency))
                {
                    messages.Add(m_RadioLog[i]);
                }
            }
        }

        /// <summary>Lets <see cref="AtcPreNavigationSystem"/>'s job finish before the next tower job rewrites the list.</summary>
        internal void AddHoldingFlightsReader(JobHandle reader)
        {
            m_JobHandle = JobHandle.CombineDependencies(m_JobHandle, reader);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            Api.Tower = this;
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_ClimateSystem = World.GetOrCreateSystemManaged<ClimateSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AirplanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<AirplaneData>(), ComponentType.ReadOnly<PrefabData>());

            // Parked airplanes have no Moving component; they can't be on a runway or about to enter one.
            m_AirplaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Airplane>(),
                    ComponentType.ReadOnly<Moving>(),
                    ComponentType.ReadWrite<AircraftCurrentLane>(),
                    ComponentType.ReadWrite<AircraftNavigation>(),
                    ComponentType.ReadOnly<PathOwner>(),
                },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<TripSource>() },
            });
            m_RunwayEdgeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Taxiway>(), ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            // Only used for its order version, which changes whenever a taxiway or runway net is added or removed.
            m_TaxiwayVersionQuery = GetEntityQuery(ComponentType.ReadOnly<Taxiway>());
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            // Only used for its order version. AirplaneStop alone: a query with Owner would change whenever any entity does.
            m_AirportVersionQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Routes.AirplaneStop>());
            m_EventPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<PrefabData>());
            m_WeatherEventQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Events.WeatherPhenomenon>(), ComponentType.ReadOnly<Game.Events.Duration>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });

            m_OwnerData = GetComponentLookup<Owner>(true);
            m_CurveData = GetComponentLookup<Curve>(true);
            m_CarLaneData = GetComponentLookup<Game.Net.CarLane>(true);
            m_PrefabRefData = GetComponentLookup<PrefabRef>(true);
            m_PrefabAircraftData = GetComponentLookup<AircraftData>(true);
            m_PrefabAirplaneData = GetComponentLookup<AirplaneData>(true);
            m_PrefabGeometryData = GetComponentLookup<ObjectGeometryData>(true);
            m_TransformData = GetComponentLookup<Transform>(true);
            m_MovingData = GetComponentLookup<Moving>(true);
            m_PathOwnerData = GetComponentLookup<PathOwner>(true);
            m_CurrentLaneData = GetComponentLookup<AircraftCurrentLane>(false);
            m_NavigationData = GetComponentLookup<AircraftNavigation>(false);
            m_NavigationLanes = GetBufferLookup<AircraftNavigationLane>(true);
            m_PathElements = GetBufferLookup<PathElement>(true);
            m_LaneObjects = GetBufferLookup<LaneObject>(true);
            m_LaneOverlaps = GetBufferLookup<LaneOverlap>(true);
            m_LineUpData = GetComponentLookup<LineUpSetting>(true);
            m_RunwayPatternData = GetComponentLookup<RunwayPattern>(true);
            m_WeatherPhenomenonData = GetComponentLookup<Game.Events.WeatherPhenomenon>(true);
            m_DurationData = GetComponentLookup<Game.Events.Duration>(true);
            m_InDangerData = GetComponentLookup<Game.Events.InDanger>(true);
            m_BlockerData = GetComponentLookup<Blocker>(true);
            m_GroundDelayData = GetComponentLookup<GroundDelaySetting>(true);
            m_TargetData = GetComponentLookup<Game.Common.Target>(true);
            m_ConnectedData = GetComponentLookup<Game.Routes.Connected>(true);
            m_BoardingVehicleData = GetComponentLookup<Game.Routes.BoardingVehicle>(true);
            m_RouteLaneData = GetComponentLookup<Game.Routes.RouteLane>(true);
            m_TaxiwayData = GetComponentLookup<Taxiway>(true);

            m_RunwayOf = new NativeParallelHashMap<Entity, Entity>(64, Allocator.Persistent);
            m_Runways = new NativeParallelHashMap<Entity, RunwayState>(16, Allocator.Persistent);
            m_RunwayGeometry = new NativeParallelHashMap<Entity, RunwayGeometry>(16, Allocator.Persistent);
            m_Flights = new NativeParallelHashMap<Entity, Flight>(64, Allocator.Persistent);
            m_HoldingFlights = new NativeList<Entity>(16, Allocator.Persistent);
            m_Pushbacks = new NativeParallelHashMap<Entity, Pushback>(16, Allocator.Persistent);
            m_Sequence = new NativeReference<uint>(0, Allocator.Persistent);
            m_PrefabNames = new NativeParallelHashMap<Entity, FixedString128Bytes>(64, Allocator.Persistent);
            m_AirportClosures = new NativeParallelHashMap<Entity, Entity>(4, Allocator.Persistent);
            m_HazardKinds = new NativeParallelHashMap<Entity, HazardKinds>(16, Allocator.Persistent);
            m_HeldBehind = new NativeParallelHashMap<Entity, Entity>(32, Allocator.Persistent);
            m_TaxiHolds = new NativeParallelHashMap<Entity, TaxiHold>(32, Allocator.Persistent);
            m_FreeSpeeds = new NativeParallelHashMap<Entity, FreeSpeed>(8, Allocator.Persistent);
            m_Zones = new NativeParallelHashMap<Entity, ZoneState>(32, Allocator.Persistent);
            m_GateHolds = new NativeParallelHashMap<Entity, Entity>(8, Allocator.Persistent);
            m_Released = new NativeParallelHashMap<Entity, DeadlockRelease>(8, Allocator.Persistent);
            m_GroundCounts = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_GroundDelays = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_GateCounts = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_Airspaces = new NativeParallelHashMap<Entity, AirportSpace>(4, Allocator.Persistent);
            m_AtisStates = new NativeParallelHashMap<Entity, AtisState>(4, Allocator.Persistent);
            m_LastMessages = new NativeParallelHashMap<Entity, AtcMessage>(64, Allocator.Persistent);
            m_RadioLog = new NativeList<AtcMessage>(64, Allocator.Persistent);
            m_RadioVersion = new NativeReference<uint>(0, Allocator.Persistent);

            RequireForUpdate(m_AirplaneQuery);
        }

        protected override void OnDestroy()
        {
            if (Api.Tower == this)
            {
                Api.Tower = null;
            }
            m_JobHandle.Complete();
            m_RunwayOf.Dispose();
            m_Runways.Dispose();
            m_RunwayGeometry.Dispose();
            m_Flights.Dispose();
            m_HoldingFlights.Dispose();
            m_Pushbacks.Dispose();
            m_Sequence.Dispose();
            m_AirportClosures.Dispose();
            m_HazardKinds.Dispose();
            m_HeldBehind.Dispose();
            m_TaxiHolds.Dispose();
            m_FreeSpeeds.Dispose();
            m_Zones.Dispose();
            m_GateHolds.Dispose();
            m_Released.Dispose();
            m_GroundCounts.Dispose();
            m_GroundDelays.Dispose();
            m_GateCounts.Dispose();
            m_Airspaces.Dispose();
            m_AtisStates.Dispose();
            m_LastMessages.Dispose();
            m_RadioLog.Dispose();
            m_RadioVersion.Dispose();
            m_PrefabNames.Dispose();
            base.OnDestroy();
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_JobHandle.Complete();
            m_RunwayOf.Clear();
            m_Runways.Clear();
            m_RunwayGeometry.Clear();
            m_Flights.Clear();
            m_HoldingFlights.Clear();
            m_LastMessages.Clear();
            m_RadioLog.Clear();
            // Messages of the city before are not news to add-ons.
            m_RaisedRadioVersion = m_RadioVersion.Value;
            m_Radio.Reset();
            m_GroundCounts.Clear();
            m_GroundDelays.Clear();
            m_GateCounts.Clear();
            m_Airspaces.Clear();
            m_AirportClosures.Clear();
            m_Pushbacks.Clear();
            // Everything keyed by airplane: entities of the city before could match airplanes of this one.
            m_Zones.Clear();
            m_TaxiHolds.Clear();
            m_FreeSpeeds.Clear();
            m_HeldBehind.Clear();
            m_GateHolds.Clear();
            m_Released.Clear();
            m_AirspaceClassRequests.Clear();
            m_TaxiwayVersion = -1;
            m_AirportVersion = -1;
            m_RunwaySignature = 0;
        }

        protected override void OnUpdate()
        {
            RefreshRunways();
            RefreshPrefabNames();
            RefreshHazardKinds();
            m_Tick++;

            m_OwnerData.Update(this);
            m_CurveData.Update(this);
            m_CarLaneData.Update(this);
            m_PrefabRefData.Update(this);
            m_PrefabAircraftData.Update(this);
            m_PrefabAirplaneData.Update(this);
            m_PrefabGeometryData.Update(this);
            m_TransformData.Update(this);
            m_MovingData.Update(this);
            m_PathOwnerData.Update(this);
            m_CurrentLaneData.Update(this);
            m_NavigationData.Update(this);
            m_NavigationLanes.Update(this);
            m_PathElements.Update(this);
            m_LaneObjects.Update(this);
            m_LaneOverlaps.Update(this);
            m_LineUpData.Update(this);
            m_RunwayPatternData.Update(this);
            m_WeatherPhenomenonData.Update(this);
            m_DurationData.Update(this);
            m_InDangerData.Update(this);
            m_BlockerData.Update(this);
            m_GroundDelayData.Update(this);
            m_TargetData.Update(this);
            m_ConnectedData.Update(this);
            m_BoardingVehicleData.Update(this);
            m_RouteLaneData.Update(this);
            m_TaxiwayData.Update(this);

            FillAtisStates();
            RaiseApiEvents();
            NativeList<Entity> airplanes = m_AirplaneQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle airplanesHandle);
            NativeList<Entity> weatherEvents = m_WeatherEventQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle weatherHandle);
            AtcJob job = new AtcJob
            {
                m_OwnerData = m_OwnerData,
                m_CurveData = m_CurveData,
                m_CarLaneData = m_CarLaneData,
                m_PrefabRefData = m_PrefabRefData,
                m_PrefabAircraftData = m_PrefabAircraftData,
                m_PrefabAirplaneData = m_PrefabAirplaneData,
                m_PrefabGeometryData = m_PrefabGeometryData,
                m_TransformData = m_TransformData,
                m_MovingData = m_MovingData,
                m_PathOwnerData = m_PathOwnerData,
                m_CurrentLaneData = m_CurrentLaneData,
                m_NavigationData = m_NavigationData,
                m_NavigationLanes = m_NavigationLanes,
                m_PathElements = m_PathElements,
                m_LaneObjects = m_LaneObjects,
                m_LaneOverlaps = m_LaneOverlaps,
                m_LineUpData = m_LineUpData,
                m_RunwayPatternData = m_RunwayPatternData,
                m_WeatherPhenomenonData = m_WeatherPhenomenonData,
                m_DurationData = m_DurationData,
                m_InDangerData = m_InDangerData,
                m_BlockerData = m_BlockerData,
                m_GroundDelayData = m_GroundDelayData,
                m_TargetData = m_TargetData,
                m_ConnectedData = m_ConnectedData,
                m_BoardingVehicleData = m_BoardingVehicleData,
                m_RouteLaneData = m_RouteLaneData,
                m_TaxiwayData = m_TaxiwayData,
                m_WeatherEvents = weatherEvents,
                m_AirportClosures = m_AirportClosures,
                m_HazardKinds = m_HazardKinds,
                m_HeldBehind = m_HeldBehind,
                m_TaxiHolds = m_TaxiHolds,
                m_FreeSpeeds = m_FreeSpeeds,
                m_Zones = m_Zones,
                m_GateHolds = m_GateHolds,
                m_Released = m_Released,
                m_GroundCounts = m_GroundCounts,
                m_GroundDelays = m_GroundDelays,
                m_LastMessages = m_LastMessages,
                m_RadioLog = m_RadioLog,
                m_RadioVersion = m_RadioVersion,
                m_Airplanes = airplanes,
                m_RunwayOf = m_RunwayOf,
                m_Runways = m_Runways,
                m_RunwayGeometry = m_RunwayGeometry,
                m_Flights = m_Flights,
                m_HoldingFlights = m_HoldingFlights,
                m_Pushbacks = m_Pushbacks,
                m_Sequence = m_Sequence,
                m_PrefabNames = m_PrefabNames,
                m_GateCounts = m_GateCounts,
                m_Airspaces = m_Airspaces,
                m_AtisStates = m_AtisStates,
                m_Settings = GetSettings(),
                m_Tick = m_Tick,
                m_Frame = m_SimulationSystem.frameIndex,
            };
            JobHandle handle = job.Schedule(JobHandle.CombineDependencies(Dependency, JobHandle.CombineDependencies(airplanesHandle, weatherHandle), m_JobHandle));
            airplanes.Dispose(handle);
            weatherEvents.Dispose(handle);
            m_JobHandle = handle;
            Dependency = handle;
        }

        /// <summary>
        /// Sorts the disaster event prefabs into the kinds the closure options name, by prefab name (the game has no type
        /// for it): tornadoes, hail, and everything else. Rebuilt only when the set of event prefabs changes.
        /// </summary>
        private void RefreshHazardKinds()
        {
            int count = m_EventPrefabQuery.CalculateEntityCount();
            if (count == m_EventPrefabCount)
            {
                return;
            }
            m_EventPrefabCount = count;
            m_JobHandle.Complete();
            m_HazardKinds.Clear();
            NativeArray<Entity> prefabs = m_EventPrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < prefabs.Length; i++)
            {
                string name = m_PrefabSystem.GetPrefabName(prefabs[i]);
                HazardKinds kind = name.IndexOf("tornado", StringComparison.OrdinalIgnoreCase) >= 0 ? HazardKinds.Tornado
                    : name.IndexOf("hail", StringComparison.OrdinalIgnoreCase) >= 0 ? HazardKinds.Hail
                    : HazardKinds.Other;
                m_HazardKinds.TryAdd(prefabs[i], kind);
            }
            prefabs.Dispose();
        }

        /// <summary>
        /// The options, plus the weather go-around chance from the climate (0–1 intensities): 0.3% in clear weather, rising
        /// with the intensity to 8% in the heaviest rain, 12% in heavy snow or thick fog and 15% in a thunderstorm. When
        /// several apply the highest counts, so it never goes above 15%. Real airliners go around on about 0.1–0.3% of
        /// approaches, and a lot more in low visibility, windshear or thunderstorms.
        /// </summary>
        private AtcSettings GetSettings()
        {
            AtcSettings settings = new AtcSettings(Mod.Setting);
            if (!Mod.Setting.WeatherGoArounds)
            {
                return settings;
            }
            float precipitation = math.saturate(m_ClimateSystem.precipitation);
            float fog = math.saturate(m_ClimateSystem.fog);
            float thunder = math.saturate(m_ClimateSystem.thunder);
            bool snowing = m_ClimateSystem.isSnowing;
            settings.m_Weather = WeatherKind.Clear;
            settings.m_WeatherGoAroundChance = kClearGoAroundChance;
            ConsiderWeather(ref settings, snowing ? WeatherKind.Snow : WeatherKind.Rain, precipitation, snowing ? kSnowGoAroundChance : kRainGoAroundChance);
            ConsiderWeather(ref settings, WeatherKind.Fog, fog, kFogGoAroundChance);
            ConsiderWeather(ref settings, WeatherKind.Thunderstorm, thunder, kThunderGoAroundChance);
            if (math.abs(settings.m_WeatherGoAroundChance - m_LoggedGoAroundChance) >= kGoAroundChanceLogStep)
            {
                m_LoggedGoAroundChance = settings.m_WeatherGoAroundChance;
                Mod.Log.Info($"Weather go-around chance {settings.m_WeatherGoAroundChance * 100f:F1}% ({settings.m_Weather}; precipitation {precipitation:F2}{(snowing ? " snow" : string.Empty)}, fog {fog:F2}, thunder {thunder:F2})");
            }
            return settings;
        }

        /// <summary>The chance for one kind of weather at this intensity; the highest of all kinds is kept.</summary>
        private static void ConsiderWeather(ref AtcSettings settings, WeatherKind kind, float intensity, float fullChance)
        {
            float chance = math.lerp(kClearGoAroundChance, fullChance, intensity);
            if (intensity > 0f && chance > settings.m_WeatherGoAroundChance)
            {
                settings.m_WeatherGoAroundChance = chance;
                settings.m_Weather = kind;
            }
        }

        /// <summary>Counts each airport's gates (its airplane stops). Only after airport edits, with the tower job completed.</summary>
        private void RefreshGateCounts()
        {
            m_JobHandle.Complete();
            m_GateCounts.Clear();
            NativeArray<Entity> stops = m_AirplaneStopQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < stops.Length; i++)
            {
                Entity airport = Airports.GetRoot(EntityManager, stops[i]);
                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(airport))
                {
                    continue;
                }
                m_GateCounts.TryGetValue(airport, out int gates);
                m_GateCounts[airport] = gates + 1;
            }
            stops.Dispose();
        }

        /// <summary>
        /// Sets up each airport's airspace from its runways and gates (class, centre, radius) and logs a summary with the
        /// distances between airports. Only after airport edits, right after the gate count.
        /// </summary>
        private void RefreshAirspaces()
        {
            m_Airspaces.Clear();
            NativeParallelHashMap<Entity, float3> sums = new NativeParallelHashMap<Entity, float3>(4, Allocator.Temp);
            NativeParallelHashMap<Entity, float> heights = new NativeParallelHashMap<Entity, float>(4, Allocator.Temp);
            for (int i = 0; i < m_RunwayList.Count; i++)
            {
                RunwayInfo runway = m_RunwayList[i];
                if (runway.Airport == Entity.Null)
                {
                    continue;
                }
                sums.TryGetValue(runway.Airport, out float3 sum);
                sums[runway.Airport] = sum + new float3(runway.Geometry.Middle, 1f);
                if (!heights.ContainsKey(runway.Airport) && runway.Edges.Count != 0 && EntityManager.HasComponent<Curve>(runway.Edges[0]))
                {
                    heights[runway.Airport] = EntityManager.GetComponentData<Curve>(runway.Edges[0]).m_Bezier.a.y;
                }
            }
            foreach (var pair in sums)
            {
                int runways = (int)pair.Value.z;
                int gates = GetGateCount(pair.Key);
                bool chosen = EntityManager.HasComponent<AirspaceClassSetting>(pair.Key);
                AirspaceClass airspaceClass = chosen
                    ? (AirspaceClass)math.min((int)EntityManager.GetComponentData<AirspaceClassSetting>(pair.Key).m_Class, (int)AirspaceClass.B)
                    : AirportSpace.Classify(runways, gates);
                m_Airspaces[pair.Key] = new AirportSpace
                {
                    m_Center = pair.Value.xy / runways,
                    m_Height = heights.TryGetValue(pair.Key, out float height) ? height : 0f,
                    m_Radius = AirportSpace.RadiusOf(airspaceClass),
                    m_Class = airspaceClass,
                    m_Runways = (byte)math.min(runways, 255),
                    m_Gates = (short)math.min(gates, short.MaxValue),
                    m_Chosen = chosen,
                };
            }
            sums.Dispose();
            heights.Dispose();
            foreach (var pair in m_Airspaces)
            {
                AirportSpace space = pair.Value;
                System.Text.StringBuilder distances = new System.Text.StringBuilder();
                foreach (var other in m_Airspaces)
                {
                    if (other.Key != pair.Key)
                    {
                        distances.Append(' ').Append(other.Key).Append('=').Append((math.distance(space.m_Center, other.Value.m_Center) / 1000f).ToString("F1")).Append("km");
                    }
                }
                Mod.Log.Info($"Airspace of airport {pair.Key}: class {space.m_Class}, radius {space.m_Radius / Setting.kMetersPerNauticalMile:F1} NM, {space.m_Runways} runways, {space.m_Gates} gates; to other airports:{distances}");
            }
        }

        /// <summary>Diagnostics: caches airplane prefab names whenever the set of airplane prefabs changes.</summary>
        private void RefreshPrefabNames()
        {
            int count = m_AirplanePrefabQuery.CalculateEntityCount();
            if (count == m_PrefabNameCount)
            {
                return;
            }
            m_PrefabNameCount = count;
            m_JobHandle.Complete();
            m_PrefabNames.Clear();
            NativeArray<Entity> prefabs = m_AirplanePrefabQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < prefabs.Length; i++)
            {
                FixedString128Bytes name = default;
                name.CopyFromTruncated(m_PrefabSystem.GetPrefabName(prefabs[i]));
                m_PrefabNames.TryAdd(prefabs[i], name);
            }
            prefabs.Dispose();
        }
    }
}
