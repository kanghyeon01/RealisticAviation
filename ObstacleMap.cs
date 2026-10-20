using System.Collections.Generic;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// Obstacles for the VFR chart infoview, by the rules of the FAA Chart Users' Guide:
    /// <list type="bullet">
    /// <item>buildings more than <see cref="kMinHeightFeet"/> above the ground, or more than <see cref="kMinUrbanHeightFeet"/>
    /// inside a built-up area (the yellow tint);</item>
    /// <item>the spire from <see cref="kTallFeet"/>, else the ^; light rays (high-intensity strobes) only above
    /// <see cref="kLightedFeet"/> (FAA AC 70/7460-1M allows medium-intensity lights up to 700 ft);</item>
    /// <item>taken highest first: an obstacle within <see cref="kSpacing"/> of one already shown is not drawn but makes that
    /// one a group, drawn with the group symbol and its top above sea level only (as charts do in congested areas);</item>
    /// <item>wind turbines (never lighted) within <see cref="kFarmSpacing"/> of each other form a cluster: one turbine is the
    /// single symbol, 2–4 the turbine group, 5 or more a wind farm, outlined by a dashed rounded hull with its highest
    /// elevation in a box.</item>
    /// </list>
    /// Found on a worker thread once each time the infoview opens.
    /// </summary>
    internal sealed class ObstacleMap
    {
        private const float kMinHeightFeet = 200f;
        private const float kMinUrbanHeightFeet = 299f;
        private const float kTallFeet = 1000f;
        private const float kLightedFeet = 700f;
        private const float kFeetPerMetre = 3.28084f;

        /// <summary>Least distance between two symbols shown, in metres: well over a symbol with its label.</summary>
        private const float kSpacing = 600f;

        /// <summary>Wind turbines this close to a turbine of a cluster belong to it (turbines stand some hundred metres apart).</summary>
        private const float kFarmSpacing = 600f;

        /// <summary>A cluster of at most this many turbines is a turbine group, a bigger one a wind farm.</summary>
        private const int kGroupTurbines = 4;

        /// <summary>Space between a wind farm's outermost turbines and its outline, in metres.</summary>
        private const float kFarmMargin = 150f;


        private enum Kind : byte
        {
            Obstacle,
            Group,
            Turbine,
            TurbineGroup,
            WindFarm,
        }

        private sealed class Obstacle
        {
            public float3 m_Base;
            public float m_Top;
            public float m_Height;
            public bool m_Turbine;
            public Kind m_Kind;

            /// <summary>A turbine cluster's turbines (the shown one first); null for other obstacles.</summary>
            public List<float2> m_Members;

            public float2 m_FarmCenter;
            public float2[] m_FarmOutline;

            // Top above sea level and "(height above the ground)", in feet.
            public string m_TopText;
            public string m_HeightText;
        }

        private struct FindJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buildings;
            [ReadOnly] public NativeList<Entity> m_NetObjects;
            [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transforms;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefs;
            [ReadOnly] public ComponentLookup<ObjectGeometryData> m_Geometries;
            [ReadOnly] public ComponentLookup<WindPoweredData> m_WindPowered;
            [ReadOnly] public ComponentLookup<Owner> m_Owners;
            [ReadOnly] public ComponentLookup<Game.Net.Edge> m_Edges;
            [ReadOnly] public ComponentLookup<Game.Net.Node> m_Nodes;
            [ReadOnly] public Game.Simulation.TerrainHeightData m_Terrain;
            [ReadOnly] public Game.Simulation.WaterSurfaceData<Game.Simulation.SurfaceWater> m_Water;

            /// <summary>Each obstacle: base (x, y, z) and height above the ground.</summary>
            public NativeList<float4> m_Found;

            /// <summary>For each obstacle, whether it is a wind turbine.</summary>
            public NativeList<bool> m_Turbines;

            /// <summary>How many of the obstacles found are buildings (the rest belong to roads and bridges).</summary>
            public NativeArray<int> m_BuildingCount;

            public void Execute()
            {
                m_Found.Clear();
                m_Turbines.Clear();
                float minHeight = kMinHeightFeet / kFeetPerMetre;
                for (int i = 0; i < m_Buildings.Length; i++)
                {
                    Entity building = m_Buildings[i];
                    Entity prefab = m_PrefabRefs[building].m_Prefab;
                    if (m_Geometries.TryGetComponent(prefab, out ObjectGeometryData geometry) && geometry.m_Bounds.max.y > minHeight)
                    {
                        m_Found.Add(new float4(m_Transforms[building].m_Position, geometry.m_Bounds.max.y));
                        m_Turbines.Add(m_WindPowered.HasComponent(prefab));
                    }
                }
                m_BuildingCount[0] = m_Found.Length;
                // Objects of roads and bridges (a bridge's towers and pillars): they stand on the ground, or in water, which is
                // what their height is measured from, as for an obstacle in the sea on a chart.
                for (int i = 0; i < m_NetObjects.Length; i++)
                {
                    Entity entity = m_NetObjects[i];
                    if (!m_Owners.TryGetComponent(entity, out Owner owner)
                        || (!m_Edges.HasComponent(owner.m_Owner) && !m_Nodes.HasComponent(owner.m_Owner))
                        || !m_Geometries.TryGetComponent(m_PrefabRefs[entity].m_Prefab, out ObjectGeometryData geometry))
                    {
                        continue;
                    }
                    float3 position = m_Transforms[entity].m_Position;
                    float ground = Game.Simulation.WaterUtils.SampleHeight(ref m_Water, ref m_Terrain, position);
                    float height = position.y + geometry.m_Bounds.max.y - ground;
                    if (height > minHeight)
                    {
                        m_Found.Add(new float4(position.x, ground, position.z, height));
                        m_Turbines.Add(false);
                    }
                }
            }
        }

        private NativeList<float4> m_Found;
        private NativeList<bool> m_Turbines;
        private NativeArray<int> m_BuildingCount;
        private NativeList<Entity> m_Buildings;
        private NativeList<Entity> m_NetObjects;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_NetObjectQuery;
        private JobHandle m_Job;
        private bool m_Pending;
        private bool m_Started;
        private readonly List<Obstacle> m_Candidates = new List<Obstacle>();
        private readonly List<Obstacle> m_Obstacles = new List<Obstacle>();

        /// <summary>Bumped whenever a new search has been collected, so that figures built on it (MEF) know to refresh.</summary>
        public int Version { get; private set; }

        public ObstacleMap(EntityManager entityManager)
        {
            m_BuildingQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            // Static objects that belong to something (FindJob keeps those of roads and bridges).
            m_NetObjectQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Static>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_Found = new NativeList<float4>(64, Allocator.Persistent);
            m_Turbines = new NativeList<bool>(64, Allocator.Persistent);
            m_BuildingCount = new NativeArray<int>(1, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_Job.Complete();
            if (m_Buildings.IsCreated)
            {
                m_Buildings.Dispose();
            }
            if (m_NetObjects.IsCreated)
            {
                m_NetObjects.Dispose();
                if (Mod.Setting != null && Mod.Setting.DetailedLog)
                {
                    Mod.Log.Info($"chart obstacles found: {m_BuildingCount[0]} buildings, {m_Found.Length - m_BuildingCount[0]} objects of roads and bridges");
                }
            }
            m_Found.Dispose();
            m_Turbines.Dispose();
            m_BuildingCount.Dispose();
        }

        /// <summary>Forgets the obstacles, so they are found again as soon as the infoview opens.</summary>
        public void Reset()
        {
            m_Started = false;
            m_Obstacles.Clear();
        }

        /// <summary>Collects a finished search, or starts a new one when due. Returns the job to add to the system's dependency, or default.</summary>
        public JobHandle Update(ComponentLookup<Game.Objects.Transform> transforms, ComponentLookup<PrefabRef> prefabRefs,
            ComponentLookup<ObjectGeometryData> geometries, ComponentLookup<WindPoweredData> windPowered, ComponentLookup<Owner> owners,
            ComponentLookup<Game.Net.Edge> edges, ComponentLookup<Game.Net.Node> nodes, Game.Simulation.TerrainSystem terrainSystem,
            Game.Simulation.WaterSystem waterSystem, float seaLevel, UrbanAreaMap urban, JobHandle dependency)
        {
            if (m_Pending)
            {
                // Wait for the built-up areas too (when shown), so the 299 ft rule sees them.
                if (!m_Job.IsCompleted || (urban != null && !urban.IsReady))
                {
                    return m_Job;
                }
                m_Job.Complete();
                m_Pending = false;
                m_Buildings.Dispose();
                m_NetObjects.Dispose();
                Declutter(seaLevel, urban);
                Version++;
            }
            // Once per opening of the chart, as a chart is printed rather than kept live: reopen it to see changes.
            if (m_Started)
            {
                return default;
            }
            m_Started = true;
            m_Buildings = m_BuildingQuery.ToEntityListAsync(Allocator.Persistent, out JobHandle listed);
            m_NetObjects = m_NetObjectQuery.ToEntityListAsync(Allocator.Persistent, out JobHandle netListed);
            Game.Simulation.WaterSurfaceData<Game.Simulation.SurfaceWater> water = waterSystem.GetSurfaceData(out JobHandle waterDependency);
            m_Job = new FindJob
            {
                m_Buildings = m_Buildings,
                m_NetObjects = m_NetObjects,
                m_Transforms = transforms,
                m_PrefabRefs = prefabRefs,
                m_Geometries = geometries,
                m_WindPowered = windPowered,
                m_Owners = owners,
                m_Edges = edges,
                m_Nodes = nodes,
                m_Terrain = terrainSystem.GetHeightData(),
                m_Water = water,
                m_Found = m_Found,
                m_Turbines = m_Turbines,
                m_BuildingCount = m_BuildingCount,
            }.Schedule(JobHandle.CombineDependencies(dependency, JobHandle.CombineDependencies(listed, netListed, waterDependency)));
            terrainSystem.AddCPUHeightReader(m_Job);
            waterSystem.AddSurfaceReader(m_Job);
            m_Pending = true;
            return m_Job;
        }

        /// <summary>
        /// The obstacles to draw, highest top first: a turbine near a cluster joins it, another obstacle near one shown makes
        /// it a group, and anything else near a shown symbol of the other kind is left out.
        /// </summary>
        private void Declutter(float seaLevel, UrbanAreaMap urban)
        {
            m_Candidates.Clear();
            float minUrban = kMinUrbanHeightFeet / kFeetPerMetre;
            for (int i = 0; i < m_Found.Length; i++)
            {
                float4 found = m_Found[i];
                if (found.w <= minUrban && urban != null && urban.IsUrban(found.xz))
                {
                    continue;
                }
                m_Candidates.Add(new Obstacle { m_Base = found.xyz, m_Top = found.y + found.w, m_Height = found.w, m_Turbine = m_Turbines[i] });
            }
            m_Candidates.Sort((a, b) => b.m_Top.CompareTo(a.m_Top));
            m_Obstacles.Clear();
            float spacing = kSpacing * kSpacing;
            float farmSpacing = kFarmSpacing * kFarmSpacing;
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                Obstacle candidate = m_Candidates[i];
                float2 at = candidate.m_Base.xz;
                bool placed = false;
                for (int j = 0; j < m_Obstacles.Count && !placed; j++)
                {
                    Obstacle shown = m_Obstacles[j];
                    if (candidate.m_Turbine && shown.m_Turbine)
                    {
                        for (int k = 0; k < shown.m_Members.Count && !placed; k++)
                        {
                            if (math.distancesq(shown.m_Members[k], at) < farmSpacing)
                            {
                                shown.m_Members.Add(at);
                                placed = true;
                            }
                        }
                    }
                    else if (math.distancesq(shown.m_Base.xz, at) < spacing)
                    {
                        if (!candidate.m_Turbine && !shown.m_Turbine)
                        {
                            shown.m_Kind = Kind.Group;
                        }
                        placed = true;
                    }
                }
                if (placed)
                {
                    continue;
                }
                candidate.m_Kind = candidate.m_Turbine ? Kind.Turbine : Kind.Obstacle;
                if (candidate.m_Turbine)
                {
                    candidate.m_Members = new List<float2> { at };
                }
                float agl = candidate.m_Height * kFeetPerMetre;
                float msl = math.max(0f, (candidate.m_Top - seaLevel) * kFeetPerMetre);
                candidate.m_TopText = ((int)math.round(msl)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                candidate.m_HeightText = "(" + ((int)math.round(agl)).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
                m_Obstacles.Add(candidate);
            }
            for (int i = 0; i < m_Obstacles.Count; i++)
            {
                ClassifyCluster(m_Obstacles[i]);
            }
        }

        /// <summary>
        /// A turbine cluster: one turbine, a turbine group (up to <see cref="kGroupTurbines"/>), or a wind farm outlined by its
        /// turbines' convex hull, moved out by <see cref="kFarmMargin"/> with rounded corners.
        /// </summary>
        private static void ClassifyCluster(Obstacle obstacle)
        {
            List<float2> members = obstacle.m_Members;
            if (members == null || members.Count < 2)
            {
                return;
            }
            if (members.Count <= kGroupTurbines)
            {
                obstacle.m_Kind = Kind.TurbineGroup;
                return;
            }
            obstacle.m_Kind = Kind.WindFarm;
            obstacle.m_FarmOutline = RoundedHull(members, kFarmMargin);
            float2 low = members[0];
            float2 high = members[0];
            for (int k = 1; k < members.Count; k++)
            {
                low = math.min(low, members[k]);
                high = math.max(high, members[k]);
            }
            obstacle.m_FarmCenter = (low + high) * 0.5f;
        }

        /// <summary>The convex hull of the points (counter-clockwise), each edge moved out by the margin and joined by arcs.</summary>
        private static float2[] RoundedHull(List<float2> points, float margin)
        {
            List<float2> sorted = new List<float2>(points);
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            List<float2> hull = new List<float2>();
            // Monotone chain: lower hull, then upper hull.
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int n = 0; n < sorted.Count; n++)
                {
                    float2 p = sorted[pass == 0 ? n : sorted.Count - 1 - n];
                    while (hull.Count - start >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            List<float2> outline = new List<float2>();
            for (int i = 0; i < hull.Count; i++)
            {
                float2 p = hull[i];
                float2 incoming = Outward(hull[(i - 1 + hull.Count) % hull.Count], p);
                float2 outgoing = Outward(p, hull[(i + 1) % hull.Count]);
                float from = math.atan2(incoming.y, incoming.x);
                float to = math.atan2(outgoing.y, outgoing.x);
                while (to < from)
                {
                    to += 2f * math.PI;
                }
                int steps = math.max(1, (int)math.ceil((to - from) / (math.PI / 12f)));
                for (int k = 0; k <= steps; k++)
                {
                    float angle = math.lerp(from, to, (float)k / steps);
                    outline.Add(p + new float2(math.cos(angle), math.sin(angle)) * margin);
                }
            }
            return outline.ToArray();
        }

        private static float Cross(float2 o, float2 a, float2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        /// <summary>Outward normal of the edge a → b of a counter-clockwise polygon.</summary>
        private static float2 Outward(float2 a, float2 b)
        {
            float2 d = math.normalizesafe(b - a);
            return new float2(d.y, -d.x);
        }

        /// <summary>
        /// The highest obstacle top (metres above the map's zero) found in a rectangle, among all obstacles found, shown or folded
        /// into a group, or float.MinValue.
        /// </summary>
        public float HighestTop(float2 low, float2 high)
        {
            float top = float.MinValue;
            for (int i = 0; i < m_Candidates.Count; i++)
            {
                float2 at = m_Candidates[i].m_Base.xz;
                if (math.all(at >= low) && math.all(at < high))
                {
                    top = math.max(top, m_Candidates[i].m_Top);
                }
            }
            return top;
        }

        /// <summary>Each obstacle's symbol with its elevation, in feet.</summary>
        public void Draw(OverlayRenderSystem.Buffer buffer, Color color)
        {
            for (int i = 0; i < m_Obstacles.Count; i++)
            {
                Obstacle obstacle = m_Obstacles[i];
                float agl = obstacle.m_Height * kFeetPerMetre;
                switch (obstacle.m_Kind)
                {
                    case Kind.Obstacle:
                        ObstacleSymbols.DrawObstacle(buffer, obstacle.m_Base, agl >= kTallFeet, agl > kLightedFeet, obstacle.m_TopText, obstacle.m_HeightText, color);
                        break;
                    case Kind.Group:
                        ObstacleSymbols.DrawGroup(buffer, obstacle.m_Base, agl >= kTallFeet, obstacle.m_TopText, color);
                        break;
                    case Kind.Turbine:
                        ObstacleSymbols.DrawTurbine(buffer, obstacle.m_Base, obstacle.m_TopText, obstacle.m_HeightText, color);
                        break;
                    case Kind.TurbineGroup:
                        ObstacleSymbols.DrawTurbineGroup(buffer, obstacle.m_Base, obstacle.m_TopText, color);
                        break;
                    case Kind.WindFarm:
                        ObstacleSymbols.DrawWindFarm(buffer, obstacle.m_FarmOutline, obstacle.m_FarmCenter, obstacle.m_Base.y, obstacle.m_TopText, color);
                        break;
                }
            }
        }
    }
}
