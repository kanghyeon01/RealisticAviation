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

        private SimulationSystem m_SimulationSystem;
        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_AirplanePrefabQuery;
        private EntityQuery m_AirplaneQuery;
        private EntityQuery m_RunwayEdgeQuery;
        private EntityQuery m_TaxiwayVersionQuery;
        private EntityQuery m_AirplaneStopQuery;
        private EntityQuery m_AirportVersionQuery;

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

        /// <summary>Lets <see cref="AtcPreNavigationSystem"/>'s job finish before the next tower job rewrites the list.</summary>
        internal void AddHoldingFlightsReader(JobHandle reader)
        {
            m_JobHandle = JobHandle.CombineDependencies(m_JobHandle, reader);
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
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

            m_RunwayOf = new NativeParallelHashMap<Entity, Entity>(64, Allocator.Persistent);
            m_Runways = new NativeParallelHashMap<Entity, RunwayState>(16, Allocator.Persistent);
            m_RunwayGeometry = new NativeParallelHashMap<Entity, RunwayGeometry>(16, Allocator.Persistent);
            m_Flights = new NativeParallelHashMap<Entity, Flight>(64, Allocator.Persistent);
            m_HoldingFlights = new NativeList<Entity>(16, Allocator.Persistent);
            m_Pushbacks = new NativeParallelHashMap<Entity, Pushback>(16, Allocator.Persistent);
            m_Sequence = new NativeReference<uint>(0, Allocator.Persistent);
            m_PrefabNames = new NativeParallelHashMap<Entity, FixedString128Bytes>(64, Allocator.Persistent);

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
            m_Pushbacks.Clear();
            m_TaxiwayVersion = -1;
            m_AirportVersion = -1;
            m_RunwaySignature = 0;
        }

        protected override void OnUpdate()
        {
            RefreshRunways();
            RefreshPrefabNames();
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

            NativeList<Entity> airplanes = m_AirplaneQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle airplanesHandle);
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
                m_Airplanes = airplanes,
                m_RunwayOf = m_RunwayOf,
                m_Runways = m_Runways,
                m_RunwayGeometry = m_RunwayGeometry,
                m_Flights = m_Flights,
                m_HoldingFlights = m_HoldingFlights,
                m_Pushbacks = m_Pushbacks,
                m_Sequence = m_Sequence,
                m_PrefabNames = m_PrefabNames,
                m_Settings = new AtcSettings(Mod.Setting),
                m_Tick = m_Tick,
                m_Frame = m_SimulationSystem.frameIndex,
            };
            JobHandle handle = job.Schedule(JobHandle.CombineDependencies(Dependency, airplanesHandle, m_JobHandle));
            airplanes.Dispose(handle);
            m_JobHandle = handle;
            Dependency = handle;
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
            private const float kGroundRequestMargin = 50f;

            // Traffic pattern: 1,500 ft above the runway (FAA pattern altitude for large and turbine airplanes); upwind runs at
            // least this far past the runway end; legs turn this many turn radii before each corner. (Parallel runways: see
            // RunwayGeometry.IsParallelOnSide.) The practice go-around happens 100 ft above
            // the runway (within 2 NM of the threshold), where a real go-around decision is made.
            private const float kPatternHeight = 1500f * 0.3048f;
            private const float kUpwindExtension = 0.5f * Setting.kMetersPerNauticalMile;
            private const float kCircuitLeadFactor = 1.5f;
            private const float kPracticeGoAroundHeight = 100f * 0.3048f;
            private const float kPracticeGoAroundDistance = 2f * Setting.kMetersPerNauticalMile;

            // Response-ratio scheduling: expected runway time of each kind of use, in game seconds.
            private const float kSecondsPerFrame = 1f / 60f;
            private const float kLandingRunwaySeconds = 30f;
            private const float kTakeoffRunwaySeconds = 30f;
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

            // Taxi spacing looks this much further than the gap plus the stopping distance.
            private const float kTaxiScanMargin = 20f;

            // A ground airplane that was cleared but hasn't reached the runway in this many frames loses its turn
            // and goes to the back of the line, so one stuck airplane can't block a runway forever.
            private const uint kGroundClearanceTimeout = 60 * 60 * 3;

            // The same safety net for arrivals; a cleared arrival normally reaches the runway well within this.
            private const uint kAirClearanceTimeout = 60 * 60 * 5;

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

            [ReadOnly] public NativeList<Entity> m_Airplanes;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity> m_RunwayOf;
            public NativeParallelHashMap<Entity, RunwayState> m_Runways;
            [ReadOnly] public NativeParallelHashMap<Entity, RunwayGeometry> m_RunwayGeometry;
            public NativeParallelHashMap<Entity, Flight> m_Flights;
            public NativeList<Entity> m_HoldingFlights;
            public NativeParallelHashMap<Entity, Pushback> m_Pushbacks;
            public NativeReference<uint> m_Sequence;
            [ReadOnly] public NativeParallelHashMap<Entity, FixedString128Bytes> m_PrefabNames;

            public AtcSettings m_Settings;
            public uint m_Tick;
            public uint m_Frame;

            public void Execute()
            {
                m_HoldingFlights.Clear();
                if (m_Runways.IsEmpty)
                {
                    m_Flights.Clear();
                    return;
                }
                try
                {
                    NativeArray<FlightScan> scans = new NativeArray<FlightScan>(m_Airplanes.Length, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    int touched = 0;
                    for (int i = 0; i < m_Airplanes.Length; i++)
                    {
                        FlightScan scan = Scan(m_Airplanes[i]);
                        if (UpdateFlight(ref scan))
                        {
                            touched++;
                        }
                        scans[i] = scan;
                    }
                    if (touched != m_Flights.Count())
                    {
                        RemoveStaleFlights();
                    }
                    GrantRunways();
                    NativeList<PushbackArea> pushbackAreas = CollectPushbackAreas();
                    for (int i = 0; i < scans.Length; i++)
                    {
                        Apply(scans[i], pushbackAreas);
                    }
                    pushbackAreas.Dispose();
                    scans.Dispose();
                    EndPushbackTick();
                }
                catch (Exception e)
                {
                    m_HoldingFlights.Clear();
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
                    scan.m_RequestDistance = m_Settings.m_ContactDistance;
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
                    float2 offset = m_TransformData[entity].m_Position.xz - flight.m_EntryPosition.xz;
                    flight.m_LastDistance = math.length(offset);
                    flight.m_LastEta = flight.m_LastDistance / math.max(math.length(m_MovingData[entity].m_Velocity), 1f);
                    m_Flights[entity] = flight;
                    return true;
                }

                if (scan.m_Occupied != Entity.Null && m_Runways.TryGetValue(scan.m_Occupied, out RunwayState occupied))
                {
                    if (occupied.m_OccupiedTick != m_Tick)
                    {
                        occupied.m_OccupiedTick = m_Tick;
                        occupied.m_Occupants = 0;
                    }
                    occupied.m_Occupants++;
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
                            if (!flight.m_TouchedDown && flight.m_LastFlying == false && flight.m_SeenOnRunway
                                && m_Runways.TryGetValue(flight.m_Runway, out RunwayState runway) && runway.m_Arrival == entity)
                            {
                                flight.m_TouchedDown = true;
                                if (runway.m_Holder != Entity.Null)
                                {
                                    // Measures how well the departure gap works; a go-around will handle this case later.
                                    Mod.Log.Info($"{entity} touched down on runway {flight.m_Runway} while {runway.m_Holder} (cleared ahead of it) still had it");
                                }
                            }
                            flight.m_SeenOnRunway = true;
                            flight.m_OnRunwayTick = m_Tick;
                            flight.m_LastDistance = 0f;
                            flight.m_LastEta = 0f;
                        }
                        else if (flight.m_SeenOnRunway || scan.m_Upcoming != flight.m_Runway)
                        {
                            // Used the runway and left it, or no longer heading for it. It may ask for the next one below.
                            LogEvent(entity, flight.m_SeenOnRunway ? "vacated runway" : "no longer heading for runway", flight.m_Runway);
                            DropFlight(entity, flight);
                            return TryRequest(ref scan);
                        }
                        else
                        {
                            flight.m_LastDistance = scan.m_Distance;
                            flight.m_LastEta = scan.m_Flying ? EstimateArrivalSeconds(entity, scan) : 0f;
                            flight.m_EntryPosition = scan.m_EntryPosition;
                            flight.m_EntryDirection = scan.m_EntryDirection;
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
                    m_Flights[entity] = flight;
                    return true;
                }
                return TryRequest(ref scan);
            }

            private bool TryRequest(ref FlightScan scan)
            {
                if (scan.m_PathUnknown || scan.m_Upcoming == Entity.Null || scan.m_Distance > scan.m_RequestDistance)
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
                };
                m_Flights.Add(scan.m_Entity, flight);
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

            private void LogLongWait(Entity entity, Flight flight)
            {
                m_Runways.TryGetValue(flight.m_Runway, out RunwayState state);
                int occupants = state.m_OccupiedTick == m_Tick ? state.m_Occupants : 0;
                Mod.Log.Info($"{entity} waiting {(m_Frame - flight.m_RequestFrame) / 60} s for runway {flight.m_Runway} " +
                    $"({Describe(flight)}, #{flight.m_Sequence}); ground: {DescribeCleared(state.m_Holder)}; " +
                    $"arrival: {DescribeCleared(state.m_Arrival)}; lined up: {DescribeCleared(state.m_LineUp)}; airplanes on runway: {occupants}");
            }

            private string DescribeCleared(Entity entity)
            {
                if (entity == Entity.Null || !m_Flights.TryGetValue(entity, out Flight flight))
                {
                    return "none";
                }
                return $"{entity} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, on runway: {flight.m_SeenOnRunway}, " +
                    $"cleared {(m_Frame - flight.m_ClearedFrame) / 60} s ago, holding: {flight.m_Holding})";
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
                    && (state.m_Holder == entity || state.m_Arrival == entity || state.m_LineUp == entity))
                {
                    if (state.m_LineUp == entity)
                    {
                        state.m_LineUp = Entity.Null;
                    }
                    if (state.m_Holder == entity)
                    {
                        state.m_Holder = Entity.Null;
                    }
                    if (state.m_Arrival == entity)
                    {
                        state.m_Arrival = Entity.Null;
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

                NativeParallelHashMap<Entity, Candidates> next = new NativeParallelHashMap<Entity, Candidates>(math.max(8, m_Runways.Count()), Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    Flight flight = pair.Value;
                    if (flight.m_Clearance != Clearance.Waiting
                        || !m_Runways.TryGetValue(flight.m_Runway, out RunwayState state)
                        || !(state.IsFree(m_Tick) || CanLineUp(state)))
                    {
                        continue;
                    }
                    next.TryGetValue(flight.m_Runway, out Candidates candidates);
                    if (flight.m_LastFlying)
                    {
                        float ratio = ResponseRatio(WaitedSeconds(flight.m_RequestFrame), flight.m_LastEta + kLandingRunwaySeconds);
                        if (candidates.m_Air == Entity.Null || ratio > candidates.m_AirRatio)
                        {
                            candidates.m_Air = pair.Key;
                            candidates.m_AirRatio = ratio;
                        }
                    }
                    else
                    {
                        if (candidates.m_Ground == Entity.Null || (int)(flight.m_RequestFrame - candidates.m_OldestGroundRequestFrame) < 0)
                        {
                            candidates.m_OldestGroundRequestFrame = flight.m_RequestFrame;
                        }
                        if (candidates.m_Ground == Entity.Null || flight.m_LastDistance < candidates.m_GroundDistance)
                        {
                            candidates.m_Ground = pair.Key;
                            candidates.m_GroundDistance = flight.m_LastDistance;
                        }
                    }
                    next[flight.m_Runway] = candidates;
                }

                foreach (var pair in next)
                {
                    Entity runway = pair.Key;
                    Candidates candidates = pair.Value;
                    RunwayState state = m_Runways[runway];
                    if (!state.IsFree(m_Tick))
                    {
                        // Busy, but this airport uses line up and wait: the head of the ground queue may line up
                        // behind traffic that has already passed its entry point.
                        if (candidates.m_Ground != Entity.Null && MayLineUpBehindTraffic(state, m_Flights[candidates.m_Ground], out Entity traffic))
                        {
                            state.m_LineUp = candidates.m_Ground;
                            Flight flight = m_Flights[candidates.m_Ground];
                            flight.m_Clearance = Clearance.LineUp;
                            m_Flights[candidates.m_Ground] = flight;
                            LogEvent(candidates.m_Ground, $"cleared to line up and wait (behind {traffic}, waited {(m_Frame - flight.m_RequestFrame) / 60} s) on runway", runway);
                            m_Runways[runway] = state;
                        }
                        continue;
                    }
                    float groundRatio = candidates.m_Ground == Entity.Null ? 0f : ResponseRatio(
                        WaitedSeconds(candidates.m_OldestGroundRequestFrame),
                        candidates.m_GroundDistance / kTaxiSpeedEstimate + kTakeoffRunwaySeconds);
                    bool groundFirst = candidates.m_Ground != Entity.Null
                        && (candidates.m_Air == Entity.Null || groundRatio > candidates.m_AirRatio);

                    // The next arrival is cleared when it is first in line, or when it is far enough out
                    // that the ground airplane going now will be gone before it arrives.
                    if (state.m_Arrival == Entity.Null && candidates.m_Air != Entity.Null
                        && (!groundFirst || m_Flights[candidates.m_Air].m_LastEta > gap))
                    {
                        state.m_Arrival = candidates.m_Air;
                        Clear(candidates.m_Air, runway, "to land", candidates.m_AirRatio);
                    }
                    if (candidates.m_Ground != Entity.Null
                        && (state.m_Arrival == Entity.Null || m_Flights[state.m_Arrival].m_LastEta > gap))
                    {
                        state.m_Holder = candidates.m_Ground;
                        Clear(candidates.m_Ground, runway, state.m_Arrival == Entity.Null ? "onto" : "ahead of the arrival onto", groundRatio);
                    }
                    m_Runways[runway] = state;
                }
                next.Dispose();
            }

            /// <summary>The runway's airport uses line up and wait, and nobody is lined up on it yet.</summary>
            private bool CanLineUp(in RunwayState state)
            {
                return state.m_LineUp == Entity.Null && state.m_Airport != Entity.Null && m_LineUpAirportData.HasComponent(state.m_Airport);
            }

            /// <summary>
            /// "Behind the landing / departing traffic, line up and wait": the only airplane on the runway is either the cleared
            /// arrival after touchdown, or the departure on its takeoff roll with no arrival cleared behind it; and it is already
            /// well past the point where the waiting airplane would enter, rolling away from it.
            /// </summary>
            private bool MayLineUpBehindTraffic(in RunwayState state, in Flight candidate, out Entity traffic)
            {
                traffic = Entity.Null;
                if (!CanLineUp(state) || state.Occupants(m_Tick) != 1 || math.lengthsq(candidate.m_EntryDirection) < 0.5f)
                {
                    return false;
                }
                if (state.m_Holder != Entity.Null)
                {
                    // A departure ahead: only when no arrival is cleared to land behind it, which would meet the lined-up airplane.
                    if (state.m_Arrival != Entity.Null || !m_Flights.TryGetValue(state.m_Holder, out Flight departure)
                        || departure.m_OnRunwayTick != m_Tick)
                    {
                        return false;
                    }
                    traffic = state.m_Holder;
                }
                else if (state.m_Arrival != Entity.Null && m_Flights.TryGetValue(state.m_Arrival, out Flight arrival)
                    && arrival.m_TouchedDown && arrival.m_OnRunwayTick == m_Tick)
                {
                    traffic = state.m_Arrival;
                }
                else
                {
                    return false;
                }
                float2 direction = candidate.m_EntryDirection;
                float2 offset = m_TransformData[traffic].m_Position.xz - candidate.m_EntryPosition.xz;
                return math.dot(offset, direction) > kLineUpBehindDistance
                    && math.dot(m_MovingData[traffic].m_Velocity.xz, direction) >= 0f;
            }

            /// <summary>Gives the takeoff (or crossing) clearance to a lined-up airplane once the runway ahead of it is clear.</summary>
            private void ReleaseLinedUp(float gap)
            {
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    RunwayState state = m_Runways[runways[i]];
                    if (state.m_LineUp == Entity.Null || state.m_Holder != Entity.Null
                        || !m_Flights.TryGetValue(state.m_LineUp, out Flight flight))
                    {
                        continue;
                    }
                    int others = state.Occupants(m_Tick) - (flight.m_OnRunwayTick == m_Tick ? 1 : 0);
                    if (others != 0 || (state.m_Arrival != Entity.Null
                        && (!m_Flights.TryGetValue(state.m_Arrival, out Flight arrival) || arrival.m_LastEta <= gap)))
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
                m_Flights[entity] = flight;
                LogEvent(entity, $"cleared {what} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, waited {(m_Frame - flight.m_RequestFrame) / 60} s, ratio {ratio:F2}) runway", runway);
            }

            // ------------------------------------------------------------ steering

            private void Apply(in FlightScan scan, NativeList<PushbackArea> pushbackAreas)
            {
                Entity entity = scan.m_Entity;
                if (!scan.m_Flying && m_Settings.m_Pushback)
                {
                    UpdatePushback(entity);
                    if (pushbackAreas.Length != 0 && scan.m_Occupied == Entity.Null && !m_Pushbacks.ContainsKey(entity))
                    {
                        GiveWayToPushbacks(entity, pushbackAreas);
                    }
                }
                if (!scan.m_Flying && scan.m_Occupied == Entity.Null && m_Settings.m_TaxiSpacing > 0f)
                {
                    KeepTaxiSpacing(entity);
                }
                if (!m_Flights.TryGetValue(entity, out Flight flight))
                {
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
                    if (m_Settings.m_PracticeGoAround && scan.m_Flying && !flight.m_WentAround && flight.m_Clearance == Clearance.Cleared
                        && IsBelowGoAroundHeight(entity, flight))
                    {
                        GoAround(entity, ref flight, "practice");
                        FlyCircuit(entity, ref flight);
                        m_HoldingFlights.Add(entity);
                    }
                    return;
                }
                if (flight.m_Holding)
                {
                    if (flight.m_Clearance == Clearance.Cleared)
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
                    }
                }
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
                        if (IsPushbackClear(entity, pushback) || (m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame > kPushbackApprovalTimeout)
                        {
                            pushback.m_Phase = PushbackPhase.Pushing;
                            Mod.Log.Info($"{entity} pushback approved after {(m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame:F0} s");
                        }
                        else
                        {
                            navigation.m_MaxSpeed = 0f;
                        }
                        break;

                    case PushbackPhase.Pushing:
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
                if (!SamplePathForPushbacks(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, limit, areas, ref distance, ref hit))
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length; i++)
                    {
                        if (SamplePathForPushbacks(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, limit, areas, ref distance, ref hit))
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
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Walks the traversed part of one lane in steps and records the path distance of the first point inside a
            /// pushback area. Returns true when done: an area was hit or the look-ahead limit was passed.
            /// </summary>
            private bool SamplePathForPushbacks(Entity entity, Entity lane, float2 delta, float limit, NativeList<PushbackArea> areas, ref float distance, ref float hit)
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
                FindTailAhead(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, ref distance, ref nearestTail);
                if (nearestTail == float.MaxValue && distance < limit)
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length && distance < limit && nearestTail == float.MaxValue; i++)
                    {
                        FindTailAhead(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, ref distance, ref nearestTail);
                    }
                }
                if (nearestTail == float.MaxValue)
                {
                    return;
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(aircraftData, math.max(0f, nearestTail - nose - spacing), kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Path distance to the tail of the nearest other object on the traversed part <paramref name="delta"/> of a lane,
            /// measured from the airplane's own position; then adds the lane part's length to <paramref name="distance"/>.
            /// </summary>
            private void FindTailAhead(Entity entity, Entity lane, float2 delta, ref float distance, ref float nearestTail)
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
                        float tail = 0f;
                        if (m_PrefabRefData.TryGetComponent(laneObject.m_LaneObject, out PrefabRef other)
                            && m_PrefabGeometryData.TryGetComponent(other.m_Prefab, out ObjectGeometryData geometry))
                        {
                            tail = math.max(0f, -geometry.m_Bounds.min.z);
                        }
                        nearestTail = math.min(nearestTail, distance + along * curve.m_Length - tail);
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

            /// <summary>Lowers the speed so the airplane stops with its nose the setback distance before the runway.</summary>
            private void HoldShort(Entity entity, in FlightScan scan)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float stopDistance = math.max(0f, scan.m_Distance - GetNoseLength(prefab) - m_Settings.m_HoldShortSetback);
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], stopDistance, kTimeStep);
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
                StartHolding(entity, ref flight, navigation.m_TargetPosition, navigation.m_TargetDirection, inbound);
            }

            /// <summary>
            /// Holds at present position, then (once cleared) flies to <paramref name="resumeTarget"/> lined up with
            /// <paramref name="resumeDirection"/> and hands the airplane back there.
            /// </summary>
            private void StartHolding(Entity entity, ref Flight flight, float3 resumeTarget, float3 resumeDirection, float2 inbound)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                flight.m_ResumeTarget = resumeTarget;
                flight.m_ResumeDirection = resumeDirection;
                flight.m_StackLevel = TakeStackLevel(flight.m_Runway);
                float3 fix = m_TransformData[entity].m_Position;
                fix.y += flight.m_StackLevel * kStackSpacing;
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
            private void GoAround(Entity entity, ref Flight flight, string reason)
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
            }

            /// <summary>Flies the pattern legs; at the end of downwind the base turn and final are flown by the hold/return logic.</summary>
            private void FlyCircuit(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                float radius = GetTurnRadius(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab]);
                // Turn onto the next leg a little before each corner, as a fly-by waypoint.
                float lead = radius * kCircuitLeadFactor;
                float3 waypoint = flight.m_CircuitLeg == CircuitLeg.Upwind ? flight.m_UpwindEnd
                    : flight.m_CircuitLeg == CircuitLeg.Crosswind ? flight.m_CrosswindEnd
                    : flight.m_DownwindEnd;
                if (math.distancesq(position.xz, waypoint.xz) < lead * lead)
                {
                    if (flight.m_CircuitLeg != CircuitLeg.Downwind)
                    {
                        flight.m_CircuitLeg++;
                        m_Flights[entity] = flight;
                        FlyCircuit(entity, ref flight);
                        return;
                    }
                    EndCircuit(entity, ref flight);
                    return;
                }
                Steer(entity, waypoint, default);
                m_Flights[entity] = flight;
            }

            /// <summary>
            /// End of downwind: the final approach starts the final length out on the extended centerline, at the glide
            /// path height there, lined up with the runway. A cleared airplane turns base and joins it at once; one that
            /// isn't yet holds right here until it is (the same hold-and-return used for arrivals).
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
                StartHolding(entity, ref flight, finalStart, new float3(landing.x, 0f, landing.y), downwind);
                if (flight.m_Clearance == Clearance.Cleared)
                {
                    ReturnToPath(entity, ref flight);
                }
                else
                {
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
                // Not set in the panel: right-hand when a parallel runway lies to the left, so the pattern stays outside.
                return HasParallelRunwayOnSide(runway, landing, MathUtils.Left(landing));
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

            /// <summary>Practice go-around: still in the air, below 100 ft above the runway, on short final.</summary>
            private bool IsBelowGoAroundHeight(Entity entity, in Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                return position.y - flight.m_EntryPosition.y < kPracticeGoAroundHeight
                    && math.distancesq(position.xz, flight.m_EntryPosition.xz) < kPracticeGoAroundDistance * kPracticeGoAroundDistance;
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
                if (m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    state.m_StackMask &= ~(1ul << level);
                    m_Runways[runway] = state;
                }
            }
        }
    }
}
