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

        private const float kWeatherGoAroundBase = 0.005f;
        private const float kWeatherGoAroundPerIntensity = 0.025f;
        private const float kSnowGoAroundFactor = 1.5f;
        private const float kMaxWeatherGoAroundChance = 0.05f;

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
        private ComponentLookup<LineUpAndWaitAirport> m_LineUpAirportData;
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

        // Diagnostics: what the mod did to each taxiing airplane since it landed, logged when it starts boarding at a gate.
        private NativeParallelHashMap<Entity, StandWatch> m_StandWatch;

        /// <summary>Airplanes moving on each airport's ground this tick, and the airports under a ground delay (phase 4-5).</summary>
        private NativeParallelHashMap<Entity, int> m_GroundCounts;
        private NativeParallelHashMap<Entity, int> m_GroundDelays;

        /// <summary>Gates (airplane stops) of each airport, for the default ground delay limit; rebuilt with the runway map.</summary>
        private NativeParallelHashMap<Entity, int> m_GateCounts;

        /// <summary>Each airport's airspace (phase 4-3); rebuilt with the runway map.</summary>
        private NativeParallelHashMap<Entity, AirportSpace> m_Airspaces;
        private NativeParallelHashMap<Entity, AtcMessage> m_LastMessages;
        private NativeList<AtcMessage> m_RadioLog;
        private NativeReference<uint> m_RadioVersion;
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
            m_LineUpAirportData = GetComponentLookup<LineUpAndWaitAirport>(true);
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
            m_StandWatch = new NativeParallelHashMap<Entity, StandWatch>(32, Allocator.Persistent);
            m_GroundCounts = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_GroundDelays = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_GateCounts = new NativeParallelHashMap<Entity, int>(4, Allocator.Persistent);
            m_Airspaces = new NativeParallelHashMap<Entity, AirportSpace>(4, Allocator.Persistent);
            m_LastMessages = new NativeParallelHashMap<Entity, AtcMessage>(64, Allocator.Persistent);
            m_RadioLog = new NativeList<AtcMessage>(64, Allocator.Persistent);
            m_RadioVersion = new NativeReference<uint>(0, Allocator.Persistent);

            RequireForUpdate(m_AirplaneQuery);
        }

        protected override void OnDestroy()
        {
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
            m_StandWatch.Dispose();
            m_GroundCounts.Dispose();
            m_GroundDelays.Dispose();
            m_GateCounts.Dispose();
            m_Airspaces.Dispose();
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
            m_Radio.Reset();
            m_GroundCounts.Clear();
            m_GroundDelays.Clear();
            m_GateCounts.Clear();
            m_Airspaces.Clear();
            m_AirportClosures.Clear();
            m_StandWatch.Clear();
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
            m_LineUpAirportData.Update(this);
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
                m_LineUpAirportData = m_LineUpAirportData,
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
                m_StandWatch = m_StandWatch,
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
        /// The options, plus the weather go-around chance from the climate: none in dry weather; in rain 0.5% + 2.5% *
        /// intensity (light rain 0.5%, heavy rain 3%), in snow half as much again (poorer visibility, contaminated runway),
        /// at most 5%. Real airliners go around on 0.1–0.3% of approaches overall and a few percent in bad weather.
        /// </summary>
        private AtcSettings GetSettings()
        {
            AtcSettings settings = new AtcSettings(Mod.Setting);
            float precipitation = m_ClimateSystem.precipitation;
            if (Mod.Setting.WeatherGoArounds && precipitation > 0f)
            {
                settings.m_Snowing = m_ClimateSystem.isSnowing;
                float chance = kWeatherGoAroundBase + kWeatherGoAroundPerIntensity * math.saturate(precipitation);
                settings.m_WeatherGoAroundChance = math.min(settings.m_Snowing ? chance * kSnowGoAroundFactor : chance, kMaxWeatherGoAroundChance);
            }
            return settings;
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

        // ---------------------------------------------------------------- runways (main thread, only after net edits)

        /// <summary>
        /// Rebuilds the runway map when taxiway nets or airports were added or removed. Runway keys survive unrelated edits.
        /// </summary>
        private void RefreshRunways()
        {
            int version = m_TaxiwayVersionQuery.GetCombinedComponentOrderVersion(false);
            int airportVersion = m_AirportVersionQuery.GetCombinedComponentOrderVersion(false);
            if (version == m_TaxiwayVersion && airportVersion == m_AirportVersion)
            {
                return;
            }
            m_TaxiwayVersion = version;
            m_AirportVersion = airportVersion;

            // The versions also move for temporary entities (tool previews, route changes), so compare the actual set of
            // taxiway edges and airplane stops before paying for a rebuild, which has to wait for the tower job.
            NativeArray<Entity> edges = m_RunwayEdgeQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> stops = m_AirplaneStopQuery.ToEntityArray(Allocator.Temp);
            ulong signature = Signature(edges) * 31ul + Signature(stops);
            stops.Dispose();
            if (signature == m_RunwaySignature)
            {
                edges.Dispose();
                return;
            }
            m_RunwaySignature = signature;
            RefreshGateCounts();

            // Rare (only after airport edits), so plain synchronous EntityManager/query access is fine here
            // and keeps net components out of the tower job's dependencies.
            m_JobHandle.Complete();
            EntityManager entityManager = EntityManager;
            NativeArray<PrefabRef> prefabs = m_RunwayEdgeQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            NativeArray<Edge> edgeEnds = m_RunwayEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            NativeParallelHashMap<Entity, Entity> parent = new NativeParallelHashMap<Entity, Entity>(math.max(8, edges.Length * 2), Allocator.Temp);
            NativeParallelHashMap<Entity, RunwayState> kept = new NativeParallelHashMap<Entity, RunwayState>(math.max(8, m_Runways.Count()), Allocator.Temp);
            NativeList<Entity> runwayEdges = new NativeList<Entity>(edges.Length, Allocator.Temp);
            try
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity prefab = prefabs[i].m_Prefab;
                    if (!entityManager.HasComponent<TaxiwayData>(prefab)
                        || (entityManager.GetComponentData<TaxiwayData>(prefab).m_Flags & TaxiwayFlags.Runway) == 0)
                    {
                        continue;
                    }
                    Entity edge = edges[i];
                    Edge ends = edgeEnds[i];
                    runwayEdges.Add(edge);
                    Union(parent, edge, ends.m_Start);
                    Union(parent, edge, ends.m_End);
                }

                m_RunwayOf.Clear();
                NativeArray<Entity> members = parent.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < members.Length; i++)
                {
                    Entity key = Find(parent, members[i]);
                    m_RunwayOf.Add(members[i], key);
                    if (!kept.ContainsKey(key))
                    {
                        m_Runways.TryGetValue(key, out RunwayState state);
                        kept.Add(key, state);
                    }
                }
                members.Dispose();

                // Centerline extent of each runway, along the direction of its first edge (traffic pattern size and side).
                m_RunwayGeometry.Clear();
                for (int i = 0; i < runwayEdges.Length; i++)
                {
                    Entity edge = runwayEdges[i];
                    if (!entityManager.HasComponent<Curve>(edge))
                    {
                        continue;
                    }
                    Bezier4x3 bezier = entityManager.GetComponentData<Curve>(edge).m_Bezier;
                    Entity key = m_RunwayOf[edge];
                    if (!m_RunwayGeometry.TryGetValue(key, out RunwayGeometry geometry))
                    {
                        geometry.m_Origin = bezier.a.xz;
                        geometry.m_Direction = math.normalizesafe(bezier.d.xz - bezier.a.xz);
                        geometry.m_Min = float.MaxValue;
                        geometry.m_Max = float.MinValue;
                    }
                    geometry.Include(bezier.a.xz);
                    geometry.Include(bezier.d.xz);
                    geometry.m_Landings |= LandingDirections(entityManager, edge, geometry.m_Direction);
                    m_RunwayGeometry[key] = geometry;
                }

                // Flights pointing at a runway that no longer exists drop themselves on the next tick.
                m_Runways.Clear();
                NativeList<Entity> airports = new NativeList<Entity>(4, Allocator.Temp);
                Airports.Collect(entityManager, m_AirplaneStopQuery, airports);
                m_RunwayList.Clear();
                Dictionary<Entity, RunwayInfo> infos = new Dictionary<Entity, RunwayInfo>();
                foreach (var pair in kept)
                {
                    RunwayState state = pair.Value;
                    state.m_Airport = FindAirport(entityManager, pair.Key, airports);
                    m_Runways.Add(pair.Key, state);
                    if (m_RunwayGeometry.TryGetValue(pair.Key, out RunwayGeometry geometry))
                    {
                        RunwayInfo info = new RunwayInfo(pair.Key, state.m_Airport, geometry);
                        infos.Add(pair.Key, info);
                        m_RunwayList.Add(info);
                    }
                }
                for (int i = 0; i < runwayEdges.Length; i++)
                {
                    if (infos.TryGetValue(m_RunwayOf[runwayEdges[i]], out RunwayInfo info))
                    {
                        info.Edges.Add(runwayEdges[i]);
                    }
                }
                airports.Dispose();
                Mod.Log.Info($"Found {m_Runways.Count()} runways.");
                // After the runway list is rebuilt: the airspaces are made from it.
                RefreshAirspaces();
            }
            finally
            {
                runwayEdges.Dispose();
                kept.Dispose();
                parent.Dispose();
                edgeEnds.Dispose();
                prefabs.Dispose();
                edges.Dispose();
            }
        }

        /// <summary>
        /// Which way the runway lanes of an edge run relative to the runway axis. Game runways are usually one-way, so this
        /// tells which end airplanes actually land on and take off from.
        /// </summary>
        private static byte LandingDirections(EntityManager entityManager, Entity edge, float2 axis)
        {
            if (!entityManager.HasBuffer<Game.Net.SubLane>(edge))
            {
                return 0;
            }
            byte directions = 0;
            DynamicBuffer<Game.Net.SubLane> subLanes = entityManager.GetBuffer<Game.Net.SubLane>(edge, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if (!entityManager.HasComponent<Game.Net.CarLane>(lane) || !entityManager.HasComponent<Curve>(lane)
                    || (entityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Runway) == 0)
                {
                    continue;
                }
                Bezier4x3 bezier = entityManager.GetComponentData<Curve>(lane).m_Bezier;
                directions |= math.dot(bezier.d.xz - bezier.a.xz, axis) >= 0f ? RunwayGeometry.kAlong : RunwayGeometry.kAgainst;
            }
            return directions;
        }

        /// <summary>Order-independent fingerprint of a set of entities.</summary>
        private static ulong Signature(NativeArray<Entity> entities)
        {
            ulong signature = (ulong)entities.Length;
            for (int i = 0; i < entities.Length; i++)
            {
                ulong value = ((ulong)(uint)entities[i].Index << 32) | (uint)entities[i].Version;
                signature += value * 0x9E3779B97F4A7C15ul ^ (value >> 29);
            }
            return signature;
        }

        /// <summary>
        /// The airport a runway belongs to: the airport that owns it when it is part of an airport building, otherwise
        /// (a separately built runway) the nearest airport.
        /// </summary>
        private static Entity FindAirport(EntityManager entityManager, Entity runwayKey, NativeList<Entity> airports)
        {
            if (airports.Length == 0)
            {
                return Entity.Null;
            }
            Entity root = Airports.GetRoot(entityManager, runwayKey);
            if (airports.Contains(root))
            {
                return root;
            }
            float3 position;
            if (entityManager.HasComponent<Curve>(runwayKey))
            {
                position = MathUtils.Position(entityManager.GetComponentData<Curve>(runwayKey).m_Bezier, 0.5f);
            }
            else if (entityManager.HasComponent<Game.Net.Node>(runwayKey))
            {
                position = entityManager.GetComponentData<Game.Net.Node>(runwayKey).m_Position;
            }
            else
            {
                return Entity.Null;
            }
            Entity nearest = Entity.Null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < airports.Length; i++)
            {
                if (!entityManager.HasComponent<Transform>(airports[i]))
                {
                    continue;
                }
                float distance = math.distancesq(entityManager.GetComponentData<Transform>(airports[i]).m_Position.xz, position.xz);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = airports[i];
                }
            }
            return nearest;
        }

        // Union-find with the lower entity index as root, so a runway keeps its key when unrelated nets change.
        private static Entity Find(NativeParallelHashMap<Entity, Entity> parent, Entity entity)
        {
            Entity root = entity;
            while (true)
            {
                if (!parent.TryGetValue(root, out Entity up))
                {
                    parent.Add(root, root);
                    return root;
                }
                if (up == root)
                {
                    break;
                }
                root = up;
            }
            // Path compression.
            while (entity != root)
            {
                Entity up = parent[entity];
                parent[entity] = root;
                entity = up;
            }
            return root;
        }

        private static void Union(NativeParallelHashMap<Entity, Entity> parent, Entity a, Entity b)
        {
            Entity rootA = Find(parent, a);
            Entity rootB = Find(parent, b);
            if (rootA == rootB)
            {
                return;
            }
            if (rootB.Index < rootA.Index)
            {
                parent[rootA] = rootB;
            }
            else
            {
                parent[rootB] = rootA;
            }
        }

        // ---------------------------------------------------------------- tower job

        private struct AtcJob : IJob
        {
            // Each airplane in a holding stack flies 1,000 ft above the one below it, as in real holding stacks.
            private const float kStackSpacing = 1000f * 0.3048f;
            private const byte kNoStackLevel = byte.MaxValue;

            // An airplane acts on a clearance this many frames (one simulation second) after it is given, the time of the readback.
            private const uint kResponseFrames = 60;

            // Check-in altitudes: feet, and the vertical speed (m/s, about 300 ft/min) beyond which the pilot reports climbing
            // or descending instead of level.
            private const float kMetersPerFoot = 0.3048f;
            private const float kLevelClimbRate = 1.5f;

            // A departure checks in with departure control (and may call the next airport) once this high above its field
            // (1,000 ft), or this many frames (20 simulation seconds) after takeoff if it stays lower.
            private const float kCheckInHeight = 1000f * kMetersPerFoot;
            private const uint kCheckInTimeoutFrames = 60 * 20;

            // A departure whose wheels are rolling (faster than this, m/s, about 5 km/h) is on its takeoff roll and is not
            // stopped for a closure (there are no V1/VR/V2 speeds to judge it by).
            private const float kRollingSpeed = 1.4f;

            // Last resort only: the mod's taxi holds give up after this many frames behind the same airplane (three simulation
            // minutes), when a deadlock clearly won't clear by itself; the airplanes may then overlap as in the base game.
            private const uint kTaxiHoldTimeout = 60 * 60 * 3;

            // "Extend downwind" lasts at most this far beyond the normal end of downwind before the airplane is told to hold.
            private const float kDownwindExtension = 2f * Setting.kMetersPerNauticalMile;

            // An arrival whose gate is occupied waits with its nose this far before the stand lane: clear of the pushback
            // area (a push of at most kMaxPushbackDistance plus half the airplane and kPushbackClearance).
            private const float kGateHoldDistance = 100f;
            private const float kGateLookAhead = 2000f;

            // Airplanes caught in a wait-for cycle ignore all taxi holds and blockers for this many frames (half a simulation
            // minute), enough to drive past each other; cycles are followed at most this many links.
            private const uint kDeadlockReleaseFrames = 60 * 30;

            // Two departures this close (metres of path to the runway) stand at the same spot: the earlier request goes first.
            private const float kSameSpotDistance = 5f;
            private const int kMaxDeadlockChain = 8;

            // The inner surface area of class B and C zones (as drawn by the airspace infoview), as a share of the radius.
            private const float kInnerZoneFactor = 0.5f;

            // A clearance is only taken back from a taxiing airplane that can still stop short (speed slack in m/s).
            private const float kHoldShortSpeedSlack = 0.5f;
            private const int kMaxStackLevel = 63;

            // Leaving a hold: hand back once within this angle (cosine) of the path point and at least this many turn
            // radii from it; until then, line up through a gate this many turn radii behind the point.
            private const float kHandBackAlignment = 0.866f;
            private const float kHandBackRadiusFactor = 1.5f;
            private const float kGateRadiusFactor = 3f;

            // Holding speed as a factor of the airplane's minimum flying speed.
            private const float kHoldingSpeedFactor = 1.15f;
            // Extra share of the speed range above the speed at which the nose would be level.
            private const float kLevelAttitudeMargin = 0.05f;

            // Path elements scanned beyond the navigation lanes when looking for the next runway, and past its entry for its first runway lane.
            private const int kMaxRunwayLaneScan = 8;
            private const int kMaxPathScan = 96;

            // A waiting airplane looks this much further than its request distance, so it keeps its place in line.
            private const float kKeepRequestFactor = 1.5f;

            // Ground airplanes ask for the runway at least this far before it, plus their stopping distance.
            private const float kMinGroundRequestDistance = 200f;

            // How far along its path an airborne airplane looks for its runway (well beyond the largest zone).
            private const float kAirScanDistance = 20000f;
            private const float kGroundRequestMargin = 50f;

            // Traffic pattern: 1,500 ft above the runway (FAA pattern altitude for large and turbine airplanes); upwind runs at
            // least this far past the runway end; legs turn this many turn radii before each corner. (Parallel runways: see
            // RunwayGeometry.IsParallelOnSide.) The landing decision is made 100 ft above
            // the runway (within 2 NM of the threshold): an arrival goes around there if the runway is not clear.
            private const float kPatternHeight = 1500f * 0.3048f;
            private const float kUpwindExtension = 0.5f * Setting.kMetersPerNauticalMile;
            private const float kCircuitLeadFactor = 1.5f;
            private const float kDecisionHeight = 100f * 0.3048f;
            private const float kDecisionDistance = 2f * Setting.kMetersPerNauticalMile;

            // Response-ratio scheduling: expected runway time of each kind of use, in game seconds.
            private const float kSecondsPerFrame = 1f / 60f;
            private const float kLandingRunwaySeconds = 30f;
            private const float kTakeoffRunwaySeconds = 30f;

            // In-trail spacing: extra seconds per size class when a larger airplane lands ahead of a smaller one (wake turbulence).
            private const float kWakeSecondsPerClass = 20f;
            private const float kTaxiSpeedEstimate = 10f;
            private const float kMinServiceSeconds = 10f;

            // Line up and wait: the landing airplane must be this far past the entry point, and the lined-up airplane
            // stops with its tail this far inside the runway lane.
            private const float kLineUpBehindDistance = 150f;
            private const float kLineUpInset = 10f;

            // Pushback: starts below kPushbackStartSpeed when the nose points against the path (cosine below kPushbackStartDot).
            // Pushes at kPushbackSpeed for half the airplane's length (kMin..kMaxPushbackDistance), or less when the path turns
            // away (cosine below kPushbackTurnDot after kPushbackSettleDistance), then swings at kPushbackTurnRate degrees/s.
            // Waits up to kPushbackUnknownPathTicks ticks for a path. Taxiing airplanes keep kPushbackClearance from the
            // pushing airplane, checking their path every kPushbackSampleStep metres.
            private const float kPushbackStartSpeed = 1f;
            private const float kPushbackStartDot = -0.5f;
            private const float kPushbackTurnDot = 0.866f;
            private const float kPushbackSpeed = 3f;
            private const float kPushbackLengthFactor = 0.5f;
            private const float kMinPushbackDistance = 15f;
            private const float kMaxPushbackDistance = 40f;
            private const float kPushbackSettleDistance = 5f;
            private const float kPushbackTurnRate = 15f;
            private const int kPushbackUnknownPathTicks = 30;
            private const float kPushbackClearance = 10f;
            private const float kPushbackSampleStep = 10f;

            // Ground control holds a pushback while an airplane moves within this margin of its area, for at most this long
            // (game seconds); after the turn the airplane stands this long while its engines start and the tug disconnects.
            private const float kPushbackApprovalMargin = 40f;
            private const float kPushbackApprovalTimeout = 60f;
            private const float kEngineStartSeconds = 10f;

            // A runway closes while a storm, hail or tornado comes within this distance of it beyond the phenomenon radius (1 NM).
            private const float kClosureMargin = Setting.kMetersPerNauticalMile;

            // Taxi spacing looks this much further than the gap plus the stopping distance.
            private const float kTaxiScanMargin = 20f;

            // A ground airplane that was cleared but hasn't reached the runway in this many frames loses its turn
            // and goes to the back of the line, so one stuck airplane can't block a runway forever.
            private const uint kGroundClearanceTimeout = 60 * 60 * 3;

            // The same safety net for arrivals; a cleared arrival normally reaches the runway well within this.
            private const uint kAirClearanceTimeout = 60 * 60 * 5;

            // Radio: the airport radio log keeps this many transmissions; an airplane's latest one is forgotten after this many
            // frames without a new one (about an in-game hour).
            private const int kRadioLogLength = 64;
            private const uint kMessageLifetimeFrames = 60 * 60 * 3;

            // Diagnostics: every this many ticks, log airplanes that have waited longer than this many frames.
            private const uint kDiagnosticInterval = 64;
            private const uint kLongWaitFrames = 60 * 60;

            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            [ReadOnly] public ComponentLookup<Game.Net.CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefData;
            [ReadOnly] public ComponentLookup<AircraftData> m_PrefabAircraftData;
            [ReadOnly] public ComponentLookup<AirplaneData> m_PrefabAirplaneData;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> m_PrefabGeometryData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            [ReadOnly] public ComponentLookup<Moving> m_MovingData;
            [ReadOnly] public ComponentLookup<PathOwner> m_PathOwnerData;
            public ComponentLookup<AircraftCurrentLane> m_CurrentLaneData;
            public ComponentLookup<AircraftNavigation> m_NavigationData;
            [ReadOnly] public BufferLookup<AircraftNavigationLane> m_NavigationLanes;
            [ReadOnly] public BufferLookup<PathElement> m_PathElements;
            [ReadOnly] public BufferLookup<LaneObject> m_LaneObjects;
            [ReadOnly] public ComponentLookup<LineUpAndWaitAirport> m_LineUpAirportData;
            [ReadOnly] public ComponentLookup<RunwayPattern> m_RunwayPatternData;
            [ReadOnly] public ComponentLookup<Game.Events.WeatherPhenomenon> m_WeatherPhenomenonData;
            [ReadOnly] public ComponentLookup<Game.Events.Duration> m_DurationData;
            [ReadOnly] public ComponentLookup<Game.Events.InDanger> m_InDangerData;
            [ReadOnly] public ComponentLookup<Blocker> m_BlockerData;
            [ReadOnly] public ComponentLookup<GroundDelaySetting> m_GroundDelayData;
            [ReadOnly] public ComponentLookup<Game.Common.Target> m_TargetData;
            [ReadOnly] public ComponentLookup<Game.Routes.Connected> m_ConnectedData;
            [ReadOnly] public ComponentLookup<Game.Routes.BoardingVehicle> m_BoardingVehicleData;
            [ReadOnly] public ComponentLookup<Game.Routes.RouteLane> m_RouteLaneData;
            [ReadOnly] public ComponentLookup<Taxiway> m_TaxiwayData;

            [ReadOnly] public NativeList<Entity> m_Airplanes;
            [ReadOnly] public NativeList<Entity> m_WeatherEvents;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity> m_RunwayOf;
            public NativeParallelHashMap<Entity, RunwayState> m_Runways;
            [ReadOnly] public NativeParallelHashMap<Entity, RunwayGeometry> m_RunwayGeometry;
            public NativeParallelHashMap<Entity, Flight> m_Flights;
            public NativeList<Entity> m_HoldingFlights;
            public NativeParallelHashMap<Entity, Pushback> m_Pushbacks;
            public NativeReference<uint> m_Sequence;
            [ReadOnly] public NativeParallelHashMap<Entity, FixedString128Bytes> m_PrefabNames;
            [ReadOnly] public NativeParallelHashMap<Entity, int> m_GateCounts;
            [ReadOnly] public NativeParallelHashMap<Entity, AirportSpace> m_Airspaces;
            public NativeParallelHashMap<Entity, Entity> m_AirportClosures;
            /// <summary>Diagnostics: what the taxi spacing or a pushback held each ground airplane behind, last tick.</summary>
            public NativeParallelHashMap<Entity, Entity> m_HeldBehind;
            public NativeParallelHashMap<Entity, TaxiHold> m_TaxiHolds;
            public NativeParallelHashMap<Entity, FreeSpeed> m_FreeSpeeds;
            public NativeParallelHashMap<Entity, ZoneState> m_Zones;
            public NativeParallelHashMap<Entity, Entity> m_GateHolds;
            public NativeParallelHashMap<Entity, DeadlockRelease> m_Released;
            public NativeParallelHashMap<Entity, StandWatch> m_StandWatch;
            public NativeParallelHashMap<Entity, int> m_GroundCounts;
            public NativeParallelHashMap<Entity, int> m_GroundDelays;
            public NativeParallelHashMap<Entity, AtcMessage> m_LastMessages;
            public NativeList<AtcMessage> m_RadioLog;
            public NativeReference<uint> m_RadioVersion;
            [ReadOnly] public NativeParallelHashMap<Entity, HazardKinds> m_HazardKinds;

            public AtcSettings m_Settings;
            public uint m_Tick;
            public uint m_Frame;

            public void Execute()
            {
                m_HoldingFlights.Clear();
                if (m_Runways.IsEmpty)
                {
                    m_AirportClosures.Clear();
                    m_Flights.Clear();
                    return;
                }
                try
                {
                    UpdateClosures();
                    NativeArray<FlightScan> scans = new NativeArray<FlightScan>(m_Airplanes.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    m_GroundCounts.Clear();
                    int touched = 0;
                    for (int i = 0; i < m_Airplanes.Length; i++)
                    {
                        FlightScan scan = Scan(m_Airplanes[i]);
                        CountGroundTraffic(scan);
                        if (UpdateFlight(ref scan))
                        {
                            touched++;
                        }
                        UpdateZone(scan);
                        scans[i] = scan;
                    }
                    if (touched != m_Flights.Count())
                    {
                        RemoveStaleFlights();
                    }
                    UpdateGroundDelays();
                    GrantRunways();
                    DetectDeadlocks(scans);
                    m_HeldBehind.Clear();
                    m_GateHolds.Clear();
                    NativeList<PushbackArea> pushbackAreas = CollectPushbackAreas();
                    for (int i = 0; i < scans.Length; i++)
                    {
                        Apply(scans[i], pushbackAreas);
                    }
                    pushbackAreas.Dispose();
                    WatchStands(scans);
                    scans.Dispose();
                    EndPushbackTick();
                    EndTaxiHoldTick();
                    EndFreeSpeedTick();
                    EndZoneTick();
                    EndReleaseTick();
                    if (m_Tick % kDiagnosticInterval == 0)
                    {
                        PruneMessages();
                    }
                }
                catch (Exception e)
                {
                    m_HoldingFlights.Clear();
                    m_AirportClosures.Clear();
                    Mod.Log.Error(e, $"{nameof(AtcSystem)} tick failed");
                }
            }

            private Entity RunwayOfLane(Entity lane)
            {
                if (m_OwnerData.TryGetComponent(lane, out Owner owner) && m_RunwayOf.TryGetValue(owner.m_Owner, out Entity runway))
                {
                    return runway;
                }
                return Entity.Null;
            }

            // ------------------------------------------------------------ airport closure

            /// <summary>
            /// Closes every runway that an active disaster of a kind chosen in the options reaches (its radius plus a margin),
            /// and every runway of an airport whose building is in danger from one. With a closure time limit, an event that
            /// has kept a runway closed that long is waived and no longer closes it. Logs each closing and reopening.
            /// </summary>
            private void UpdateClosures()
            {
                // Last tick's closed airports, to announce closings and reopenings once per airport.
                NativeParallelHashMap<Entity, Entity> before = new NativeParallelHashMap<Entity, Entity>(math.max(4, m_AirportClosures.Count()), Allocator.Temp);
                foreach (var pair in m_AirportClosures)
                {
                    before.TryAdd(pair.Key, pair.Value);
                }
                m_AirportClosures.Clear();
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    Entity runway = runways[i];
                    RunwayState state = m_Runways[runway];
                    Entity previous = state.m_ClosedTick == m_Tick - 1 ? state.m_ClosedBy : Entity.Null;
                    Entity closedBy = FindHazard(runway, state.m_Airport, state.m_WaivedBy);
                    if (closedBy != Entity.Null && closedBy == previous && m_Settings.m_MaxClosureFrames != 0
                        && m_Frame - state.m_ClosedFrame >= m_Settings.m_MaxClosureFrames)
                    {
                        LogEvent(closedBy, "reached the closure time limit; no longer closes runway", runway);
                        state.m_WaivedBy = closedBy;
                        closedBy = FindHazard(runway, state.m_Airport, closedBy);
                    }
                    if (closedBy != Entity.Null && closedBy != previous)
                    {
                        state.m_ClosedFrame = m_Frame;
                    }
                    state.m_ClosedBy = closedBy;
                    state.m_ClosedTick = m_Tick;
                    m_Runways[runway] = state;
                    if (closedBy != Entity.Null && state.m_Airport != Entity.Null)
                    {
                        m_AirportClosures.TryAdd(state.m_Airport, closedBy);
                    }
                    if (closedBy != previous)
                    {
                        if (closedBy != Entity.Null)
                        {
                            LogEvent(closedBy, "closes runway", runway);
                        }
                        else
                        {
                            LogEvent(previous, "no longer closes runway; reopened", runway);
                        }
                    }
                }
                runways.Dispose();
                foreach (var pair in m_AirportClosures)
                {
                    if (!before.ContainsKey(pair.Key))
                    {
                        Broadcast(pair.Key, AtcMessageKind.AirportClosed, pair.Value);
                    }
                }
                foreach (var pair in before)
                {
                    if (!m_AirportClosures.ContainsKey(pair.Key))
                    {
                        Broadcast(pair.Key, AtcMessageKind.AirportOpen, Entity.Null);
                    }
                }
                before.Dispose();
            }

            /// <summary>
            /// The first disaster (other than <paramref name="waived"/>) that closes this runway, or Entity.Null. A weather event
            /// counts from the moment it is announced: the game may delay its start by the disaster warning time (early warning
            /// facilities), and an airport closes on the warning, not when the storm hits.
            /// </summary>
            private Entity FindHazard(Entity runway, Entity airport, Entity waived)
            {
                if (m_Settings.m_ClosureHazards == HazardKinds.None)
                {
                    return Entity.Null;
                }
                if (airport != Entity.Null && m_InDangerData.TryGetComponent(airport, out Game.Events.InDanger danger) && danger.m_EndFrame > m_Frame)
                {
                    Entity hazard = danger.m_Event != Entity.Null ? danger.m_Event : airport;
                    if (hazard != waived && ClosesAirport(hazard))
                    {
                        return hazard;
                    }
                }
                if (m_WeatherEvents.Length == 0 || !m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry geometry))
                {
                    return Entity.Null;
                }
                for (int i = 0; i < m_WeatherEvents.Length; i++)
                {
                    Entity hazard = m_WeatherEvents[i];
                    if (hazard == waived
                        || !m_DurationData.TryGetComponent(hazard, out Game.Events.Duration duration)
                        || m_Frame >= duration.m_EndFrame
                        || !m_WeatherPhenomenonData.TryGetComponent(hazard, out Game.Events.WeatherPhenomenon phenomenon)
                        || !ClosesAirport(hazard))
                    {
                        continue;
                    }
                    float2 center = phenomenon.m_PhenomenonPosition.xz;
                    float along = math.clamp(math.dot(center - geometry.m_Origin, geometry.m_Direction), geometry.m_Min, geometry.m_Max);
                    float reach = phenomenon.m_PhenomenonRadius + kClosureMargin;
                    if (math.distancesq(geometry.m_Origin + geometry.m_Direction * along, center) <= reach * reach)
                    {
                        return hazard;
                    }
                }
                return Entity.Null;
            }

            /// <summary>The event (or an airport in danger without one, counted as "other") is a kind chosen to close airports.</summary>
            private bool ClosesAirport(Entity hazard)
            {
                HazardKinds kind = m_PrefabRefData.TryGetComponent(hazard, out PrefabRef prefabRef)
                    && m_HazardKinds.TryGetValue(prefabRef.m_Prefab, out HazardKinds known) ? known : HazardKinds.Other;
                return (m_Settings.m_ClosureHazards & kind) != 0;
            }

            // ------------------------------------------------------------ ground flow control (phase 4-5)

            /// <summary>
            /// Counts an airplane that wants to move on the ground (it has a route: taxiing, waiting for the runway or a gate,
            /// pushing back, rolling out) towards its airport. One boarding at a gate has no route yet and doesn't count.
            /// </summary>
            private void CountGroundTraffic(in FlightScan scan)
            {
                if (scan.m_Flying || m_NavigationLanes[scan.m_Entity].Length == 0)
                {
                    return;
                }
                Entity airport = GetAirportOf(scan.m_Entity);
                m_GroundCounts.TryGetValue(airport, out int count);
                m_GroundCounts[airport] = count + 1;
            }

            /// <summary>
            /// Starts a ground delay at an airport with the threshold reached, and ends it once the count is two below it (so it
            /// doesn't flicker at the limit). Both are announced to all aircraft.
            /// </summary>
            private void UpdateGroundDelays()
            {
                foreach (var pair in m_GroundCounts)
                {
                    int limit = GetGroundDelayLimit(pair.Key);
                    if (limit > 0 && pair.Value >= limit && !m_GroundDelays.ContainsKey(pair.Key))
                    {
                        m_GroundDelays.Add(pair.Key, pair.Value);
                        Mod.Log.Info($"Ground delay at airport {pair.Key}: {pair.Value} airplanes moving on the ground (limit {limit})");
                        Broadcast(pair.Key, AtcMessageKind.GroundDelay, Entity.Null);
                    }
                }
                if (m_GroundDelays.IsEmpty)
                {
                    return;
                }
                NativeArray<Entity> delayed = m_GroundDelays.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < delayed.Length; i++)
                {
                    m_GroundCounts.TryGetValue(delayed[i], out int count);
                    int limit = GetGroundDelayLimit(delayed[i]);
                    if (limit <= 0 || count <= limit - 2)
                    {
                        m_GroundDelays.Remove(delayed[i]);
                        Mod.Log.Info($"Ground delay at airport {delayed[i]} ended: {count} airplanes moving on the ground");
                        Broadcast(delayed[i], AtcMessageKind.GroundDelayEnded, Entity.Null);
                    }
                }
                delayed.Dispose();
            }

            /// <summary>The airport's ground delay limit as set in its info panel; 0 when it is off there (the default).</summary>
            private int GetGroundDelayLimit(Entity airport)
            {
                if (m_GroundDelayData.TryGetComponent(airport, out GroundDelaySetting setting))
                {
                    return setting.ActiveLimit;
                }
                // Off until the player turns it on for this airport.
                return 0;
            }

            /// <summary>The airplane stands at a closed airport (its lane belongs to the airport's buildings or nets).</summary>
            private bool IsAtClosedAirport(Entity entity)
            {
                return !m_AirportClosures.IsEmpty && m_AirportClosures.ContainsKey(GetAirportOf(entity));
            }

            private float GetNoseLength(Entity prefab)
            {
                return m_PrefabGeometryData.TryGetComponent(prefab, out ObjectGeometryData geometry) ? math.max(0f, geometry.m_Bounds.max.z) : 0f;
            }

            // ------------------------------------------------------------ per-airplane scan

            private FlightScan Scan(Entity entity)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                FlightScan scan = new FlightScan
                {
                    m_Entity = entity,
                    m_Flying = (currentLane.m_LaneFlags & AircraftLaneFlags.Flying) != 0,
                    m_TakingOff = (currentLane.m_LaneFlags & AircraftLaneFlags.TakingOff) != 0,
                    m_Occupied = RunwayOfLane(currentLane.m_Lane),
                };
                PathOwner pathOwner = m_PathOwnerData[entity];
                if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete)) != 0)
                {
                    scan.m_PathUnknown = true;
                    return scan;
                }

                if (scan.m_Flying)
                {
                    // Far enough along the path to find the runway before the airplane enters the largest zone (B, 5 NM + margin);
                    // whether it calls is decided by its straight distance to its airport (IsWithinContact).
                    scan.m_RequestDistance = kAirScanDistance;
                }
                else
                {
                    Entity prefab = m_PrefabRefData[entity].m_Prefab;
                    AircraftData aircraftData = m_PrefabAircraftData[prefab];
                    scan.m_RequestDistance = math.max(kMinGroundRequestDistance,
                        VehicleUtils.GetBrakingDistance(aircraftData, aircraftData.m_GroundMaxSpeed, kTimeStep)
                        + GetNoseLength(prefab) + m_Settings.m_HoldShortSetback + kGroundRequestMargin);
                }
                float limit = scan.m_RequestDistance * kKeepRequestFactor;

                // The rest of the current lane, then the navigation lanes, then the path elements not yet copied into them.
                Entity skip = scan.m_Occupied;
                float distance = 0f;
                if (m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    distance = curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x);
                }
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AircraftNavigationLane lane = navigationLanes[i];
                    if (VisitLane(lane.m_Lane, lane.m_CurvePosition, limit, ref skip, ref distance, ref scan))
                    {
                        return AlignEntryWithRunway(entity, scan, i, pathOwner.m_ElementIndex);
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, pathOwner.m_ElementIndex + kMaxPathScan);
                for (int i = pathOwner.m_ElementIndex; i < end; i++)
                {
                    PathElement element = path[i];
                    if (VisitLane(element.m_Target, element.m_TargetDelta, limit, ref skip, ref distance, ref scan))
                    {
                        return AlignEntryWithRunway(entity, scan, navigationLanes.Length, i);
                    }
                }
                return scan;
            }

            /// <summary>
            /// A ground airplane usually enters a runway through a junction lane that turns onto it, so the first runway lane
            /// of its path points along the taxiway. Line up and wait needs the direction of the takeoff roll instead: that
            /// of the first actual runway lane (CarLaneFlags.Runway) that follows. Crossing traffic keeps the junction direction.
            /// </summary>
            private FlightScan AlignEntryWithRunway(Entity entity, FlightScan scan, int navigationIndex, int pathIndex)
            {
                if (scan.m_Flying || scan.m_Upcoming == Entity.Null)
                {
                    return scan;
                }
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = navigationIndex; i < navigationLanes.Length; i++)
                {
                    if (TakeRunwayDirection(navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, ref scan))
                    {
                        return scan;
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, pathIndex + kMaxRunwayLaneScan);
                for (int i = pathIndex; i < end; i++)
                {
                    if (TakeRunwayDirection(path[i].m_Target, path[i].m_TargetDelta, ref scan))
                    {
                        return scan;
                    }
                }
                return scan;
            }

            /// <summary>Returns true when done: a runway lane gave the direction, or the path left the runway first.</summary>
            private bool TakeRunwayDirection(Entity lane, float2 delta, ref FlightScan scan)
            {
                if (RunwayOfLane(lane) != scan.m_Upcoming)
                {
                    // The path leaves the runway before any runway lane proper: it only crosses it.
                    scan.m_Crossing = true;
                    return true;
                }
                if (!m_CarLaneData.TryGetComponent(lane, out Game.Net.CarLane carLane) || (carLane.m_Flags & Game.Net.CarLaneFlags.Runway) == 0
                    || !m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                scan.m_EntryDirection = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                return true;
            }

            /// <summary>Returns true when the scan is finished: the next runway was found or the distance limit passed.</summary>
            private bool VisitLane(Entity lane, float2 delta, float limit, ref Entity skip, ref float distance, ref FlightScan scan)
            {
                Entity runway = RunwayOfLane(lane);
                bool hasCurve = m_CurveData.TryGetComponent(lane, out Curve curve);
                if (runway == Entity.Null)
                {
                    // Once the path leaves the runway the airplane is on, a later lane of that runway is a new use.
                    skip = Entity.Null;
                }
                else if (runway != skip)
                {
                    scan.m_Upcoming = runway;
                    scan.m_Distance = distance;
                    if (hasCurve)
                    {
                        scan.m_EntryPosition = MathUtils.Position(curve.m_Bezier, delta.x);
                        float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                        scan.m_EntryDirection = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                    }
                    return true;
                }
                if (hasCurve)
                {
                    distance += curve.m_Length * math.abs(delta.y - delta.x);
                }
                return distance > limit;
            }

            // ------------------------------------------------------------ clearances

            /// <summary>Updates or creates this airplane's request. Returns true when it has one afterwards.</summary>
            private bool UpdateFlight(ref FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                bool hasFlight = m_Flights.TryGetValue(entity, out Flight flight);

                // Safety net: an airplane flying the pattern or holding that the game no longer considers airborne (landed
                // after all) is handed back at once, so it can never sit on the runway as a pattern flight forever.
                if (hasFlight && !scan.m_Flying && (flight.m_InCircuit || (flight.m_Holding && scan.m_Occupied == flight.m_Runway)))
                {
                    if (flight.m_Holding)
                    {
                        FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                    }
                    flight.m_InCircuit = false;
                    flight.m_Holding = false;
                    m_Flights[entity] = flight;
                    LogEvent(entity, "lost its flying state in the pattern; handed back to the game on runway", flight.m_Runway);
                }

                // After a go-around the airplane's game state still says "on final on the runway lane" while it flies the
                // pattern or holds; it isn't using the runway, and its path can't tell where it is.
                if (hasFlight && scan.m_Flying && (flight.m_InCircuit || flight.m_Holding) && scan.m_Occupied == flight.m_Runway)
                {
                    flight.m_LastTick = m_Tick;
                    flight.m_LastFlying = true;
                    UpdateStraightEta(entity, ref flight);
                    NoteArrival(entity, flight);
                    if (flight.m_Clearance == Clearance.Waiting && m_Tick % kDiagnosticInterval == 0 && m_Frame - flight.m_RequestFrame > kLongWaitFrames)
                    {
                        LogLongWait(entity, flight);
                    }
                    m_Flights[entity] = flight;
                    return true;
                }

                // Airborne arrivals on final are on the runway lane too, but they don't occupy the runway yet; whether it is
                // clear for them is decided at the decision height. A departure lifting off still does.
                if (scan.m_Occupied != Entity.Null && (!scan.m_Flying || scan.m_TakingOff)
                    && m_Runways.TryGetValue(scan.m_Occupied, out RunwayState occupied))
                {
                    if (occupied.m_OccupiedTick != m_Tick)
                    {
                        occupied.m_OccupiedTick = m_Tick;
                        occupied.m_Occupants = 0;
                        occupied.m_GroundOccupants = 0;
                    }
                    occupied.m_Occupants++;
                    if (!scan.m_Flying)
                    {
                        occupied.m_GroundOccupants++;
                    }
                    m_Runways[scan.m_Occupied] = occupied;
                }

                if (hasFlight)
                {
                    flight.m_LastTick = m_Tick;
                    flight.m_LastFlying = scan.m_Flying;
                    if (!scan.m_PathUnknown)
                    {
                        if (scan.m_Occupied == flight.m_Runway)
                        {
                            // An arrival is on the runway lane for its whole final approach; what matters is the touchdown.
                            if (flight.m_Arrival && !flight.m_TouchedDown && !scan.m_Flying && flight.m_SeenOnRunway && flight.m_Clearance == Clearance.Cleared)
                            {
                                flight.m_TouchedDown = true;
                            }
                            flight.m_SeenOnRunway = true;
                            flight.m_OnRunwayTick = m_Tick;
                            if (scan.m_Flying && !scan.m_TakingOff && !flight.m_TouchedDown)
                            {
                                UpdateStraightEta(entity, ref flight);
                            }
                            else
                            {
                                flight.m_LastDistance = 0f;
                                flight.m_LastEta = 0f;
                            }
                            if (scan.m_Flying && !scan.m_TakingOff && flight.m_Arrival && flight.m_Clearance == Clearance.Cleared && !flight.m_ToldLanding)
                            {
                                // On final: the tower clears it to land.
                                flight.m_ToldLanding = true;
                                Say(entity, AtcMessageKind.ClearedToLand, flight);
                            }
                        }
                        else if (flight.m_SeenOnRunway || scan.m_Upcoming != flight.m_Runway)
                        {
                            // Used the runway and left it, or no longer heading for it. It may ask for the next one below.
                            LogEvent(entity, flight.m_SeenOnRunway ? "vacated runway" : "no longer heading for runway", flight.m_Runway);
                            if (flight.m_TouchedDown)
                            {
                                // Off the runway: the tower hands it to ground, which clears it to its gate.
                                Say(entity, AtcMessageKind.ContactGround, flight);
                                Say(entity, AtcMessageKind.TaxiToGate, flight);
                                m_Runways.TryGetValue(flight.m_Runway, out RunwayState landedOn);
                                LogPathToGate(entity, landedOn.m_Airport);
                            }
                            else if (flight.m_SeenOnRunway && scan.m_Flying)
                            {
                                // Airborne after takeoff: on to departure control, or off the frequency at a class D airport.
                                // Value: 2 = its own departure frequency (class B), 1 = approach works departures too, 0 = none (class D).
                                AirspaceClass departedClass = GetAirspaceClass(flight.m_Runway);
                                int departureControl = departedClass == AirspaceClass.B ? 2 : departedClass == AirspaceClass.C ? 1 : 0;
                                Say(entity, AtcMessageKind.ContactDeparture, flight, departureControl);
                                m_Runways.TryGetValue(flight.m_Runway, out RunwayState departedFrom);
                                m_Zones[entity] = new ZoneState
                                {
                                    m_Airport = departedFrom.m_Airport,
                                    m_Silent = departedClass == AirspaceClass.D,
                                    m_Departing = departedClass == AirspaceClass.B,
                                    m_Climbing = true,
                                    m_DepartureControl = (byte)departureControl,
                                    m_TakeoffFrame = m_Frame,
                                    m_Tick = m_Tick,
                                };
                            }
                            DropFlight(entity, flight);
                            return TryRequest(ref scan);
                        }
                        else
                        {
                            flight.m_LastDistance = scan.m_Distance;
                            flight.m_LastEta = scan.m_Flying ? EstimateArrivalSeconds(entity, scan) : 0f;
                            flight.m_EntryPosition = scan.m_EntryPosition;
                            flight.m_EntryDirection = scan.m_EntryDirection;
                            flight.m_Crossing = scan.m_Crossing;
                            if (flight.m_Clearance == Clearance.Cleared
                                && m_Frame - flight.m_ClearedFrame > (scan.m_Flying ? kAirClearanceTimeout : kGroundClearanceTimeout))
                            {
                                LogEvent(entity, "clearance timed out, back of the line for runway", flight.m_Runway);
                                ReleaseRunway(entity, flight.m_Runway);
                                flight.m_Clearance = Clearance.Waiting;
                                flight.m_Sequence = NextSequence();
                                flight.m_RequestFrame = m_Frame;
                            }
                        }
                    }
                    if (flight.m_Clearance == Clearance.Waiting && m_Tick % kDiagnosticInterval == 0 && m_Frame - flight.m_RequestFrame > kLongWaitFrames)
                    {
                        LogLongWait(entity, flight);
                    }
                    NoteArrival(entity, flight);
                    m_Flights[entity] = flight;
                    return true;
                }
                return TryRequest(ref scan);
            }

            /// <summary>
            /// ETA where the path can't say: straight to the threshold on final; in the pattern, along the remaining legs
            /// and then to the threshold; holding after a go-around, via the final-approach start it will resume at.
            /// </summary>
            private void UpdateStraightEta(Entity entity, ref Flight flight)
            {
                float2 position = m_TransformData[entity].m_Position.xz;
                float2 threshold = flight.m_EntryPosition.xz;
                if (flight.m_InCircuit)
                {
                    float2 upwind = flight.m_UpwindEnd.xz;
                    float2 crosswind = flight.m_CrosswindEnd.xz;
                    float2 downwind = flight.m_DownwindEnd.xz;
                    flight.m_LastDistance = flight.m_CircuitLeg == CircuitLeg.Upwind
                        ? math.distance(position, upwind) + math.distance(upwind, crosswind) + math.distance(crosswind, downwind)
                        : flight.m_CircuitLeg == CircuitLeg.Crosswind
                            ? math.distance(position, crosswind) + math.distance(crosswind, downwind)
                            : flight.m_CircuitLeg == CircuitLeg.Downwind
                                ? math.distance(position, downwind)
                                : 0f;
                    // From the end of downwind (or from beyond it, on the extension), the base turn and final to the threshold.
                    flight.m_LastDistance += math.distance(flight.m_CircuitLeg == CircuitLeg.ExtendedDownwind ? position : downwind, threshold);
                }
                else if (flight.m_Holding)
                {
                    float2 resume = flight.m_ResumeTarget.xz;
                    flight.m_LastDistance = math.distance(position, resume) + math.distance(resume, threshold);
                }
                else
                {
                    flight.m_LastDistance = math.distance(position, threshold);
                }
                flight.m_LastEta = flight.m_LastDistance / math.max(math.length(m_MovingData[entity].m_Velocity), 1f);
            }

            /// <summary>Adds a cleared arrival to its runway's arrival picture for this tick.</summary>
            private void NoteArrival(Entity entity, in Flight flight)
            {
                if (flight.m_Clearance != Clearance.Cleared || !flight.m_LastFlying && !flight.m_TouchedDown
                    || !m_Runways.TryGetValue(flight.m_Runway, out RunwayState state))
                {
                    return;
                }
                state.BeginArrivals(m_Tick);
                if (flight.m_TouchedDown)
                {
                    if (flight.m_OnRunwayTick == m_Tick)
                    {
                        state.m_LandingTraffic = entity;
                    }
                }
                else
                {
                    state.AddArrival(flight.m_LastEta, flight.m_Size);
                }
                m_Runways[flight.m_Runway] = state;
            }

            /// <summary>
            /// An arrival calls once it is inside its airport's zone plus a margin (phase 4-3), measured straight to the airport,
            /// whatever other zones overlap there. An airport without airspace data uses the B-to-D middle value.
            /// </summary>
            private bool IsWithinContact(in FlightScan scan)
            {
                float2 position = m_TransformData[scan.m_Entity].m_Position.xz;
                if (m_Runways.TryGetValue(scan.m_Upcoming, out RunwayState state) && m_Airspaces.TryGetValue(state.m_Airport, out AirportSpace space))
                {
                    return math.distancesq(position, space.m_Center) <= space.ContactDistance * space.ContactDistance;
                }
                return scan.m_Distance <= AirportSpace.RadiusOf(AirspaceClass.C) + AirportSpace.kContactMargin;
            }

            private bool TryRequest(ref FlightScan scan)
            {
                if (scan.m_PathUnknown || scan.m_Upcoming == Entity.Null
                    || (scan.m_Flying ? !IsWithinContact(scan) || IsClimbingOut(scan.m_Entity) : scan.m_Distance > scan.m_RequestDistance || !HasTaxiClearance(scan.m_Entity)))
                {
                    return false;
                }
                Flight flight = new Flight
                {
                    m_Runway = scan.m_Upcoming,
                    m_Sequence = NextSequence(),
                    m_RequestFrame = m_Frame,
                    m_LastTick = m_Tick,
                    m_LastDistance = scan.m_Distance,
                    m_LastFlying = scan.m_Flying,
                    m_LastEta = scan.m_Flying ? EstimateArrivalSeconds(scan.m_Entity, scan) : 0f,
                    m_EntryPosition = scan.m_EntryPosition,
                    m_EntryDirection = scan.m_EntryDirection,
                    m_Clearance = Clearance.Waiting,
                    m_Size = m_PrefabAircraftData[m_PrefabRefData[scan.m_Entity].m_Prefab].m_SizeClass,
                    m_Crossing = scan.m_Crossing,
                    m_Arrival = scan.m_Flying,
                };
                m_Flights.Add(scan.m_Entity, flight);
                if (scan.m_Flying)
                {
                    // First call in the airspace: approach (or the tower at a class D airport) identifies it.
                    // Coming from another airport's airspace, that controller hands it over first; from now on its own airport
                    // keeps it (UpdateZone).
                    Entity destination = m_Runways.TryGetValue(flight.m_Runway, out RunwayState destinationState) ? destinationState.m_Airport : Entity.Null;
                    if (m_Zones.TryGetValue(scan.m_Entity, out ZoneState zone) && zone.m_Airport != destination && zone.m_Airport != Entity.Null && !zone.m_Silent)
                    {
                        SayAt(scan.m_Entity, AtcMessageKind.HandoffTo, zone.m_Airport, Entity.Null, default, (int)GetAirspaceClass(flight.m_Runway), destination,
                            DepartureFrequency(zone));
                    }
                    m_Zones[scan.m_Entity] = new ZoneState { m_Airport = destination, m_Tick = m_Tick };
                    Say(scan.m_Entity, AtcMessageKind.InitialContact, flight, (int)GetAirspaceClass(flight.m_Runway));
                }
                else
                {
                    // Ready for the runway: ground hands the departure over to the tower.
                    Say(scan.m_Entity, AtcMessageKind.ContactTower, flight);
                }
                m_PrefabNames.TryGetValue(m_PrefabRefData[scan.m_Entity].m_Prefab, out FixedString128Bytes name);
                LogEvent(scan.m_Entity, $"[{name}] asks (#{flight.m_Sequence}, {Describe(flight)}) for runway", flight.m_Runway);
                return true;
            }

            /// <summary>
            /// Seconds until an airborne airplane reaches the runway. A holding airplane's path stays frozen while it
            /// circles away from it, so the straight-line distance to the runway is used when that is longer.
            /// </summary>
            private float EstimateArrivalSeconds(Entity entity, in FlightScan scan)
            {
                float distance = math.max(scan.m_Distance, math.distance(m_TransformData[entity].m_Position.xz, scan.m_EntryPosition.xz));
                float minSpeed = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab].m_FlyingSpeed.x;
                return distance / math.max(math.length(m_MovingData[entity].m_Velocity), math.max(minSpeed, 1f));
            }

            // ------------------------------------------------------------ diagnostics

            private static string Describe(Flight flight)
            {
                return $"{(flight.m_LastFlying ? "air" : "ground")}, {flight.m_LastDistance:F0} m";
            }

            private static void LogEvent(Entity entity, string what, Entity runway)
            {
                Mod.Log.Info($"{entity} {what} {runway}");
            }

            // ------------------------------------------------------------ radio (phase 4-2)

            /// <summary>
            /// Records a tower transmission: it becomes the airplane's latest message and goes into the radio log. Only these
            /// small records are kept here; the words are built on the main thread when an info panel shows them.
            /// </summary>
            private void Say(Entity airplane, AtcMessageKind kind, Entity runway, float2 direction, int value = 0, Entity hazard = default)
            {
                Entity airport = m_Runways.TryGetValue(runway, out RunwayState state) ? state.m_Airport : GetAirportOf(airplane);
                SayAt(airplane, kind, airport, runway, direction, value, hazard);
            }

            /// <summary>A transmission by the given airport's controller (<paramref name="hazard"/>: the disaster, or for a handoff the next airport).</summary>
            private void SayAt(Entity airplane, AtcMessageKind kind, Entity airport, Entity runway, float2 direction, int value, Entity hazard,
                AtcFrequency? frequency = null)
            {
                AtcMessage message = new AtcMessage
                {
                    m_Airplane = airplane,
                    m_Runway = runway,
                    m_Airport = airport,
                    m_Direction = direction,
                    m_Event = hazard,
                    m_Value = value,
                    m_Frame = m_Frame,
                    m_Kind = kind,
                    m_Frequency = frequency ?? FrequencyAt(airport, kind),
                };
                if (airplane != Entity.Null && m_TransformData.TryGetComponent(airplane, out Transform transform))
                {
                    // Height above the airport (as the hold altitudes), for the pilot's check-in.
                    float fieldHeight = m_Airspaces.TryGetValue(airport, out AirportSpace space) ? space.m_Height : 0f;
                    message.m_Altitude = math.max(0, (int)math.round((transform.m_Position.y - fieldHeight) / (100f * kMetersPerFoot)) * 100);
                    float climb = m_MovingData.TryGetComponent(airplane, out Moving moving) ? moving.m_Velocity.y : 0f;
                    message.m_Vertical = (sbyte)(climb > kLevelClimbRate ? 1 : climb < -kLevelClimbRate ? -1 : 0);
                }
                m_LastMessages[airplane] = message;
                AddToRadioLog(message);
            }

            private void Say(Entity airplane, AtcMessageKind kind, in Flight flight, int value = 0, Entity hazard = default)
            {
                Say(airplane, kind, flight.m_Runway, flight.m_EntryDirection, value, hazard);
            }

            /// <summary>A transmission to all aircraft of an airport ("Attention all aircraft"): no readback, no airplane.</summary>
            private void Broadcast(Entity airport, AtcMessageKind kind, Entity hazard)
            {
                AtcMessage message = new AtcMessage
                {
                    m_Airport = airport,
                    m_Event = hazard,
                    m_Frame = m_Frame,
                    m_Kind = kind,
                };
                AddToRadioLog(message);
            }

            private void AddToRadioLog(in AtcMessage message)
            {
                // Diagnostics: every transmission, so a missing one can be told from one that was only scrolled away.
                Mod.Log.Info($"radio {message.m_Frequency} {message.m_Kind} {(message.m_Airplane == Entity.Null ? "all aircraft" : message.m_Airplane.ToString())} runway {message.m_Runway} airport {message.m_Airport}");
                m_RadioVersion.Value++;
                if (m_RadioLog.Length >= kRadioLogLength)
                {
                    m_RadioLog.RemoveAt(0);
                }
                m_RadioLog.Add(message);
            }

            /// <summary>
            /// Ground control's taxi clearance for a departure, given once when it is ready to leave its stand (route known,
            /// pushback done or not needed), naming the runway its route takes off from. It comes before any runway request
            /// (see <see cref="HasTaxiClearance"/>): at small airports the stand can be within the request distance of the runway.
            /// An airplane that taxied in after landing gets it once it stands again with a route onto a runway.
            /// </summary>
            private void ClearToTaxi(Entity entity)
            {
                if (m_NavigationLanes[entity].Length == 0)
                {
                    return;
                }
                if (m_LastMessages.TryGetValue(entity, out AtcMessage last) && last.m_Kind != AtcMessageKind.PushbackApproved)
                {
                    if (last.m_Kind != AtcMessageKind.TaxiToGate
                        || math.lengthsq(m_MovingData[entity].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed
                        || !FindTakeoffRunway(entity, out Entity nextRunway, out float2 nextDirection))
                    {
                        return;
                    }
                    Say(entity, AtcMessageKind.TaxiToRunway, nextRunway, nextDirection);
                    LogEvent(entity, "cleared to taxi to runway", nextRunway);
                    return;
                }
                // No message yet: leaving its stand, or (right after loading a city, when nobody has heard anything) already
                // taxiing. One taxiing in to a gate here is told so; anyone else is cleared to the runway, so that it can ask
                // for it (without a runway in sight of the path scan, just "taxi to the runway").
                if (!FindTakeoffRunway(entity, out Entity runway, out float2 direction) && IsTaxiingToGate(entity))
                {
                    Say(entity, AtcMessageKind.TaxiToGate, Entity.Null, default);
                    return;
                }
                Say(entity, AtcMessageKind.TaxiToRunway, runway, direction);
                LogEvent(entity, "cleared to taxi to runway", runway);
            }

            /// <summary>
            /// A ground airplane may ask the tower for a runway only after ground control cleared it to taxi (or to taxi in
            /// after landing, for a crossing on the way to its gate) and its pushback is over. "Taxi to the gate" doesn't
            /// clear a route that takes off: that airplane has boarded and waits for its pushback and "taxi to runway".
            /// </summary>
            private bool HasTaxiClearance(Entity entity)
            {
                if (m_Pushbacks.ContainsKey(entity) || !m_LastMessages.TryGetValue(entity, out AtcMessage last)
                    || last.m_Kind == AtcMessageKind.PushbackApproved)
                {
                    return false;
                }
                return (last.m_Kind != AtcMessageKind.TaxiToGate && last.m_Kind != AtcMessageKind.HoldForGate)
                    || !FindTakeoffRunway(entity, out _, out _);
            }

            /// <summary>The first runway the route takes off from (a lane of the runway proper, not a crossing), and the roll direction.</summary>
            private bool FindTakeoffRunway(Entity entity, out Entity runway, out float2 direction)
            {
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    if (IsTakeoffLane(navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, out runway, out direction))
                    {
                        return true;
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int start = m_PathOwnerData[entity].m_ElementIndex;
                int end = math.min(path.Length, start + kMaxPathScan);
                for (int i = start; i < end; i++)
                {
                    if (IsTakeoffLane(path[i].m_Target, path[i].m_TargetDelta, out runway, out direction))
                    {
                        return true;
                    }
                }
                runway = Entity.Null;
                direction = default;
                return false;
            }

            private bool IsTakeoffLane(Entity lane, float2 delta, out Entity runway, out float2 direction)
            {
                runway = Entity.Null;
                direction = default;
                if (!m_CarLaneData.TryGetComponent(lane, out Game.Net.CarLane carLane) || (carLane.m_Flags & Game.Net.CarLaneFlags.Runway) == 0
                    || !m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                runway = RunwayOfLane(lane);
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                direction = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                return runway != Entity.Null;
            }

            /// <summary>
            /// Handoffs between airspaces (phase 4-3): an airborne airplane is with the controller of the zone it is in. An
            /// arrival stays with its destination once it has called it; anyone else crossing into another zone is handed over
            /// ("contact Portland Approach ...") and calls the next controller, and leaving the last zone ends radar service.
            /// </summary>
            private void UpdateZone(in FlightScan scan)
            {
                if (!scan.m_Flying || m_Airspaces.IsEmpty)
                {
                    return;
                }
                Entity entity = scan.m_Entity;
                m_Zones.TryGetValue(entity, out ZoneState zone);
                if (zone.m_Climbing)
                {
                    // Just took off: it checks in with departure control once it has climbed, then is handed on as usual.
                    float fieldHeight = m_Airspaces.TryGetValue(zone.m_Airport, out AirportSpace field) ? field.m_Height : 0f;
                    zone.m_Tick = m_Tick;
                    if (m_TransformData[entity].m_Position.y - fieldHeight < kCheckInHeight && m_Frame - zone.m_TakeoffFrame < kCheckInTimeoutFrames)
                    {
                        m_Zones[entity] = zone;
                        return;
                    }
                    zone.m_Climbing = false;
                    m_Zones[entity] = zone;
                    if (zone.m_DepartureControl != 0)
                    {
                        SayAt(entity, AtcMessageKind.DepartureCheckIn, zone.m_Airport, Entity.Null, default, zone.m_DepartureControl, Entity.Null,
                            zone.m_DepartureControl == 2 ? AtcFrequency.Departure : AtcFrequency.Approach);
                    }
                }
                if (m_Flights.TryGetValue(entity, out Flight flight))
                {
                    // An arrival stays with its destination; a departure lifting off is still the tower's until it leaves the
                    // runway ("contact departure").
                    if (flight.m_Arrival)
                    {
                        zone.m_Tick = m_Tick;
                        m_Zones[entity] = zone;
                    }
                    return;
                }
                float2 position = m_TransformData[entity].m_Position.xz;
                Entity owner = ZoneOwner(position);
                // It stays with its controller while still inside that zone (no ping-pong along a boundary), unless it entered a
                // higher class's inner surface area.
                if (owner != zone.m_Airport && zone.m_Airport != Entity.Null && m_Airspaces.TryGetValue(zone.m_Airport, out AirportSpace current)
                    && math.distancesq(position, current.m_Center) <= current.m_Radius * current.m_Radius
                    && !(owner != Entity.Null && m_Airspaces[owner].m_Class > current.m_Class
                        && math.distance(position, m_Airspaces[owner].m_Center) < m_Airspaces[owner].m_Radius * kInnerZoneFactor))
                {
                    owner = zone.m_Airport;
                }
                if (owner == zone.m_Airport)
                {
                    if (owner != Entity.Null)
                    {
                        zone.m_Tick = m_Tick;
                        m_Zones[entity] = zone;
                    }
                    return;
                }
                if (zone.m_Airport != Entity.Null && !zone.m_Silent)
                {
                    if (owner != Entity.Null)
                    {
                        SayAt(entity, AtcMessageKind.HandoffTo, zone.m_Airport, Entity.Null, default, (int)m_Airspaces[owner].m_Class, owner, DepartureFrequency(zone));
                    }
                    else
                    {
                        SayAt(entity, AtcMessageKind.ContactCenter, zone.m_Airport, Entity.Null, default, 0, Entity.Null, DepartureFrequency(zone));
                    }
                }
                if (owner == Entity.Null)
                {
                    m_Zones.Remove(entity);
                    return;
                }
                SayAt(entity, AtcMessageKind.TransitContact, owner, Entity.Null, default, (int)m_Airspaces[owner].m_Class, Entity.Null);
                m_Zones[entity] = new ZoneState { m_Airport = owner, m_Tick = m_Tick };
            }

            /// <summary>Just took off and not yet checked in with departure: it doesn't call the next airport yet.</summary>
            private bool IsClimbingOut(Entity entity)
            {
                return m_Zones.TryGetValue(entity, out ZoneState zone) && zone.m_Climbing;
            }

            /// <summary>A class B departure still with its own airport is on that airport's departure frequency; else the usual one.</summary>
            private static AtcFrequency? DepartureFrequency(in ZoneState zone)
            {
                return zone.m_Departing ? AtcFrequency.Departure : (AtcFrequency?)null;
            }

            /// <summary>
            /// The airport whose airspace a point is in, as the airspace infoview draws it: a higher class's inner surface area
            /// first, else the zone the point is relatively deepest in (distance / radius); Entity.Null outside every zone.
            /// </summary>
            private Entity ZoneOwner(float2 point)
            {
                Entity owner = Entity.Null;
                float best = float.MaxValue;
                foreach (var pair in m_Airspaces)
                {
                    AirportSpace space = pair.Value;
                    float depth = math.distance(point, space.m_Center) / space.m_Radius;
                    if (depth > 1f)
                    {
                        continue;
                    }
                    if (space.m_Class != AirspaceClass.D && depth < kInnerZoneFactor)
                    {
                        depth -= 10f * (int)space.m_Class;
                    }
                    if (depth < best)
                    {
                        best = depth;
                        owner = pair.Key;
                    }
                }
                return owner;
            }

            /// <summary>Forgets airplanes that landed or are gone (not airborne for a while).</summary>
            private void EndZoneTick()
            {
                if (m_Zones.IsEmpty || m_Tick % kDiagnosticInterval != 0)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Zones)
                {
                    if (m_Tick - pair.Value.m_Tick > kDiagnosticInterval)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_Zones.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>The airspace class of a runway's airport (C when unknown).</summary>
            private AirspaceClass GetAirspaceClass(Entity runway)
            {
                return m_Runways.TryGetValue(runway, out RunwayState state) && m_Airspaces.TryGetValue(state.m_Airport, out AirportSpace space)
                    ? space.m_Class
                    : AirspaceClass.C;
            }

            private bool HasApproachControl(Entity runway) => GetAirspaceClass(runway) != AirspaceClass.D;

            /// <summary>The frequency of a message at this airport: a class D airport has no approach control, so its tower says it.</summary>
            private AtcFrequency FrequencyAt(Entity airport, AtcMessageKind kind)
            {
                AtcFrequency frequency = AtcMessage.FrequencyOf(kind);
                return frequency == AtcFrequency.Approach && m_Airspaces.TryGetValue(airport, out AirportSpace space) && space.m_Class == AirspaceClass.D
                    ? AtcFrequency.Tower
                    : frequency;
            }

            /// <summary>The disaster closing this runway this tick, or Entity.Null.</summary>
            private Entity ClosedBy(Entity runway)
            {
                return m_Runways.TryGetValue(runway, out RunwayState state) && state.IsClosed(m_Tick) ? state.m_ClosedBy : Entity.Null;
            }

            /// <summary>Forgets the latest message of airplanes that haven't heard from the tower for a long time (or are gone).</summary>
            private void PruneMessages()
            {
                if (m_LastMessages.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> old = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_LastMessages)
                {
                    if (m_Frame - pair.Value.m_Frame > kMessageLifetimeFrames)
                    {
                        old.Add(pair.Key);
                    }
                }
                for (int i = 0; i < old.Length; i++)
                {
                    m_LastMessages.Remove(old[i]);
                }
                old.Dispose();
            }

            /// <summary>The airport an airplane stands at: the top owner of its lane.</summary>
            private Entity GetAirportOf(Entity airplane)
            {
                Entity root = m_CurrentLaneData[airplane].m_Lane;
                while (m_OwnerData.TryGetComponent(root, out Owner owner))
                {
                    root = owner.m_Owner;
                }
                return root;
            }

            private void LogLongWait(Entity entity, Flight flight)
            {
                m_Runways.TryGetValue(flight.m_Runway, out RunwayState state);
                int occupants = state.Occupants(m_Tick);
                Mod.Log.Info($"{entity} waiting {(m_Frame - flight.m_RequestFrame) / 60} s for runway {flight.m_Runway} " +
                    $"({Describe(flight)}, #{flight.m_Sequence}); ground: {DescribeCleared(state.m_Holder)}; " +
                    $"next arrival ETA: {(state.HasArrivalInSequence(m_Tick) ? state.m_NextArrivalEta.ToString("F0") + " s" : "none")}; closed: {state.IsClosed(m_Tick)}; lined up: {DescribeCleared(state.m_LineUp)}; airplanes on runway: {occupants}");
            }

            private string DescribeCleared(Entity entity)
            {
                if (entity == Entity.Null || !m_Flights.TryGetValue(entity, out Flight flight))
                {
                    return "none";
                }
                return $"{entity} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, on runway: {flight.m_SeenOnRunway}, " +
                    $"cleared {(m_Frame - flight.m_ClearedFrame) / 60} s ago, holding: {flight.m_Holding}{DescribeBlocker(entity)})";
            }

            /// <summary>
            /// Diagnostics for airplanes parked askew at gates: collects what the mod did to each ground airplane after it
            /// landed, and when one starts boarding logs where it stands against its stand (the stop's route lane end):
            /// distance, heading against the lane, its current lane, and those interventions. Airplanes in the air start over.
            /// </summary>
            private void WatchStands(NativeArray<FlightScan> scans)
            {
                for (int i = 0; i < scans.Length; i++)
                {
                    FlightScan scan = scans[i];
                    if (scan.m_Flying)
                    {
                        continue;
                    }
                    Entity entity = scan.m_Entity;
                    m_StandWatch.TryGetValue(entity, out StandWatch watch);
                    watch.m_Tick = m_Tick;
                    bool boarding = IsBoarding(entity, out Entity waypoint, out Entity stop);
                    if (!boarding)
                    {
                        watch.m_Boarding = false;
                        if (m_Pushbacks.ContainsKey(entity)) watch.m_Touched |= StandWatch.kPushback;
                        if (m_HeldBehind.ContainsKey(entity)) watch.m_Touched |= StandWatch.kTaxiHold;
                        if (m_GateHolds.ContainsKey(entity)) watch.m_Touched |= StandWatch.kGateHold;
                        if (IsReleased(entity)) watch.m_Touched |= StandWatch.kDeadlockPass;
                        if (m_FreeSpeeds.ContainsKey(entity)) watch.m_Touched |= StandWatch.kBlockerPass;
                    }
                    else if (!watch.m_Boarding)
                    {
                        LogParking(entity, waypoint, stop, watch.m_Touched);
                        watch.m_Boarding = true;
                        watch.m_Touched = 0;
                    }
                    m_StandWatch[entity] = watch;
                }
                // Forget airplanes that took off or are gone.
                NativeList<Entity> old = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_StandWatch)
                {
                    if (pair.Value.m_Tick != m_Tick)
                    {
                        old.Add(pair.Key);
                    }
                }
                for (int i = 0; i < old.Length; i++)
                {
                    m_StandWatch.Remove(old[i]);
                }
                old.Dispose();
            }

            /// <summary>The airplane is the one boarding at the stop of its current target waypoint.</summary>
            private bool IsBoarding(Entity entity, out Entity waypoint, out Entity stop)
            {
                waypoint = m_TargetData.TryGetComponent(entity, out Game.Common.Target target) ? target.m_Target : Entity.Null;
                stop = m_ConnectedData.TryGetComponent(waypoint, out Game.Routes.Connected connected) ? connected.m_Connected : Entity.Null;
                return m_BoardingVehicleData.TryGetComponent(stop, out Game.Routes.BoardingVehicle boarding) && boarding.m_Vehicle == entity;
            }

            private void LogParking(Entity entity, Entity waypoint, Entity stop, byte touched)
            {
                Transform transform = m_TransformData[entity];
                float2 forward = math.normalizesafe(math.forward(transform.m_Rotation).xz);
                string stand = "no stand lane";
                if (m_RouteLaneData.TryGetComponent(waypoint, out Game.Routes.RouteLane routeLane) && m_CurveData.TryGetComponent(routeLane.m_EndLane, out Curve curve))
                {
                    float3 point = MathUtils.Position(curve.m_Bezier, routeLane.m_EndCurvePos);
                    float2 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, routeLane.m_EndCurvePos).xz);
                    float angle = math.degrees(math.acos(math.clamp(math.dot(forward, tangent), -1f, 1f)));
                    stand = $"{math.distance(transform.m_Position.xz, point.xz):F1} m from stand point, heading {angle:F0} deg from stand lane {routeLane.m_EndLane}@{routeLane.m_EndCurvePos:F2}";
                }
                string stopOffset = m_TransformData.TryGetComponent(stop, out Transform stopTransform)
                    ? $", {math.distance(transform.m_Position.xz, stopTransform.m_Position.xz):F1} m from stop" : string.Empty;
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                System.Text.StringBuilder mod = new System.Text.StringBuilder();
                if ((touched & StandWatch.kPushback) != 0) mod.Append(" pushback");
                if ((touched & StandWatch.kTaxiHold) != 0) mod.Append(" taxi-hold");
                if ((touched & StandWatch.kGateHold) != 0) mod.Append(" gate-hold");
                if ((touched & StandWatch.kDeadlockPass) != 0) mod.Append(" deadlock-pass");
                if ((touched & StandWatch.kBlockerPass) != 0) mod.Append(" blocker-pass");
                if (mod.Length == 0) mod.Append(" none");
                m_PrefabNames.TryGetValue(m_PrefabRefData[entity].m_Prefab, out FixedString128Bytes name);
                Mod.Log.Info($"{entity} [{name}] boarding at stop {stop}: {stand}{stopOffset}, on lane {currentLane.m_Lane}@{currentLane.m_CurvePosition.x:F2}; mod since landing:{mod}");
            }

            /// <summary>
            /// Diagnostics for gate blocks (research, see PLAN): once per landing, the lanes from the airplane to its gate. Each
            /// lane is "index:length/owner" with flags: T = the owner is a taxiway net, R = a runway, A = its top owner is the
            /// airport, S = a stand lane of the gate (the waypoint's RouteLane). Also who is boarding at the gate.
            /// </summary>
            private void LogPathToGate(Entity entity, Entity airport)
            {
                string gate = "none";
                Entity waypoint = Entity.Null;
                Game.Routes.RouteLane stand = default;
                if (m_TargetData.TryGetComponent(entity, out Game.Common.Target target))
                {
                    waypoint = target.m_Target;
                    m_RouteLaneData.TryGetComponent(waypoint, out stand);
                    if (m_ConnectedData.TryGetComponent(waypoint, out Game.Routes.Connected connected))
                    {
                        gate = m_BoardingVehicleData.TryGetComponent(connected.m_Connected, out Game.Routes.BoardingVehicle boarding)
                            ? $"{connected.m_Connected} (boarding: {boarding.m_Vehicle})"
                            : connected.m_Connected.ToString();
                    }
                }
                System.Text.StringBuilder lanes = new System.Text.StringBuilder();
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AppendLane(lanes, navigationLanes[i].m_Lane, airport, stand);
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, m_PathOwnerData[entity].m_ElementIndex + kMaxPathScan);
                for (int i = m_PathOwnerData[entity].m_ElementIndex; i < end; i++)
                {
                    AppendLane(lanes, path[i].m_Target, airport, stand);
                }
                Mod.Log.Info($"{entity} path to gate {gate} (waypoint {waypoint}, airport {airport}):{lanes}");
            }

            private void AppendLane(System.Text.StringBuilder lanes, Entity lane, Entity airport, in Game.Routes.RouteLane stand)
            {
                Entity owner = m_OwnerData.TryGetComponent(lane, out Owner laneOwner) ? laneOwner.m_Owner : Entity.Null;
                Entity top = owner;
                while (m_OwnerData.TryGetComponent(top, out Owner next))
                {
                    top = next.m_Owner;
                }
                float length = m_CurveData.TryGetComponent(lane, out Curve curve) ? curve.m_Length : 0f;
                lanes.Append(' ').Append(lane.Index).Append(':').Append(length.ToString("F0")).Append('/').Append(owner.Index);
                if (m_TaxiwayData.HasComponent(owner))
                {
                    lanes.Append('T');
                }
                if (m_RunwayOf.ContainsKey(owner))
                {
                    lanes.Append('R');
                }
                if (top == airport && airport != Entity.Null)
                {
                    lanes.Append('A');
                }
                if (lane == stand.m_StartLane || lane == stand.m_EndLane)
                {
                    lanes.Append('S');
                }
            }

            /// <summary>What the game itself is waiting for, if anything (diagnostics for an airplane that stopped).</summary>
            private string DescribeBlocker(Entity entity)
            {
                string held = m_HeldBehind.TryGetValue(entity, out Entity ahead) ? $", held by the mod behind {ahead}" : string.Empty;
                return m_BlockerData.TryGetComponent(entity, out Blocker blocker) && blocker.m_Blocker != Entity.Null
                    ? $", blocked by {blocker.m_Blocker} ({blocker.m_Type}){held}"
                    : held;
            }

            private uint NextSequence()
            {
                uint sequence = m_Sequence.Value + 1;
                m_Sequence.Value = sequence;
                return sequence;
            }

            private void DropFlight(Entity entity, Flight flight)
            {
                ReleaseRunway(entity, flight.m_Runway);
                if (flight.m_Holding)
                {
                    FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                }
                m_Flights.Remove(entity);
            }

            private void ReleaseRunway(Entity entity, Entity runway)
            {
                if (m_Runways.TryGetValue(runway, out RunwayState state)
                    && (state.m_Holder == entity || state.m_LineUp == entity))
                {
                    if (state.m_LineUp == entity)
                    {
                        state.m_LineUp = Entity.Null;
                    }
                    if (state.m_Holder == entity)
                    {
                        state.m_Holder = Entity.Null;
                    }
                    m_Runways[runway] = state;
                }
            }

            /// <summary>Drops requests of airplanes that no longer exist or were parked.</summary>
            private void RemoveStaleFlights()
            {
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    DropFlight(stale[i], m_Flights[stale[i]]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// Highest Response Ratio Next: ratio = (time waited + expected runway time) / expected runway time.
            /// Quick users of the runway go first, but every waiting airplane's ratio keeps growing, so none waits forever.
            /// </summary>
            private static float ResponseRatio(float waitedSeconds, float serviceSeconds)
            {
                serviceSeconds = math.max(serviceSeconds, kMinServiceSeconds);
                return (waitedSeconds + serviceSeconds) / serviceSeconds;
            }

            private float WaitedSeconds(uint requestFrame)
            {
                return (m_Frame - requestFrame) * kSecondsPerFrame;
            }

            /// <summary>
            /// For every runway with no ground airplane on or cleared for it, picks by response ratio between the best
            /// arrival and the head of the ground queue. The arrival is cleared when it wins, or when it is far enough out
            /// for the ground airplane to go first; the ground airplane goes when it wins or the cleared arrival is still at
            /// least the departure gap away.
            /// </summary>
            private void GrantRunways()
            {
                float gap = m_Settings.m_DepartureGapSeconds;
                ReleaseLinedUp(gap);
                UnblockGroundQueues();

                NativeParallelHashMap<Entity, Candidates> next = new NativeParallelHashMap<Entity, Candidates>(math.max(8, m_Runways.Count()), Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    Flight flight = pair.Value;
                    if (flight.m_Clearance != Clearance.Waiting
                        || !m_Runways.TryGetValue(flight.m_Runway, out RunwayState state)
                        || state.IsClosed(m_Tick))
                    {
                        continue;
                    }
                    next.TryGetValue(flight.m_Runway, out Candidates candidates);
                    if (flight.m_LastFlying)
                    {
                        // Ground delay: no new arrivals until the ground traffic has thinned out (the departures go first).
                        if (m_GroundDelays.ContainsKey(state.m_Airport))
                        {
                            continue;
                        }
                        float ratio = ResponseRatio(WaitedSeconds(flight.m_RequestFrame), flight.m_LastEta + kLandingRunwaySeconds);
                        if (candidates.m_Air == Entity.Null || ratio > candidates.m_AirRatio)
                        {
                            candidates.m_Air = pair.Key;
                            candidates.m_AirRatio = ratio;
                            // It joins only behind the last arrival cleared, with the spacing (and wake) kept. If it can't
                            // yet, it keeps its turn anyway: nobody else joins, so the sequence drains until it fits (a
                            // go-around airplane close to the runway would otherwise never get in).
                            candidates.m_AirReady = flight.m_LastEta >= EarliestArrivalEta(state, flight.m_Size, gap);
                        }
                    }
                    else
                    {
                        // Physically stuck behind another airplane waiting for this runway: that one is the real head of the line
                        // (path distances can't tell, e.g. when it heads for a farther entry point along the same taxiway).
                        if (IsQueuedBehindWaiting(pair.Key, flight.m_Runway))
                        {
                            continue;
                        }
                        if (!state.IsFree(m_Tick) && !CanLineUp(state))
                        {
                            continue;
                        }
                        if (candidates.m_Ground == Entity.Null || (int)(flight.m_RequestFrame - candidates.m_OldestGroundRequestFrame) < 0)
                        {
                            candidates.m_OldestGroundRequestFrame = flight.m_RequestFrame;
                        }
                        // The nearest goes first; at the same spot (e.g. both at the hold-short line) the one that asked first.
                        if (candidates.m_Ground == Entity.Null || flight.m_LastDistance < candidates.m_GroundDistance - kSameSpotDistance
                            || (flight.m_LastDistance <= candidates.m_GroundDistance + kSameSpotDistance
                                && (int)(flight.m_RequestFrame - candidates.m_GroundRequestFrame) < 0))
                        {
                            candidates.m_Ground = pair.Key;
                            candidates.m_GroundDistance = flight.m_LastDistance;
                            candidates.m_GroundRequestFrame = flight.m_RequestFrame;
                        }
                    }
                    next[flight.m_Runway] = candidates;
                }

                foreach (var pair in next)
                {
                    Entity runway = pair.Key;
                    Candidates candidates = pair.Value;
                    RunwayState state = m_Runways[runway];
                    float groundRatio = candidates.m_Ground == Entity.Null ? 0f : ResponseRatio(
                        WaitedSeconds(candidates.m_OldestGroundRequestFrame),
                        candidates.m_GroundDistance / kTaxiSpeedEstimate + kTakeoffRunwaySeconds);
                    bool groundFirst = candidates.m_Ground != Entity.Null
                        && (candidates.m_Air == Entity.Null || groundRatio > candidates.m_AirRatio);

                    // The next arrival joins the sequence, unless the ground airplane has waited longer (relatively): then
                    // no new arrival joins (the sequence drains to make a hole), except a first one far enough out to go after it.
                    if (candidates.m_AirReady)
                    {
                        Flight arrival = m_Flights[candidates.m_Air];
                        bool inSequence = state.HasArrivalInSequence(m_Tick);
                        if (!groundFirst || (!inSequence && arrival.m_LastEta > gap))
                        {
                            state.BeginArrivals(m_Tick);
                            state.AddArrival(arrival.m_LastEta, arrival.m_Size);
                            m_Runways[runway] = state;
                            Clear(candidates.m_Air, runway, inSequence ? "to land in sequence" : "to land", candidates.m_AirRatio);
                        }
                    }
                    if (candidates.m_Ground == Entity.Null)
                    {
                        continue;
                    }
                    if (state.IsFree(m_Tick))
                    {
                        if (state.NextArrivalEta(m_Tick) > gap)
                        {
                            state.m_Holder = candidates.m_Ground;
                            m_Runways[runway] = state;
                            Clear(candidates.m_Ground, runway, state.HasArrivalInSequence(m_Tick) ? "ahead of the arrival onto" : "onto", groundRatio);
                        }
                    }
                    else if (MayLineUpBehindTraffic(state, m_Flights[candidates.m_Ground], gap, out Entity traffic))
                    {
                        // Busy, but this airport uses line up and wait: the head of the ground queue may line up behind
                        // traffic that has already passed its entry point.
                        state.m_LineUp = candidates.m_Ground;
                        m_Runways[runway] = state;
                        Flight flight = m_Flights[candidates.m_Ground];
                        flight.m_Clearance = Clearance.LineUp;
                        flight.m_ClearedFrame = m_Frame;
                        m_Flights[candidates.m_Ground] = flight;
                        Say(candidates.m_Ground, AtcMessageKind.LineUpAndWait, flight);
                        LogEvent(candidates.m_Ground, $"cleared to line up and wait (behind {traffic}, waited {(m_Frame - flight.m_RequestFrame) / 60} s) on runway", runway);
                    }
                }
                next.Dispose();
            }

            /// <summary>
            /// A ground airplane cleared onto (or to line up on) a runway that the taxi spacing holds behind another airplane
            /// still waiting for the same runway can never get there: they would wait for each other forever. Its clearance goes
            /// back (it keeps its place in time) so that the airplane ahead, the real head of the line, is cleared instead.
            /// </summary>
            private void UnblockGroundQueues()
            {
                if (m_HeldBehind.IsEmpty)
                {
                    return;
                }
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    RunwayState state = m_Runways[runways[i]];
                    TakeBackIfQueued(state.m_Holder, runways[i]);
                    TakeBackIfQueued(state.m_LineUp, runways[i]);
                }
                runways.Dispose();
            }

            private void TakeBackIfQueued(Entity entity, Entity runway)
            {
                if (entity == Entity.Null || !IsQueuedBehindWaiting(entity, runway) || !m_Flights.TryGetValue(entity, out Flight flight)
                    || flight.m_SeenOnRunway)
                {
                    return;
                }
                ReleaseRunway(entity, runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                LogEvent(entity, $"is stuck behind {m_HeldBehind[entity]}, which waits for the same runway; clearance passed on, runway", runway);
            }

            /// <summary>Last tick the taxi spacing held this airplane behind a ground airplane waiting for the same runway.</summary>
            private bool IsQueuedBehindWaiting(Entity entity, Entity runway)
            {
                return m_HeldBehind.TryGetValue(entity, out Entity ahead) && m_Flights.TryGetValue(ahead, out Flight other)
                    && other.m_Runway == runway && other.m_Clearance == Clearance.Waiting && !other.m_LastFlying;
            }

            /// <summary>
            /// The soonest an arrival of this size may reach the runway: the arrival spacing (plus wake separation when a
            /// larger airplane is ahead) behind the last cleared arrival; with none, the departure gap while a ground
            /// airplane is using the runway.
            /// </summary>
            private float EarliestArrivalEta(in RunwayState state, SizeClass size, float gap)
            {
                if (state.HasArrivalInSequence(m_Tick))
                {
                    return state.m_LastArrivalEta + m_Settings.m_ArrivalSpacingSeconds + WakeSeconds(state.m_LastArrivalSize, size);
                }
                return state.IsFree(m_Tick) ? 0f : gap;
            }

            private static float WakeSeconds(SizeClass leader, SizeClass follower)
            {
                int steps = SizeRank(leader) - SizeRank(follower);
                return steps > 0 ? steps * kWakeSecondsPerClass : 0f;
            }

            private static int SizeRank(SizeClass size) => size == SizeClass.Undefined ? (int)SizeClass.Medium : (int)size;

            /// <summary>The runway's airport uses line up and wait, and nobody is lined up on it yet.</summary>
            private bool CanLineUp(in RunwayState state)
            {
                return state.m_LineUp == Entity.Null && state.m_Airport != Entity.Null && m_LineUpAirportData.HasComponent(state.m_Airport);
            }

            /// <summary>
            /// "Behind the landing / departing traffic, line up and wait": no arrival is due within the departure gap (else the
            /// lined-up airplane would still be on the runway when it arrives), the only airplane on the runway is either an
            /// arrival rolling out after touchdown or the departure on its takeoff roll, and it is already well past the
            /// point where the waiting airplane would enter, rolling away.
            /// </summary>
            private bool MayLineUpBehindTraffic(in RunwayState state, in Flight candidate, float gap, out Entity traffic)
            {
                traffic = Entity.Null;
                if (!CanLineUp(state) || state.Occupants(m_Tick) != 1 || state.NextArrivalEta(m_Tick) <= gap
                    || math.lengthsq(candidate.m_EntryDirection) < 0.5f)
                {
                    return false;
                }
                if (state.m_Holder != Entity.Null)
                {
                    if (!m_Flights.TryGetValue(state.m_Holder, out Flight departure)
                        || departure.m_OnRunwayTick != m_Tick)
                    {
                        return false;
                    }
                    traffic = state.m_Holder;
                }
                else
                {
                    traffic = state.LandingTraffic(m_Tick);
                    if (traffic == Entity.Null)
                    {
                        return false;
                    }
                }
                float2 direction = candidate.m_EntryDirection;
                float2 offset = m_TransformData[traffic].m_Position.xz - candidate.m_EntryPosition.xz;
                return math.dot(offset, direction) > kLineUpBehindDistance
                    && math.dot(m_MovingData[traffic].m_Velocity.xz, direction) >= 0f;
            }

            /// <summary>
            /// Gives the takeoff (or crossing) clearance to a lined-up airplane as soon as the runway ahead of it is clear,
            /// without waiting on the arrivals: if it hasn't lifted off when the next arrival reaches the decision height, that
            /// arrival goes around. One that hasn't reached the runway yet when an arrival gets within half the departure gap
            /// holds short instead, if it can still stop; one already entering carries on.
            /// </summary>
            private void ReleaseLinedUp(float gap)
            {
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    RunwayState state = m_Runways[runways[i]];
                    if (state.m_LineUp == Entity.Null || state.m_Holder != Entity.Null || !m_Flights.TryGetValue(state.m_LineUp, out Flight flight))
                    {
                        continue;
                    }
                    float nextArrival = state.NextArrivalEta(m_Tick);
                    bool onRunway = flight.m_OnRunwayTick == m_Tick;
                    if (!onRunway && nextArrival <= gap * 0.5f)
                    {
                        RevokeGroundClearance(state.m_LineUp, ref flight, AtcMessageKind.HoldShortForArrival);
                        continue;
                    }
                    if (state.IsClosed(m_Tick) || state.Occupants(m_Tick) != (onRunway ? 1 : 0))
                    {
                        continue;
                    }
                    state.m_Holder = state.m_LineUp;
                    state.m_LineUp = Entity.Null;
                    m_Runways[runways[i]] = state;
                    Clear(state.m_Holder, runways[i], "for takeoff after lining up on", 0f);
                }
                runways.Dispose();
            }

            private void Clear(Entity entity, Entity runway, string what, float ratio)
            {
                Flight flight = m_Flights[entity];
                flight.m_Clearance = Clearance.Cleared;
                flight.m_ClearedFrame = m_Frame;
                flight.m_Told = false;
                flight.m_ToldLanding = false;
                m_Flights[entity] = flight;
                if (flight.m_LastFlying)
                {
                    // Approach clears it for the approach and hands it to the tower; the tower clears it to land once it is on
                    // final (UpdateFlight). After a go-around it is already with the tower, which only gives the landing clearance.
                    if (!flight.m_WentAround)
                    {
                        m_Runways.TryGetValue(runway, out RunwayState state);
                        Say(entity, AtcMessageKind.ClearedApproach, flight, state.m_ArrivalTick == m_Tick ? math.max(1, state.m_ArrivalCount) : 1);
                        if (HasApproachControl(runway))
                        {
                            // Handed to the tower: the pilot checks in on the tower frequency.
                            Say(entity, AtcMessageKind.TowerCheckIn, flight);
                        }
                    }
                }
                else
                {
                    Say(entity, flight.m_Crossing ? AtcMessageKind.CrossRunway : AtcMessageKind.ClearedForTakeoff, flight);
                }
                LogEvent(entity, $"cleared {what} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, waited {(m_Frame - flight.m_RequestFrame) / 60} s, ratio {ratio:F2}) runway", runway);
            }

            // ------------------------------------------------------------ steering

            private void Apply(in FlightScan scan, NativeList<PushbackArea> pushbackAreas)
            {
                Entity entity = scan.m_Entity;
                if (!scan.m_Flying && (scan.m_Occupied != Entity.Null || IsReleased(entity)))
                {
                    PassAirborneBlocker(entity);
                }
                if (!scan.m_Flying && m_Settings.m_Pushback)
                {
                    UpdatePushback(entity);
                    if (pushbackAreas.Length != 0 && scan.m_Occupied == Entity.Null && !m_Pushbacks.ContainsKey(entity))
                    {
                        GiveWayToPushbacks(entity, pushbackAreas);
                    }
                }
                if (!scan.m_Flying && scan.m_Occupied == Entity.Null && !scan.m_PathUnknown && !m_Pushbacks.ContainsKey(entity) && !m_Flights.ContainsKey(entity))
                {
                    ClearToTaxi(entity);
                    if (m_LastMessages.TryGetValue(entity, out AtcMessage last) && last.m_Kind == AtcMessageKind.TaxiToRunway
                        && m_Frame - last.m_Frame < kResponseFrames)
                    {
                        StandStill(entity);
                    }
                    HoldForGate(entity);
                }
                if (!scan.m_Flying && scan.m_Occupied == Entity.Null && m_Settings.m_TaxiSpacing > 0f)
                {
                    KeepTaxiSpacing(entity);
                }
                if (!m_Flights.TryGetValue(entity, out Flight flight))
                {
                    return;
                }
                if (flight.m_Clearance != Clearance.Waiting && !flight.m_TouchedDown
                    && m_Runways.TryGetValue(flight.m_Runway, out RunwayState runwayState) && runwayState.IsClosed(m_Tick)
                    && CancelForClosure(entity, scan, ref flight))
                {
                    return;
                }
                if (!scan.m_Flying && flight.m_Clearance != Clearance.Waiting && m_Frame - flight.m_ClearedFrame < kResponseFrames
                    && math.lengthsq(m_MovingData[entity].m_Velocity) < kPushbackStartSpeed * kPushbackStartSpeed)
                {
                    // A standing airplane reads the clearance back before it moves (it is not shown starting its roll first).
                    StandStill(entity);
                    return;
                }
                if (flight.m_Told && flight.m_Clearance == Clearance.Cleared && !flight.m_Arrival && !scan.m_Flying && scan.m_Occupied == flight.m_Runway)
                {
                    // Its takeoff clearance was cancelled for a closure that is now over: cleared again (moving a second later).
                    flight.m_Told = false;
                    flight.m_ClearedFrame = m_Frame;
                    m_Flights[entity] = flight;
                    Say(entity, AtcMessageKind.ClearedForTakeoff, flight);
                    StandStill(entity);
                    return;
                }
                if (flight.m_Clearance == Clearance.LineUp && !scan.m_Flying)
                {
                    HoldAtLineUpPoint(entity, scan);
                    return;
                }
                if (flight.m_InCircuit)
                {
                    FlyCircuit(entity, ref flight);
                    m_HoldingFlights.Add(entity);
                    return;
                }
                if (scan.m_Occupied == flight.m_Runway && !flight.m_Holding)
                {
                    if (scan.m_Flying && !scan.m_TakingOff && flight.m_Clearance == Clearance.Cleared && !flight.m_TouchedDown
                        && IsBelowDecisionHeight(entity, flight))
                    {
                        if (!IsRunwayClearToLand(flight.m_Runway, out string reason))
                        {
                            GoAround(entity, ref flight, reason, false);
                            FlyCircuit(entity, ref flight);
                            m_HoldingFlights.Add(entity);
                        }
                        else if (!flight.m_WeatherChecked)
                        {
                            // The pilot's own decision at minimums: in rain or snow, now and then the runway isn't in sight.
                            flight.m_WeatherChecked = true;
                            m_Flights[entity] = flight;
                            if (m_Settings.m_WeatherGoAroundChance > 0f && RollWeatherGoAround(entity))
                            {
                                GoAround(entity, ref flight, m_Settings.m_Snowing ? "weather: snow" : "weather: rain", true);
                                FlyCircuit(entity, ref flight);
                                m_HoldingFlights.Add(entity);
                            }
                        }
                    }
                    return;
                }
                if (flight.m_Holding)
                {
                    if (flight.m_Clearance == Clearance.Cleared && m_Frame - flight.m_ClearedFrame >= kResponseFrames)
                    {
                        ReturnToPath(entity, ref flight);
                    }
                    else
                    {
                        Hold(entity, ref flight);
                    }
                    if (flight.m_Holding)
                    {
                        m_HoldingFlights.Add(entity);
                    }
                }
                else if (flight.m_Clearance == Clearance.Waiting)
                {
                    if (scan.m_Flying)
                    {
                        StartHolding(entity, ref flight);
                        Hold(entity, ref flight);
                        m_HoldingFlights.Add(entity);
                    }
                    else if (!scan.m_PathUnknown)
                    {
                        HoldShort(entity, scan);
                        if (!flight.m_Told)
                        {
                            flight.m_Told = true;
                            m_Flights[entity] = flight;
                            Say(entity, AtcMessageKind.HoldShort, flight);
                        }
                    }
                }
            }

            /// <summary>
            /// The airport is closed: an arrival on final goes around (and then holds, as nothing is cleared), one further
            /// out holds, and a ground airplane not yet on the runway holds short. Airplanes already on the runway carry on.
            /// Returns true when the airplane has been steered for this tick.
            /// </summary>
            private bool CancelForClosure(Entity entity, in FlightScan scan, ref Flight flight)
            {
                if (!scan.m_Flying)
                {
                    if (!flight.m_SeenOnRunway)
                    {
                        RevokeGroundClearance(entity, ref flight, AtcMessageKind.HoldShortClosed);
                        return false;
                    }
                    // On the runway with a takeoff clearance but not on its takeoff roll yet (still entering, or standing lined up):
                    // the tower cancels it and the airplane holds in position until the airport reopens. Only one on its takeoff
                    // roll (the game's TakingOff state, wheels rolling) carries on; speed alone can't tell entering from rolling.
                    bool rolling = scan.m_TakingOff && math.lengthsq(m_MovingData[entity].m_Velocity) >= kRollingSpeed * kRollingSpeed;
                    if (flight.m_Clearance == Clearance.Cleared && !flight.m_Arrival && scan.m_Occupied == flight.m_Runway && !rolling)
                    {
                        // Back to "line up and wait": it goes on to the line-up point and waits there lined up (rather than stopping
                        // half on the runway), and the usual release clears it for takeoff when the airport reopens.
                        if (m_Runways.TryGetValue(flight.m_Runway, out RunwayState lineUpState) && (lineUpState.m_LineUp == Entity.Null || lineUpState.m_LineUp == entity))
                        {
                            ReleaseRunway(entity, flight.m_Runway);
                            lineUpState = m_Runways[flight.m_Runway];
                            lineUpState.m_LineUp = entity;
                            m_Runways[flight.m_Runway] = lineUpState;
                            flight.m_Clearance = Clearance.LineUp;
                            m_Flights[entity] = flight;
                            LogEvent(entity, "takeoff clearance cancelled (airport closed), lining up and waiting on runway", flight.m_Runway);
                            Say(entity, AtcMessageKind.CancelTakeoff, flight, 1, ClosedBy(flight.m_Runway));
                            HoldAtLineUpPoint(entity, scan);
                            return true;
                        }
                        // Someone else is lined up already (rare): it holds where it is until the airport reopens.
                        StandStill(entity);
                        if (!flight.m_Told)
                        {
                            flight.m_Told = true;
                            m_Flights[entity] = flight;
                            LogEvent(entity, "takeoff clearance cancelled (airport closed), holding in position on runway", flight.m_Runway);
                            Say(entity, AtcMessageKind.CancelTakeoff, flight, 0, ClosedBy(flight.m_Runway));
                        }
                        return true;
                    }
                    return false;
                }
                if (scan.m_TakingOff)
                {
                    return false;
                }
                if (scan.m_Occupied == flight.m_Runway && !flight.m_Holding && !flight.m_InCircuit)
                {
                    GoAround(entity, ref flight, "airport closed", false);
                    FlyCircuit(entity, ref flight);
                    m_HoldingFlights.Add(entity);
                    return true;
                }
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                LogEvent(entity, "approach clearance cancelled (airport closed), holding for runway", flight.m_Runway);
                Say(entity, flight.m_ToldLanding ? AtcMessageKind.LandingClearanceCancelled : AtcMessageKind.ApproachClearanceCancelled, flight, 0,
                    ClosedBy(flight.m_Runway));
                return false;
            }

            /// <summary>
            /// The landing check at the decision height. A ground airplane cleared onto the runway (or to line up) that hasn't
            /// entered it yet is told to hold short instead, as a tower would.
            /// </summary>
            private bool IsRunwayClearToLand(Entity runway, out string reason)
            {
                reason = null;
                if (!m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    return true;
                }
                if (state.IsClosed(m_Tick))
                {
                    reason = "airport closed";
                    return false;
                }
                // Only airplanes still on the ground: a departure that has lifted off is no longer in the way.
                if (state.GroundOccupants(m_Tick) != 0)
                {
                    reason = "runway not clear";
                    return false;
                }
                RevokeIfNotOnRunway(state.m_Holder);
                RevokeIfNotOnRunway(state.m_LineUp);
                return true;
            }

            private void RevokeIfNotOnRunway(Entity entity)
            {
                if (entity != Entity.Null && m_Flights.TryGetValue(entity, out Flight flight) && !flight.m_LastFlying && !flight.m_SeenOnRunway)
                {
                    RevokeGroundClearance(entity, ref flight, AtcMessageKind.HoldShortForArrival);
                }
            }

            /// <summary>
            /// Takes a ground airplane's runway or line-up clearance back, so it holds short on the taxiway it is still on.
            /// Not when it can no longer stop at its hold-short point: then it is committed and carries on.
            /// </summary>
            private void RevokeGroundClearance(Entity entity, ref Flight flight, AtcMessageKind message)
            {
                if (!CanHoldShort(entity, flight))
                {
                    return;
                }
                ReleaseRunway(entity, flight.m_Runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                bool closed = message == AtcMessageKind.HoldShortClosed;
                LogEvent(entity, $"clearance cancelled ({(closed ? "airport closed" : "arrival close")}), holding short of runway", flight.m_Runway);
                Say(entity, message, flight, 0, closed ? ClosedBy(flight.m_Runway) : Entity.Null);
            }

            // ------------------------------------------------------------ pushback

            /// <summary>
            /// The game turns an airplane leaving a nose-in gate around on the spot. Instead, an airplane that starts moving
            /// with its nose against its path is pushed back: it rolls backwards along the path with the nose locked, stops
            /// where the path turns onto the taxiway, and is swung around until it faces along the path. Only the speed
            /// and the facing direction the game computed are replaced; its path and lane state are untouched.
            /// </summary>
            private void UpdatePushback(Entity entity)
            {
                bool active = m_Pushbacks.TryGetValue(entity, out Pushback pushback);
                // Only an airplane standing still (at its gate) can begin a pushback. Airplanes at a gate keep their
                // Moving component while boarding, so "just started moving" can't be used to find a departure.
                if (!active && (math.lengthsq(m_MovingData[entity].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed
                    || m_NavigationLanes[entity].Length == 0))
                {
                    // Standing still with somewhere to go: an airplane still boarding has no route lanes yet.
                    return;
                }
                Transform transform = m_TransformData[entity];
                AircraftNavigation navigation = m_NavigationData[entity];
                float2 pathDirection = GetPathDirection(entity, transform.m_Position, navigation);
                if (math.lengthsq(pathDirection) < 0.5f)
                {
                    if (!active)
                    {
                        return;
                    }
                    // The path is being searched again (common right after leaving a gate). Stand still with the nose
                    // locked instead of letting the game spin the airplane around; give up only after a while.
                    if (++pushback.m_UnknownPathTicks > kPushbackUnknownPathTicks)
                    {
                        m_Pushbacks.Remove(entity);
                        Mod.Log.Info($"{entity} pushback cancelled: no path");
                        return;
                    }
                    pushback.m_LastTick = m_Tick;
                    navigation.m_MaxSpeed = 0f;
                    navigation.m_TargetDirection = new float3(pushback.m_Heading.x, 0f, pushback.m_Heading.y);
                    m_NavigationData[entity] = navigation;
                    m_Pushbacks[entity] = pushback;
                    return;
                }
                if (!active)
                {
                    float2 forward = math.normalizesafe(math.forward(transform.m_Rotation).xz);
                    if (math.dot(forward, pathDirection) > kPushbackStartDot)
                    {
                        return;
                    }
                    float2 size = m_PrefabGeometryData.TryGetComponent(m_PrefabRefData[entity].m_Prefab, out ObjectGeometryData geometry)
                        ? geometry.m_Size.xz : new float2(30f, 30f);
                    pushback = new Pushback
                    {
                        m_Start = transform.m_Position,
                        m_Axis = pathDirection,
                        m_Heading = forward,
                        m_Phase = PushbackPhase.AwaitingApproval,
                        m_PhaseFrame = m_Frame,
                        // A short push, about half the airplane's length, then it turns towards the taxiway.
                        m_MaxDistance = math.clamp(size.y * kPushbackLengthFactor, kMinPushbackDistance, kMaxPushbackDistance),
                        m_Radius = 0.5f * math.cmax(size) + kPushbackClearance,
                    };
                    Mod.Log.Info($"{entity} ready for pushback (nose vs path cos {math.dot(forward, pathDirection):F2})");
                }
                pushback.m_LastTick = m_Tick;
                pushback.m_UnknownPathTicks = 0;

                switch (pushback.m_Phase)
                {
                    case PushbackPhase.AwaitingApproval:
                        // Nothing has moved yet: keep the push laid out from where the airplane stands.
                        pushback.m_Start = transform.m_Position;
                        pushback.m_Axis = pathDirection;
                        if (IsAtClosedAirport(entity))
                        {
                            // Ground stop: no pushbacks while the airport is closed; the approval wait starts over when it reopens.
                            pushback.m_PhaseFrame = m_Frame;
                            navigation.m_MaxSpeed = 0f;
                        }
                        else if (IsPushbackClear(entity, pushback) || (m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame > kPushbackApprovalTimeout)
                        {
                            pushback.m_Phase = PushbackPhase.Pushing;
                            Mod.Log.Info($"{entity} pushback approved after {(m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame:F0} s");
                            Say(entity, AtcMessageKind.PushbackApproved, Entity.Null, default);
                            pushback.m_PhaseFrame = m_Frame;
                        }
                        else
                        {
                            navigation.m_MaxSpeed = 0f;
                        }
                        break;

                    case PushbackPhase.Pushing:
                        if (m_Frame - pushback.m_PhaseFrame < kResponseFrames)
                        {
                            // The readback first: the push starts a moment after the approval.
                            pushback.m_Start = transform.m_Position;
                            navigation.m_MaxSpeed = 0f;
                            break;
                        }
                        float pushed = math.distance(transform.m_Position.xz, pushback.m_Start.xz);
                        if (pushed < kPushbackSettleDistance)
                        {
                            // The first lanes out of a stand (gate connections) can point a little off the stand's axis;
                            // follow the path for a few metres before watching for the turn.
                            pushback.m_Axis = pathDirection;
                        }
                        else if (pushed > pushback.m_MaxDistance || math.dot(pathDirection, pushback.m_Axis) < kPushbackTurnDot)
                        {
                            pushback.m_Phase = PushbackPhase.Turning;
                            Mod.Log.Info($"{entity} pushback turning after {pushed:F0} m");
                        }
                        navigation.m_MaxSpeed = pushback.m_Phase == PushbackPhase.Pushing ? math.min(navigation.m_MaxSpeed, kPushbackSpeed) : 0f;
                        break;

                    case PushbackPhase.Turning:
                        // Swing towards the path direction at the tug's turn rate, standing still.
                        float2 heading = pushback.m_Heading;
                        float angle = math.atan2(heading.x * pathDirection.y - heading.y * pathDirection.x, math.dot(heading, pathDirection));
                        float step = math.radians(kPushbackTurnRate) * kTimeStep;
                        if (math.abs(angle) <= step)
                        {
                            pushback.m_Heading = pathDirection;
                            pushback.m_Phase = PushbackPhase.EngineStart;
                            pushback.m_PhaseFrame = m_Frame;
                        }
                        else
                        {
                            math.sincos(math.sign(angle) * step, out float sin, out float cos);
                            pushback.m_Heading = math.normalize(new float2(heading.x * cos - heading.y * sin, heading.x * sin + heading.y * cos));
                        }
                        navigation.m_MaxSpeed = 0f;
                        break;

                    case PushbackPhase.EngineStart:
                        if ((m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame >= kEngineStartSeconds)
                        {
                            // Engines running, tug gone: hand the airplane back to the game, facing along its path.
                            m_Pushbacks.Remove(entity);
                            Mod.Log.Info($"{entity} pushback done after {math.distance(transform.m_Position.xz, pushback.m_Start.xz):F0} m");
                            navigation.m_TargetDirection = new float3(pathDirection.x, 0f, pathDirection.y);
                            m_NavigationData[entity] = navigation;
                            return;
                        }
                        navigation.m_MaxSpeed = 0f;
                        break;
                }
                // On the ground the move system faces the target direction when one is set.
                navigation.m_TargetDirection = new float3(pushback.m_Heading.x, 0f, pushback.m_Heading.y);
                m_NavigationData[entity] = navigation;
                m_Pushbacks[entity] = pushback;
            }

            /// <summary>
            /// Ground control's pushback approval: no airplane moving within kPushbackApprovalMargin of the area the push
            /// would cover, and none standing in it.
            /// </summary>
            private bool IsPushbackClear(Entity entity, in Pushback pushback)
            {
                float2 from = pushback.m_Start.xz;
                float2 to = from + pushback.m_Axis * pushback.m_MaxDistance;
                float movingLimit = pushback.m_Radius + kPushbackApprovalMargin;
                for (int i = 0; i < m_Airplanes.Length; i++)
                {
                    Entity other = m_Airplanes[i];
                    if (other == entity || (m_CurrentLaneData[other].m_LaneFlags & AircraftLaneFlags.Flying) != 0)
                    {
                        continue;
                    }
                    float limit = math.lengthsq(m_MovingData[other].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed
                        ? movingLimit
                        : pushback.m_Radius;
                    if (DistanceToSegmentSq(m_TransformData[other].m_Position.xz, from, to) < limit * limit)
                    {
                        return false;
                    }
                }
                return true;
            }

            /// <summary>Direction of travel along the path at the airplane's position (the current lane's direction).</summary>
            private float2 GetPathDirection(Entity entity, float3 position, in AircraftNavigation navigation)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float span = currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x;
                if (math.abs(span) > 0.001f && m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    float2 tangent = MathUtils.Tangent(curve.m_Bezier, currentLane.m_CurvePosition.x).xz;
                    return math.normalizesafe(math.select(tangent, -tangent, span < 0f));
                }
                return math.normalizesafe(navigation.m_TargetPosition.xz - position.xz);
            }

            /// <summary>
            /// The ground each pushing airplane covers: from where it is now along the rest of its push (just its own spot
            /// once it is turning). Built once per tick from last tick's pushback state.
            /// </summary>
            private NativeList<PushbackArea> CollectPushbackAreas()
            {
                NativeList<PushbackArea> areas = new NativeList<PushbackArea>(m_Pushbacks.Count(), Allocator.Temp);
                if (m_Pushbacks.IsEmpty)
                {
                    return areas;
                }
                foreach (var pair in m_Pushbacks)
                {
                    if (pair.Value.m_Phase == PushbackPhase.AwaitingApproval)
                    {
                        // Not approved yet: it isn't moving, and traffic must be free to pass so that it can be approved.
                        continue;
                    }
                    if (!m_TransformData.TryGetComponent(pair.Key, out Transform transform))
                    {
                        continue;
                    }
                    Pushback pushback = pair.Value;
                    float2 from = transform.m_Position.xz;
                    float remaining = pushback.m_Phase == PushbackPhase.Pushing
                        ? math.max(0f, pushback.m_MaxDistance - math.distance(from, pushback.m_Start.xz))
                        : 0f;
                    areas.Add(new PushbackArea
                    {
                        m_Entity = pair.Key,
                        m_From = from,
                        m_To = from + pushback.m_Axis * remaining,
                        m_Radius = pushback.m_Radius,
                    });
                }
                return areas;
            }

            /// <summary>
            /// A taxiing airplane stops before its path enters the area of an airplane being pushed back, the way ground
            /// control holds traffic behind a pushback. The pushing airplane has priority, so the two can't block each other.
            /// </summary>
            private void GiveWayToPushbacks(Entity entity, NativeList<PushbackArea> areas)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                AircraftData aircraftData = m_PrefabAircraftData[prefab];
                AircraftNavigation navigation = m_NavigationData[entity];
                float nose = GetNoseLength(prefab);
                float limit = nose + VehicleUtils.GetBrakingDistance(aircraftData, navigation.m_MaxSpeed, kTimeStep) + kTaxiScanMargin;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance = 0f;
                float hit = float.MaxValue;
                Entity pusher = Entity.Null;
                if (!SamplePathForPushbacks(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, limit, areas, ref distance, ref hit, ref pusher))
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length; i++)
                    {
                        if (SamplePathForPushbacks(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, limit, areas, ref distance, ref hit, ref pusher))
                        {
                            break;
                        }
                    }
                }
                if (hit == float.MaxValue)
                {
                    return;
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(aircraftData, math.max(0f, hit - nose), kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed && MayHoldBehind(entity, pusher))
                {
                    m_HeldBehind[entity] = pusher;
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Walks the traversed part of one lane in steps and records the path distance of the first point inside a
            /// pushback area. Returns true when done: an area was hit or the look-ahead limit was passed.
            /// </summary>
            private bool SamplePathForPushbacks(Entity entity, Entity lane, float2 delta, float limit, NativeList<PushbackArea> areas, ref float distance,
                ref float hit, ref Entity pusher)
            {
                if (!m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                float length = curve.m_Length * math.abs(delta.y - delta.x);
                int steps = math.max(1, (int)math.ceil(length / kPushbackSampleStep));
                for (int step = 0; step <= steps; step++)
                {
                    float along = length * step / steps;
                    if (distance + along > limit)
                    {
                        return true;
                    }
                    float2 point = MathUtils.Position(curve.m_Bezier, math.lerp(delta.x, delta.y, (float)step / steps)).xz;
                    for (int i = 0; i < areas.Length; i++)
                    {
                        PushbackArea area = areas[i];
                        if (area.m_Entity != entity && DistanceToSegmentSq(point, area.m_From, area.m_To) < area.m_Radius * area.m_Radius)
                        {
                            hit = distance + along;
                            pusher = area.m_Entity;
                            return true;
                        }
                    }
                }
                distance += length;
                return distance > limit;
            }

            private static float DistanceToSegmentSq(float2 point, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = math.saturate(math.dot(point - a, ab) / math.max(math.lengthsq(ab), 1e-4f));
                return math.distancesq(point, a + ab * t);
            }

            /// <summary>Forgets pushbacks of airplanes that stopped being tracked (parked, deleted or flying).</summary>
            private void EndPushbackTick()
            {
                if (m_Pushbacks.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Pushbacks)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_Pushbacks.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// Gate hold: an airplane taxiing to a gate that another airplane still uses (boarding there, or standing on the
            /// stand lanes, e.g. pushing back) stops kGateHoldDistance before the stand, clear of the pushback area, so the
            /// one leaving can always get out. Ground control tells it to hold, and to taxi on once the gate is free.
            /// One that is already closer goes on to the stand and waits there, as in the game.
            /// </summary>
            private void HoldForGate(Entity entity)
            {
                bool told = m_LastMessages.TryGetValue(entity, out AtcMessage last) && last.m_Kind == AtcMessageKind.HoldForGate;
                if (!IsGateOccupied(entity, out Game.Routes.RouteLane stand, out Entity occupant))
                {
                    if (told)
                    {
                        Say(entity, AtcMessageKind.TaxiToGate, Entity.Null, default);
                    }
                    return;
                }
                // Only a gate at this airport, on the taxi in (a departure's next stop is somewhere else).
                float distance = DistanceToLane(entity, stand.m_StartLane);
                if (distance < 0f || distance > kGateLookAhead)
                {
                    return;
                }
                // It waits for the airplane on its gate (deadlock detection follows these).
                m_GateHolds[entity] = occupant;
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float stopDistance = distance - GetNoseLength(prefab) - kGateHoldDistance;
                if (stopDistance <= 0f || IsReleasedFrom(entity, occupant))
                {
                    return;
                }
                AircraftNavigation navigation = m_NavigationData[entity];
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], stopDistance, kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
                if (!told)
                {
                    Say(entity, AtcMessageKind.HoldForGate, Entity.Null, default);
                }
            }

            /// <summary>The airplane's next stop is a gate whose stand its route reaches within the look-ahead (taxiing in here).</summary>
            private bool IsTaxiingToGate(Entity entity)
            {
                if (!m_TargetData.TryGetComponent(entity, out Game.Common.Target target)
                    || !m_RouteLaneData.TryGetComponent(target.m_Target, out Game.Routes.RouteLane stand))
                {
                    return false;
                }
                float distance = DistanceToLane(entity, stand.m_StartLane);
                return distance >= 0f && distance <= kGateLookAhead;
            }

            /// <summary>The airplane's next stop is a gate that another airplane is boarding at or still standing on.</summary>
            private bool IsGateOccupied(Entity entity, out Game.Routes.RouteLane stand, out Entity occupant)
            {
                stand = default;
                occupant = Entity.Null;
                if (!m_TargetData.TryGetComponent(entity, out Game.Common.Target target)
                    || !m_RouteLaneData.TryGetComponent(target.m_Target, out stand)
                    || !m_ConnectedData.TryGetComponent(target.m_Target, out Game.Routes.Connected connected)
                    || !m_BoardingVehicleData.TryGetComponent(connected.m_Connected, out Game.Routes.BoardingVehicle boarding))
                {
                    return false;
                }
                if (boarding.m_Vehicle != Entity.Null && boarding.m_Vehicle != entity)
                {
                    occupant = boarding.m_Vehicle;
                    return true;
                }
                return HasOtherObject(stand.m_StartLane, entity, out occupant)
                    || (stand.m_EndLane != stand.m_StartLane && HasOtherObject(stand.m_EndLane, entity, out occupant));
            }

            private bool HasOtherObject(Entity lane, Entity entity, out Entity other)
            {
                other = Entity.Null;
                if (!m_LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> laneObjects))
                {
                    return false;
                }
                for (int i = 0; i < laneObjects.Length; i++)
                {
                    if (laneObjects[i].m_LaneObject != entity)
                    {
                        other = laneObjects[i].m_LaneObject;
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Path distance from the airplane to the start of <paramref name="target"/>, or -1 if its route doesn't reach it.</summary>
            private float DistanceToLane(Entity entity, Entity target)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if (currentLane.m_Lane == target)
                {
                    return 0f;
                }
                float distance = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve current)
                    ? current.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x)
                    : 0f;
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AircraftNavigationLane lane = navigationLanes[i];
                    if (lane.m_Lane == target)
                    {
                        return distance;
                    }
                    if (m_CurveData.TryGetComponent(lane.m_Lane, out Curve curve))
                    {
                        distance += curve.m_Length * math.abs(lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int start = m_PathOwnerData[entity].m_ElementIndex;
                int end = math.min(path.Length, start + kMaxPathScan);
                for (int i = start; i < end; i++)
                {
                    PathElement element = path[i];
                    if (element.m_Target == target)
                    {
                        return distance;
                    }
                    if (m_CurveData.TryGetComponent(element.m_Target, out Curve curve))
                    {
                        distance += curve.m_Length * math.abs(element.m_TargetDelta.y - element.m_TargetDelta.x);
                    }
                }
                return -1f;
            }

            /// <summary>
            /// An airplane that went around is still on the runway lane in the game's state while it flies the pattern or holds,
            /// and the game then stops a departure (or a landed airplane) behind it as if it stood on the runway. An airborne
            /// airplane never blocks a ground one: the game's own IgnoreBlocker flag lets the next navigation update skip it,
            /// and as that flag only lasts one update, a blocked update keeps the speed trend of the last free one.
            /// </summary>
            private void PassAirborneBlocker(Entity entity)
            {
                AircraftNavigation navigation = m_NavigationData[entity];
                float speed = math.length(m_MovingData[entity].m_Velocity);
                // Airborne blockers are always passed; any blocker when the airplane was released from a deadlock.
                if (!m_BlockerData.TryGetComponent(entity, out Blocker blocker) || blocker.m_Blocker == Entity.Null
                    || !m_CurrentLaneData.TryGetComponent(blocker.m_Blocker, out AircraftCurrentLane blockerLane)
                    || ((blockerLane.m_LaneFlags & AircraftLaneFlags.Flying) == 0 && !IsReleasedFrom(entity, blocker.m_Blocker)))
                {
                    m_FreeSpeeds[entity] = new FreeSpeed { m_MaxSpeed = navigation.m_MaxSpeed, m_Speed = speed, m_Tick = m_Tick };
                    return;
                }
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                currentLane.m_LaneFlags |= AircraftLaneFlags.IgnoreBlocker;
                m_CurrentLaneData[entity] = currentLane;
                if (m_FreeSpeeds.TryGetValue(entity, out FreeSpeed free) && m_Tick - free.m_Tick <= 2)
                {
                    Bounds1 range = VehicleUtils.CalculateSpeedRange(m_PrefabAircraftData[m_PrefabRefData[entity].m_Prefab], speed, kTimeStep);
                    float trend = math.clamp(free.m_MaxSpeed + (free.m_MaxSpeed - free.m_Speed), range.min, range.max);
                    if (trend > navigation.m_MaxSpeed)
                    {
                        navigation.m_MaxSpeed = trend;
                        m_NavigationData[entity] = navigation;
                    }
                }
            }

            /// <summary>
            /// Deadlock detection: each ground airplane waits for at most one other: the airplane the mod's taxi hold keeps it
            /// behind, the airplane on its gate, or else the game's own blocker. Following those links back to the start is a
            /// cycle that won't clear by itself, so everyone on it is released at once for kDeadlockReleaseFrames and drives
            /// through the others on that cycle (only them), overlapping if it must. Runway hold-short is never released.
            /// </summary>
            private void DetectDeadlocks(NativeArray<FlightScan> scans)
            {
                NativeParallelHashMap<Entity, Entity> waitsFor = new NativeParallelHashMap<Entity, Entity>(math.max(8, scans.Length), Allocator.Temp);
                for (int i = 0; i < scans.Length; i++)
                {
                    if (scans[i].m_Flying)
                    {
                        continue;
                    }
                    Entity entity = scans[i].m_Entity;
                    Entity next = m_HeldBehind.TryGetValue(entity, out Entity ahead) ? ahead
                        : m_GateHolds.TryGetValue(entity, out Entity occupant) ? occupant
                        : m_BlockerData.TryGetComponent(entity, out Blocker blocker) && m_CurrentLaneData.HasComponent(blocker.m_Blocker) ? blocker.m_Blocker
                        : Entity.Null;
                    if (next != Entity.Null)
                    {
                        waitsFor.TryAdd(entity, next);
                    }
                }
                foreach (var pair in waitsFor)
                {
                    Entity start = pair.Key;
                    if (IsReleased(start))
                    {
                        continue;
                    }
                    Entity current = pair.Value;
                    for (int step = 0; step < kMaxDeadlockChain && current != start && waitsFor.TryGetValue(current, out Entity next); step++)
                    {
                        current = next;
                    }
                    if (current != start)
                    {
                        continue;
                    }
                    // Everyone on this cycle passes the others on it (only them: an airplane ahead in a queue still holds it).
                    // The cycle is named by its first airplane, which is not released yet, so no other active cycle shares it.
                    uint cycle = (uint)start.Index;
                    System.Text.StringBuilder chain = new System.Text.StringBuilder();
                    Entity node = start;
                    do
                    {
                        m_Released[node] = new DeadlockRelease { m_Until = m_Frame + kDeadlockReleaseFrames, m_Cycle = cycle };
                        chain.Append(node).Append(" -> ");
                        node = waitsFor[node];
                    }
                    while (node != start);
                    Mod.Log.Info($"deadlock: {chain}{start}; passing through");
                }
                waitsFor.Dispose();
            }

            /// <summary>Released from a deadlock: no taxi holds and no blockers until the release runs out.</summary>
            private bool IsReleased(Entity entity)
            {
                return !m_Released.IsEmpty && m_Released.TryGetValue(entity, out DeadlockRelease release) && (int)(release.m_Until - m_Frame) > 0;
            }

            /// <summary>Released from a deadlock together with <paramref name="other"/>: it may drive through that airplane.</summary>
            private bool IsReleasedFrom(Entity entity, Entity other)
            {
                return !m_Released.IsEmpty && m_Released.TryGetValue(entity, out DeadlockRelease release) && (int)(release.m_Until - m_Frame) > 0
                    && m_Released.TryGetValue(other, out DeadlockRelease otherRelease) && otherRelease.m_Cycle == release.m_Cycle;
            }

            private void EndReleaseTick()
            {
                if (m_Released.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> expired = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Released)
                {
                    if ((int)(pair.Value.m_Until - m_Frame) <= 0)
                    {
                        expired.Add(pair.Key);
                    }
                }
                for (int i = 0; i < expired.Length; i++)
                {
                    m_Released.Remove(expired[i]);
                }
                expired.Dispose();
            }

            private void EndFreeSpeedTick()
            {
                if (m_FreeSpeeds.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_FreeSpeeds)
                {
                    if (m_Tick - pair.Value.m_Tick > 2)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_FreeSpeeds.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// The mod's own taxi holds (spacing, giving way to a pushback) assume the airplane ahead moves on. One that has
            /// held an airplane behind the same other airplane for kTaxiHoldTimeout is dropped, and the game's own traffic
            /// logic (which lets stuck airplanes pass each other) takes over, so the mod can never keep a deadlock on the apron.
            /// </summary>
            private bool MayHoldBehind(Entity entity, Entity ahead)
            {
                if (IsReleasedFrom(entity, ahead))
                {
                    return false;
                }
                if (m_TaxiHolds.TryGetValue(entity, out TaxiHold hold) && hold.m_Ahead == ahead)
                {
                    hold.m_LastTick = m_Tick;
                    if (!hold.m_Released && m_Frame - hold.m_Since > kTaxiHoldTimeout)
                    {
                        hold.m_Released = true;
                        Mod.Log.Info($"{entity} held behind {ahead} for {(m_Frame - hold.m_Since) / 60} s; taxi hold released (the game takes over)");
                    }
                    m_TaxiHolds[entity] = hold;
                    return !hold.m_Released;
                }
                m_TaxiHolds[entity] = new TaxiHold { m_Ahead = ahead, m_Since = m_Frame, m_LastTick = m_Tick };
                return true;
            }

            /// <summary>Forgets taxi holds that didn't apply this tick (the airplane moved on, or the one ahead did).</summary>
            private void EndTaxiHoldTick()
            {
                if (m_TaxiHolds.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_TaxiHolds)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_TaxiHolds.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// On taxiways the game lets an airplane roll up almost to the tail of the one ahead. This keeps the configured
            /// gap between its nose and the nearest tail ahead on its path, the way real taxiing aircraft keep their distance.
            /// </summary>
            private void KeepTaxiSpacing(Entity entity)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                AircraftData aircraftData = m_PrefabAircraftData[prefab];
                AircraftNavigation navigation = m_NavigationData[entity];
                float nose = GetNoseLength(prefab);
                float spacing = m_Settings.m_TaxiSpacing;
                // Nothing beyond the point where the airplane could still stop with the gap to spare matters this tick.
                float limit = nose + spacing + VehicleUtils.GetBrakingDistance(aircraftData, navigation.m_MaxSpeed, kTimeStep) + kTaxiScanMargin;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance = 0f;
                float nearestTail = float.MaxValue;
                Entity ahead = Entity.Null;
                FindTailAhead(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, ref distance, ref nearestTail, ref ahead);
                if (nearestTail == float.MaxValue && distance < limit)
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length && distance < limit && nearestTail == float.MaxValue; i++)
                    {
                        FindTailAhead(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, ref distance, ref nearestTail, ref ahead);
                    }
                }
                if (nearestTail == float.MaxValue)
                {
                    return;
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(aircraftData, math.max(0f, nearestTail - nose - spacing), kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed && MayHoldBehind(entity, ahead))
                {
                    m_HeldBehind[entity] = ahead;
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Path distance to the tail of the nearest other object on the traversed part <paramref name="delta"/> of a lane,
            /// measured from the airplane's own position; then adds the lane part's length to <paramref name="distance"/>.
            /// </summary>
            private void FindTailAhead(Entity entity, Entity lane, float2 delta, ref float distance, ref float nearestTail, ref Entity ahead)
            {
                if (!m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return;
                }
                float span = delta.y - delta.x;
                if (m_LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> laneObjects))
                {
                    float direction = math.select(-1f, 1f, span >= 0f);
                    for (int i = 0; i < laneObjects.Length; i++)
                    {
                        LaneObject laneObject = laneObjects[i];
                        float along = (laneObject.m_CurvePosition.x - delta.x) * direction;
                        if (laneObject.m_LaneObject == entity || along <= 0f || along > math.abs(span))
                        {
                            continue;
                        }
                        // An airplane in the air (an arrival on approach is listed on the runway's lanes) is the tower's
                        // business, not taxi spacing: holding behind it kept a cleared departure from ever moving.
                        if (m_CurrentLaneData.TryGetComponent(laneObject.m_LaneObject, out AircraftCurrentLane otherLane)
                            && (otherLane.m_LaneFlags & AircraftLaneFlags.Flying) != 0)
                        {
                            continue;
                        }
                        float tail = 0f;
                        if (m_PrefabRefData.TryGetComponent(laneObject.m_LaneObject, out PrefabRef other)
                            && m_PrefabGeometryData.TryGetComponent(other.m_Prefab, out ObjectGeometryData geometry))
                        {
                            tail = math.max(0f, -geometry.m_Bounds.min.z);
                        }
                        float candidate = distance + along * curve.m_Length - tail;
                        if (candidate < nearestTail)
                        {
                            nearestTail = candidate;
                            ahead = laneObject.m_LaneObject;
                        }
                    }
                }
                distance += curve.m_Length * math.abs(span);
            }

            /// <summary>
            /// Line up and wait: lets the airplane onto the runway and stops it lined up on the first runway lane of its path,
            /// with its tail just inside it. A path that only crosses the runway has no such lane, so the airplane keeps
            /// holding short until its full clearance.
            /// </summary>
            private void HoldAtLineUpPoint(Entity entity, in FlightScan scan)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float tail = m_PrefabGeometryData.TryGetComponent(prefab, out ObjectGeometryData geometry) ? math.max(0f, -geometry.m_Bounds.min.z) : 0f;
                float target = tail + kLineUpInset;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance;
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Runway) != 0)
                {
                    // Already on the runway lane, which it entered at one of its ends.
                    float length = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve) ? curve.m_Length : 0f;
                    float start = math.select(1f, 0f, currentLane.m_CurvePosition.z >= currentLane.m_CurvePosition.x);
                    distance = target - math.abs(currentLane.m_CurvePosition.x - start) * length;
                }
                else
                {
                    distance = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve)
                        ? curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x)
                        : 0f;
                    bool found = false;
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length; i++)
                    {
                        AircraftNavigationLane lane = navigationLanes[i];
                        if ((lane.m_Flags & AircraftLaneFlags.Runway) != 0)
                        {
                            distance += target;
                            found = true;
                            break;
                        }
                        if (m_CurveData.TryGetComponent(lane.m_Lane, out Curve laneCurve))
                        {
                            distance += laneCurve.m_Length * math.abs(lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                        }
                    }
                    if (!found)
                    {
                        if (!scan.m_PathUnknown && scan.m_Occupied == Entity.Null)
                        {
                            HoldShort(entity, scan);
                        }
                        return;
                    }
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], math.max(0f, distance), kTimeStep);
                AircraftNavigation navigation = m_NavigationData[entity];
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>Highest speed from which the airplane still stops at its hold-short point, given its distance to the runway.</summary>
            private float GetHoldShortSpeed(Entity entity, float distance)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float stopDistance = math.max(0f, distance - GetNoseLength(prefab) - m_Settings.m_HoldShortSetback);
                return VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], stopDistance, kTimeStep);
            }

            /// <summary>A taxiing airplane can still stop at its hold-short point (with a little slack for rounding).</summary>
            private bool CanHoldShort(Entity entity, in Flight flight)
            {
                return math.length(m_MovingData[entity].m_Velocity) <= GetHoldShortSpeed(entity, flight.m_LastDistance) + kHoldShortSpeedSlack;
            }

            private void StandStill(Entity entity)
            {
                AircraftNavigation navigation = m_NavigationData[entity];
                navigation.m_MaxSpeed = 0f;
                m_NavigationData[entity] = navigation;
            }

            /// <summary>Lowers the speed so the airplane stops with its nose the setback distance before the runway.</summary>
            private void HoldShort(Entity entity, in FlightScan scan)
            {
                float maxSpeed = GetHoldShortSpeed(entity, scan.m_Distance);
                AircraftNavigation navigation = m_NavigationData[entity];
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Holds at present position: the fix is where the airplane is now, on its own path, and the inbound course
            /// follows that path. The game's own steering target and direction are kept, so the airplane can be handed
            /// back in exactly the state the game left it in. The game's lane state is never touched.
            /// </summary>
            private void StartHolding(Entity entity, ref Flight flight)
            {
                Transform transform = m_TransformData[entity];
                AircraftNavigation navigation = m_NavigationData[entity];
                float2 inbound = math.normalizesafe(navigation.m_TargetPosition.xz - transform.m_Position.xz,
                    math.normalizesafe(m_MovingData[entity].m_Velocity.xz, new float2(0f, 1f)));
                StartHolding(entity, ref flight, navigation.m_TargetPosition, navigation.m_TargetDirection, inbound, true);
            }

            /// <summary>
            /// Holds at present position, then (once cleared) flies to <paramref name="resumeTarget"/> lined up with
            /// <paramref name="resumeDirection"/> and hands the airplane back there.
            /// Arrival holds are <paramref name="stacked"/> 1,000 ft apart per runway. A hold at the end of the downwind
            /// isn't: it stays at pattern altitude near the airport, apart from the arrival holds (stacking it on top of
            /// them sent the airplane climbing thousands of feet).
            /// </summary>
            private void StartHolding(Entity entity, ref Flight flight, float3 resumeTarget, float3 resumeDirection, float2 inbound, bool stacked)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                flight.m_ResumeTarget = resumeTarget;
                flight.m_ResumeDirection = resumeDirection;
                flight.m_StackLevel = stacked ? TakeStackLevel(flight.m_Runway) : kNoStackLevel;
                float3 fix = m_TransformData[entity].m_Position;
                if (stacked)
                {
                    // Holds of different airports are separated by altitude: each class has its own band above its runway, B highest
                    // (phase 4-3); without airspace data, the stack starts where the airplane is.
                    fix.y = m_Runways.TryGetValue(flight.m_Runway, out RunwayState runwayState) && m_Airspaces.TryGetValue(runwayState.m_Airport, out AirportSpace space)
                        ? flight.m_EntryPosition.y + space.HoldBase + flight.m_StackLevel * kStackSpacing
                        : fix.y + flight.m_StackLevel * kStackSpacing;
                    if (!flight.m_Told)
                    {
                        // Not when the reason was just given (closure): that transmission already said to hold.
                        flight.m_Told = true;
                        Say(entity, AtcMessageKind.HoldAtPresentPosition, flight, (int)math.round((fix.y - flight.m_EntryPosition.y) / (100f * 0.3048f)) * 100);
                    }
                }
                flight.m_Pattern = new HoldingPattern(fix, inbound, m_Settings.m_HoldingLegLength, GetTurnRadius(airplaneData));
                flight.m_Holding = true;
                m_Flights[entity] = flight;
            }

            /// <summary>Turn radius at holding speed: the move system turns with radius speed / m_FlyingTurning, plus a margin.</summary>
            private static float GetTurnRadius(AirplaneData airplaneData)
            {
                return GetHoldingSpeed(airplaneData) / math.max(airplaneData.m_FlyingTurning, 0.001f) * 1.1f;
            }

            // ------------------------------------------------------------ go-around and traffic pattern

            /// <summary>
            /// Breaks off the approach: the runway is released and the airplane asks again, keeping its place in time.
            /// It climbs straight ahead and flies the traffic pattern built from its own runway: upwind past the runway
            /// end, crosswind, downwind abeam the runway to the base turn. Left-hand by default; on the other side when a
            /// parallel runway lies on the pattern side.
            /// </summary>
            private void GoAround(Entity entity, ref Flight flight, string reason, bool pilotInitiated)
            {
                // Close to the threshold the game has already moved the approach on from "approaching" to "landing", and in
                // that state it treats being below the steering target as touchdown and drops the flying state, so the
                // climbing airplane would turn into a ground vehicle in the air. Put the approach state it had back.
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Landing) != 0 && (currentLane.m_LaneFlags & AircraftLaneFlags.Approaching) == 0)
                {
                    currentLane.m_LaneFlags |= AircraftLaneFlags.Approaching;
                    m_CurrentLaneData[entity] = currentLane;
                }

                ReleaseRunway(entity, flight.m_Runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_SeenOnRunway = false;
                flight.m_TouchedDown = false;
                flight.m_WentAround = true;
                flight.m_ToldLanding = false;
                flight.m_WeatherChecked = false;

                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float radius = GetTurnRadius(airplaneData);
                float3 threshold = flight.m_EntryPosition;
                float2 landing = flight.m_EntryDirection;
                float2 side = IsRightHandPattern(entity, flight.m_Runway, landing) ? MathUtils.Right(landing) : MathUtils.Left(landing);
                float width = math.max(m_Settings.m_PatternWidth, radius * 2.2f);

                // Upwind ends past the runway end (and past the airplane), leaving room for the crosswind turn.
                float upwind = math.dot(m_TransformData[entity].m_Position.xz - threshold.xz, landing);
                if (m_RunwayGeometry.TryGetValue(flight.m_Runway, out RunwayGeometry geometry))
                {
                    float2 a = geometry.m_Origin + geometry.m_Direction * geometry.m_Min;
                    float2 b = geometry.m_Origin + geometry.m_Direction * geometry.m_Max;
                    upwind = math.max(upwind, math.max(math.dot(a - threshold.xz, landing), math.dot(b - threshold.xz, landing)));
                }
                upwind += math.max(kUpwindExtension, radius);
                float final = math.max(m_Settings.m_FinalLength, radius * 2f);
                float altitude = threshold.y + kPatternHeight;

                flight.m_UpwindEnd = new float3(threshold.x + landing.x * upwind, altitude, threshold.z + landing.y * upwind);
                flight.m_CrosswindEnd = flight.m_UpwindEnd + new float3(side.x, 0f, side.y) * width;
                flight.m_DownwindEnd = new float3(threshold.x - landing.x * final + side.x * width, altitude, threshold.z - landing.y * final + side.y * width);
                flight.m_CircuitLeg = CircuitLeg.Upwind;
                flight.m_InCircuit = true;
                m_Flights[entity] = flight;
                LogEvent(entity, $"went around ({reason}), {(math.dot(side, MathUtils.Right(landing)) > 0f ? "right" : "left")}-hand pattern for runway", flight.m_Runway);
                Say(entity, pilotInitiated ? AtcMessageKind.GoingAround : AtcMessageKind.GoAround, flight,
                    math.dot(side, MathUtils.Right(landing)) > 0f ? 1 : 0, ClosedBy(flight.m_Runway));
            }

            /// <summary>Flies the pattern legs; at the end of downwind the base turn and final are flown by the hold/return logic.</summary>
            private void FlyCircuit(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                float radius = GetTurnRadius(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab]);
                // Turn onto the next leg a little before each corner, as a fly-by waypoint.
                float lead = radius * kCircuitLeadFactor;
                bool cleared = flight.m_Clearance == Clearance.Cleared && m_Frame - flight.m_ClearedFrame >= kResponseFrames;
                if (flight.m_CircuitLeg == CircuitLeg.ExtendedDownwind && cleared)
                {
                    // Cleared on the extension: turn base from here.
                    EndCircuit(entity, ref flight);
                    return;
                }
                float3 waypoint = flight.m_CircuitLeg == CircuitLeg.Upwind ? flight.m_UpwindEnd
                    : flight.m_CircuitLeg == CircuitLeg.Crosswind ? flight.m_CrosswindEnd
                    : flight.m_CircuitLeg == CircuitLeg.Downwind ? flight.m_DownwindEnd
                    : GetDownwindExtensionEnd(flight);
                if (math.distancesq(position.xz, waypoint.xz) < lead * lead)
                {
                    if (flight.m_CircuitLeg < CircuitLeg.Downwind)
                    {
                        flight.m_CircuitLeg++;
                        m_Flights[entity] = flight;
                        FlyCircuit(entity, ref flight);
                        return;
                    }
                    if (flight.m_CircuitLeg == CircuitLeg.Downwind && !cleared)
                    {
                        // Not cleared yet: keep flying downwind instead of turning base.
                        flight.m_CircuitLeg = CircuitLeg.ExtendedDownwind;
                        m_Flights[entity] = flight;
                        Say(entity, AtcMessageKind.ExtendDownwind, flight);
                        FlyCircuit(entity, ref flight);
                        return;
                    }
                    // Cleared at the end of downwind, or the extension is used up (then the tower holds it).
                    EndCircuit(entity, ref flight);
                    return;
                }
                Steer(entity, waypoint, default);
                m_Flights[entity] = flight;
            }

            /// <summary>Where the extended downwind ends: kDownwindExtension beyond the normal end, at pattern altitude.</summary>
            private static float3 GetDownwindExtensionEnd(in Flight flight)
            {
                float3 direction = flight.m_DownwindEnd - flight.m_CrosswindEnd;
                direction.y = 0f;
                return flight.m_DownwindEnd + math.normalizesafe(direction) * kDownwindExtension;
            }

            /// <summary>
            /// End of downwind (or of its extension): the final approach starts the final length out on the extended centerline,
            /// at the glide path height there, lined up with the runway. A cleared airplane turns base and joins it at once; one
            /// that still isn't at the end of the extension is told to hold, and holds right there until it is (the same
            /// hold-and-return used for arrivals).
            /// </summary>
            private void EndCircuit(Entity entity, ref Flight flight)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float3 threshold = flight.m_EntryPosition;
                float2 landing = flight.m_EntryDirection;
                float final = math.max(m_Settings.m_FinalLength, GetTurnRadius(airplaneData) * 2f);
                float3 finalStart = new float3(threshold.x - landing.x * final, 0f, threshold.z - landing.y * final);
                finalStart.y = math.min(flight.m_DownwindEnd.y, threshold.y + final * math.tan(airplaneData.m_ClimbAngle));

                flight.m_InCircuit = false;
                float2 downwind = math.normalizesafe(flight.m_DownwindEnd.xz - flight.m_CrosswindEnd.xz, -landing);
                StartHolding(entity, ref flight, finalStart, new float3(landing.x, 0f, landing.y), downwind, false);
                if (flight.m_Clearance == Clearance.Cleared)
                {
                    ReturnToPath(entity, ref flight);
                }
                else
                {
                    Say(entity, AtcMessageKind.HoldInPattern, flight);
                    Hold(entity, ref flight);
                }
            }

            /// <summary>
            /// The runway end's pattern direction, set in the airport info panel and stored on the runway's edges: read from
            /// the edge the airplane is over (or the runway's key edge) for the end it lands on.
            /// </summary>
            private bool IsRightHandPattern(Entity entity, Entity runway, float2 landing)
            {
                Entity edge = m_OwnerData.TryGetComponent(m_CurrentLaneData[entity].m_Lane, out Owner owner) ? owner.m_Owner : Entity.Null;
                if (!m_RunwayPatternData.HasComponent(edge))
                {
                    edge = runway;
                }
                if (m_RunwayPatternData.TryGetComponent(edge, out RunwayPattern pattern) && m_CurveData.TryGetComponent(edge, out Curve curve)
                    && pattern.TryGetRightHand(curve.m_Bezier.d.xz - curve.m_Bezier.a.xz, landing, out bool rightHand))
                {
                    return rightHand;
                }
                // Not set in the panel: right-hand when a parallel runway lies to the left, so the pattern stays outside; with no
                // parallel runway, away from a neighbouring airport on the left (phase 4-3).
                if (HasParallelRunwayOnSide(runway, landing, MathUtils.Left(landing)))
                {
                    return true;
                }
                if (HasParallelRunwayOnSide(runway, landing, MathUtils.Right(landing)))
                {
                    return false;
                }
                return m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry geometry) && m_Runways.TryGetValue(runway, out RunwayState state)
                    && AirspaceRules.IsNeighbourOnLeft(m_Airspaces, state.m_Airport, geometry.Middle, landing);
            }

            /// <summary>Whether another runway runs parallel within the pattern width on the given side of this one.</summary>
            private bool HasParallelRunwayOnSide(Entity runway, float2 landing, float2 side)
            {
                if (!m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry ours))
                {
                    return false;
                }
                foreach (var pair in m_RunwayGeometry)
                {
                    if (pair.Key != runway && RunwayGeometry.IsParallelOnSide(ours, pair.Value, landing, side, m_Settings.m_PatternWidth))
                    {
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Flies the holding pattern instead of the path the game planned.</summary>
            private void Hold(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                Steer(entity, flight.m_Pattern.GetSteeringTarget(position, flight.m_Pattern.m_TurnRadius + GetSpeed(entity) * 2f), default);
            }

            /// <summary>
            /// After clearance, flies back to the point where the game's path was frozen and lines up with it, then hands the
            /// airplane back with the game's own target and direction. From there the game's approach logic (descent and
            /// slowing down) runs exactly as for an airplane that never held.
            /// </summary>
            private void ReturnToPath(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                float3 resume = flight.m_ResumeTarget;
                float radius = flight.m_Pattern.m_TurnRadius;
                float2 toResume = resume.xz - position.xz;
                float distance = math.length(toResume);
                float2 heading = math.normalizesafe(m_MovingData[entity].m_Velocity.xz);

                // Hand back once flying towards the point from far enough out that it can't end up inside the turn circle.
                if (distance > radius * kHandBackRadiusFactor && math.dot(heading, toResume / math.max(distance, 1f)) > kHandBackAlignment)
                {
                    FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                    flight.m_Holding = false;
                    m_Flights[entity] = flight;
                    Steer(entity, resume, flight.m_ResumeDirection);
                    return;
                }

                // Otherwise come round to a gate behind the point, on the path's own course, so the airplane arrives lined up.
                float2 course = math.lengthsq(flight.m_ResumeDirection.xz) > 0.5f
                    ? math.normalize(flight.m_ResumeDirection.xz)
                    : flight.m_Pattern.m_Inbound;
                float2 gate = resume.xz - course * (radius * kGateRadiusFactor);
                float3 target = math.distance(position.xz, gate) > radius ? new float3(gate.x, resume.y, gate.y) : resume;
                Steer(entity, target, default);
            }

            /// <summary>Sets the steering target and a speed the airplane can actually reach this tick.</summary>
            private void Steer(Entity entity, float3 target, float3 direction)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float3 position = m_TransformData[entity].m_Position;
                // The move system sets the velocity to exactly m_MaxSpeed, so it must stay inside the reachable range.
                float maxSpeed = MathUtils.Clamp(GetHoldingSpeed(airplaneData), VehicleUtils.CalculateSpeedRange(airplaneData, GetSpeed(entity), kTimeStep));
                AircraftNavigation navigation = m_NavigationData[entity];
                navigation.m_TargetPosition = target;
                navigation.m_TargetDirection = direction;
                navigation.m_MaxSpeed = maxSpeed;
                // Same climb rule the game uses for airplanes that aren't landing.
                navigation.m_MinClimbAngle = math.atan((target.y - position.y) * 0.02f) * airplaneData.m_ClimbAngle / (math.PI * 0.5f);
                m_NavigationData[entity] = navigation;
            }

            private float GetSpeed(Entity entity)
            {
                return math.length(m_MovingData[entity].m_Velocity);
            }

            /// <summary>
            /// Speed for holding and the traffic pattern. The move system keeps the nose at least at
            /// lerp(-climb angle, slow pitch angle, (1 - f)^2), where f is how far the speed is between the minimum and maximum
            /// flying speed: near the minimum the airplane flies nose-up like in the landing flare. So fly at least fast
            /// enough for that minimum pitch to be level or below.
            /// </summary>
            private static float GetHoldingSpeed(AirplaneData airplaneData)
            {
                float2 range = airplaneData.m_FlyingSpeed;
                float climb = math.max(airplaneData.m_ClimbAngle, 0f);
                float slow = math.max(airplaneData.m_SlowPitchAngle, 0f);
                float level = climb + slow > 0.0001f ? 1f - math.sqrt(climb / (climb + slow)) : 0f;
                float levelSpeed = math.lerp(range.x, range.y, math.saturate(level + kLevelAttitudeMargin));
                return math.min(math.max(range.x * kHoldingSpeedFactor, levelSpeed), range.y);
            }

            /// <summary>One roll per approach: the same airplane on the same frame always rolls the same, so the result is reproducible.</summary>
            private bool RollWeatherGoAround(Entity entity)
            {
                Unity.Mathematics.Random random = new Unity.Mathematics.Random(math.hash(new uint2((uint)entity.Index, m_Frame)) | 1u);
                return random.NextFloat() < m_Settings.m_WeatherGoAroundChance;
            }

            /// <summary>At the decision height: still in the air, below 100 ft above the runway, on short final.</summary>
            private bool IsBelowDecisionHeight(Entity entity, in Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                return position.y - flight.m_EntryPosition.y < kDecisionHeight
                    && math.distancesq(position.xz, flight.m_EntryPosition.xz) < kDecisionDistance * kDecisionDistance;
            }

            private byte TakeStackLevel(Entity runway)
            {
                RunwayState state = m_Runways[runway];
                int level = math.min(math.tzcnt(~state.m_StackMask), kMaxStackLevel);
                state.m_StackMask |= 1ul << level;
                m_Runways[runway] = state;
                return (byte)level;
            }

            private void FreeStackLevel(Entity runway, byte level)
            {
                if (level != kNoStackLevel && m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    state.m_StackMask &= ~(1ul << level);
                    m_Runways[runway] = state;
                }
            }
        }
    }
}
