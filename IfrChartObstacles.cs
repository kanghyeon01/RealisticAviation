using System;
using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    /// <summary>An obstacle shown on the IFR chart: its top (world), its height above the ground (negative: terrain).</summary>
    internal struct ChartObstacle
    {
        public float3 m_Top;
        public float m_Height;

        /// <summary>SID: the number it has in the climb gradient notes (0: none).</summary>
        public int m_Number;

        /// <summary>SID: the climb gradient it asks for, and how far out it stands.</summary>
        public float m_Gradient;
        public float m_Distance;
    }

    /// <summary>
    /// Finds the obstacles the IFR chart shows, by the rules decided for it (docs/PLAN.md, IFR chart, obstacles), on a worker
    /// thread: for each segment and hold, the controlling obstacle (the one that sets its minimum altitude: highest top plus
    /// the MOC it needs where it stands, primary area full, secondary tapering; only one above the threshold); on final,
    /// those through the approach surface; under a SID, those through the 2.5 % obstacle identification surface. Terrain is
    /// sampled every 200 m (a terrain obstacle is a spot height), buildings by their prefab's top.
    /// </summary>
    internal sealed class IfrChartObstacles : IDisposable
    {
        internal enum AreaKind : byte
        {
            /// <summary>A segment a -> b with its areas: the controlling obstacle.</summary>
            Segment,

            /// <summary>Round a holding fix a, out to m_Radius plus the areas: the controlling obstacle.</summary>
            Round,

            /// <summary>Final from the threshold a outward along b (unit) for m_Radius metres: those through the approach surface.</summary>
            Final,

            /// <summary>A SID along the polyline of m_Points from its first point: those through the 2.5 % surface.</summary>
            Departure,

            /// <summary>
            /// An MSA sector round a (out to m_Radius, true bearings from m_B.x to m_B.y degrees): its highest obstacle or
            /// terrain, whatever its height.
            /// </summary>
            Sector,
        }

        internal struct Area
        {
            public AreaKind m_Kind;
            public float2 m_A;
            public float2 m_B;
            public float m_Base;
            public float m_Moc;
            public float m_Primary;
            public float m_Secondary;
            public float m_Radius;
            public float m_GlideTan;
            public int m_PointStart;
            public int m_PointCount;
        }

        /// <summary>What was found: the area it is for and the obstacle.</summary>
        internal struct Found
        {
            public int m_Area;
            public ChartObstacle m_Obstacle;

            /// <summary>For sorting: penetration (final) or gradient (SID).</summary>
            public float m_Score;
        }

        // Most obstacles listed for one final or SID, the worst first.
        private const int kListed = 3;
        private const float kStep = 200f;

        // Buildings lower than this can't stand out on any surface the chart checks.
        private const float kMinHeight = 10f;

        // Approach surface: 50 m under the glide path (which crosses the threshold 15 m up), 1,000 ft wide at the threshold,
        // 15 % splay, floored by a 2 % slope from 60 m out (Annex 14 approach surface).
        private const float kSurfaceClearance = 50f;
        private const float kCrossingHeight = 15f;
        private const float kInnerHalfWidth = 152f;
        private const float kFlare = 0.15f;
        private const float kFloorStart = 60f;
        private const float kFloorSlope = 0.02f;

        // SID: the obstacle identification surface from the runway end, 5 m up, 2.5 %; 150 m each side splaying 15°
        // (PANS-OPS departure area); the gradient asked for clears the obstacle by 0.8 % of its distance.
        private const float kSidStartHeight = 5f;
        private const float kSidSurface = 0.025f;
        private const float kSidHalfWidth = 150f;
        private const float kSidSplay = 0.268f;
        internal const float kSidMoc = 0.008f;

        private NativeList<Area> m_Areas;
        private NativeList<float2> m_Points;
        private NativeList<float4> m_Tops;
        private NativeList<Found> m_Found;
        private JobHandle m_Job;
        private bool m_Pending;
        private readonly Dictionary<Entity, float> m_PrefabTops = new Dictionary<Entity, float>();

        public IfrChartObstacles()
        {
            m_Areas = new NativeList<Area>(32, Allocator.Persistent);
            m_Points = new NativeList<float2>(64, Allocator.Persistent);
            m_Tops = new NativeList<float4>(1024, Allocator.Persistent);
            m_Found = new NativeList<Found>(32, Allocator.Persistent);
        }

        public bool IsPending => m_Pending;

        public int AddArea(Area area)
        {
            m_Areas.Add(area);
            return m_Areas.Length - 1;
        }

        public int AddPoint(float2 point)
        {
            m_Points.Add(point);
            return m_Points.Length - 1;
        }

        /// <summary>Forgets the areas of the last search (after any running one is done).</summary>
        public void Clear()
        {
            m_Job.Complete();
            m_Pending = false;
            m_Areas.Clear();
            m_Points.Clear();
            m_Found.Clear();
        }

        /// <summary>Starts the search over the areas added, with the buildings now in the city.</summary>
        public void Start(EntityManager entityManager, EntityQuery buildings, TerrainSystem terrainSystem)
        {
            m_Job.Complete();
            m_Tops.Clear();
            m_Found.Clear();
            NativeArray<Transform> transforms = buildings.ToComponentDataArray<Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabs = buildings.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            for (int i = 0; i < transforms.Length; i++)
            {
                Entity prefab = prefabs[i].m_Prefab;
                if (!m_PrefabTops.TryGetValue(prefab, out float height))
                {
                    height = entityManager.HasComponent<ObjectGeometryData>(prefab) ? entityManager.GetComponentData<ObjectGeometryData>(prefab).m_Bounds.max.y : 0f;
                    m_PrefabTops[prefab] = height;
                }
                if (height >= kMinHeight)
                {
                    float3 position = transforms[i].m_Position;
                    m_Tops.Add(new float4(position.x, position.y + height, position.z, height));
                }
            }
            transforms.Dispose();
            prefabs.Dispose();
            m_Job = new SearchJob
            {
                m_Terrain = terrainSystem.GetHeightData(),
                m_Areas = m_Areas.AsArray(),
                m_Points = m_Points.AsArray(),
                m_Tops = m_Tops.AsArray(),
                m_Found = m_Found,
            }.Schedule();
            terrainSystem.AddCPUHeightReader(m_Job);
            m_Pending = true;
        }

        /// <summary>The obstacles found, once the search is done (false while it runs).</summary>
        public bool TryCollect(List<Found> found)
        {
            if (!m_Pending || !m_Job.IsCompleted)
            {
                return false;
            }
            m_Job.Complete();
            m_Pending = false;
            found.Clear();
            for (int i = 0; i < m_Found.Length; i++)
            {
                found.Add(m_Found[i]);
            }
            return true;
        }

        public void Dispose()
        {
            m_Job.Complete();
            m_Areas.Dispose();
            m_Points.Dispose();
            m_Tops.Dispose();
            m_Found.Dispose();
        }

        private struct SearchJob : IJob
        {
            [ReadOnly] public TerrainHeightData m_Terrain;
            [ReadOnly] public NativeArray<Area> m_Areas;
            [ReadOnly] public NativeArray<float2> m_Points;

            /// <summary>Building tops: x, top height (world), z, height above the ground.</summary>
            [ReadOnly] public NativeArray<float4> m_Tops;
            public NativeList<Found> m_Found;

            public void Execute()
            {
                NativeList<Found> listed = new NativeList<Found>(16, Allocator.Temp);
                for (int i = 0; i < m_Areas.Length; i++)
                {
                    Area area = m_Areas[i];
                    switch (area.m_Kind)
                    {
                        case AreaKind.Segment:
                        case AreaKind.Round:
                            Controlling(i, area);
                            break;
                        case AreaKind.Final:
                            listed.Clear();
                            Final(i, area, listed);
                            Keep(listed);
                            break;
                        case AreaKind.Sector:
                            Highest(i, area);
                            break;
                        case AreaKind.Departure:
                            listed.Clear();
                            Departure(i, area, listed);
                            Keep(listed);
                            break;
                    }
                }
                listed.Dispose();
            }

            private float Ground(float2 p) => TerrainUtils.SampleHeight(ref m_Terrain, new float3(p.x, 0f, p.y));

            private static bool InSector(in Area area, float2 p)
            {
                float2 offset = p - area.m_A;
                if (math.lengthsq(offset) > area.m_Radius * area.m_Radius)
                {
                    return false;
                }
                float bearing = (RunwayNames.Heading(offset) + 360f) % 360f;
                return bearing >= area.m_B.x && bearing < area.m_B.y;
            }

            /// <summary>The highest terrain point or building top in an MSA sector.</summary>
            private void Highest(int index, in Area area)
            {
                float best = float.MinValue;
                ChartObstacle obstacle = default;
                for (float x = -area.m_Radius; x <= area.m_Radius; x += kStep)
                {
                    for (float z = -area.m_Radius; z <= area.m_Radius; z += kStep)
                    {
                        float2 p = area.m_A + new float2(x, z);
                        if (!InSector(area, p))
                        {
                            continue;
                        }
                        float top = Ground(p);
                        if (top > best)
                        {
                            best = top;
                            obstacle = new ChartObstacle { m_Top = new float3(p.x, top, p.y), m_Height = -1f };
                        }
                    }
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float4 top = m_Tops[i];
                    if (top.y > best && InSector(area, top.xz))
                    {
                        best = top.y;
                        obstacle = new ChartObstacle { m_Top = top.xyz, m_Height = top.w };
                    }
                }
                if (best > float.MinValue)
                {
                    m_Found.Add(new Found { m_Area = index, m_Obstacle = obstacle });
                }
            }

            /// <summary>The MOC an obstacle needs this far beside the track (or round the hold), or a negative value outside the areas.</summary>
            private static float MocAt(in Area area, float lateral)
            {
                if (lateral <= area.m_Primary)
                {
                    return area.m_Moc;
                }
                return lateral <= area.m_Primary + area.m_Secondary ? area.m_Moc * (1f - (lateral - area.m_Primary) / math.max(area.m_Secondary, 1f)) : -1f;
            }

            /// <summary>Distance beside the area's track (segment) or outside the hold's reach (round).</summary>
            private static float Lateral(in Area area, float2 p)
            {
                if (area.m_Kind == AreaKind.Round)
                {
                    return math.max(0f, math.distance(p, area.m_A) - area.m_Radius);
                }
                float2 along = area.m_B - area.m_A;
                float length = math.length(along);
                float2 unit = along / math.max(length, 1f);
                float t = math.clamp(math.dot(p - area.m_A, unit), 0f, length);
                return math.distance(p, area.m_A + unit * t);
            }

            /// <summary>The obstacle with the highest top plus its MOC, if it stands above the threshold.</summary>
            private void Controlling(int index, in Area area)
            {
                float reach = area.m_Primary + area.m_Secondary;
                float best = float.MinValue;
                ChartObstacle obstacle = default;
                // Terrain: a grid over the area's bounds.
                float2 min, max;
                if (area.m_Kind == AreaKind.Round)
                {
                    min = area.m_A - (area.m_Radius + reach);
                    max = area.m_A + (area.m_Radius + reach);
                }
                else
                {
                    min = math.min(area.m_A, area.m_B) - reach;
                    max = math.max(area.m_A, area.m_B) + reach;
                }
                for (float x = min.x; x <= max.x; x += kStep)
                {
                    for (float z = min.y; z <= max.y; z += kStep)
                    {
                        float2 p = new float2(x, z);
                        float need = MocAt(area, Lateral(area, p));
                        if (need < 0f)
                        {
                            continue;
                        }
                        float top = Ground(p);
                        if (top + need > best)
                        {
                            best = top + need;
                            obstacle = new ChartObstacle { m_Top = new float3(p.x, top, p.y), m_Height = -1f };
                        }
                    }
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float4 top = m_Tops[i];
                    float need = MocAt(area, Lateral(area, top.xz));
                    if (need >= 0f && top.y + need > best)
                    {
                        best = top.y + need;
                        obstacle = new ChartObstacle { m_Top = top.xyz, m_Height = top.w };
                    }
                }
                if (best > float.MinValue && obstacle.m_Top.y > area.m_Base)
                {
                    m_Found.Add(new Found { m_Area = index, m_Obstacle = obstacle });
                }
            }

            /// <summary>Height of the approach surface this far out from the threshold, above the threshold.</summary>
            private static float Surface(in Area area, float distance)
            {
                return math.max(kCrossingHeight + distance * area.m_GlideTan - kSurfaceClearance, math.max(0f, distance - kFloorStart) * kFloorSlope);
            }

            private void Final(int index, in Area area, NativeList<Found> listed)
            {
                float2 outward = area.m_B, side = new float2(-outward.y, outward.x);
                float length = area.m_Radius;
                // Terrain: the highest point through the surface (one spot height, not every sample).
                Found terrain = default;
                terrain.m_Score = 0f;
                for (float d = 0f; d <= length; d += kStep)
                {
                    float half = kInnerHalfWidth + kFlare * d;
                    for (float l = -half; l <= half; l += kStep)
                    {
                        float2 p = area.m_A + outward * d + side * l;
                        float top = Ground(p);
                        float through = top - (area.m_Base + Surface(area, d));
                        if (through > terrain.m_Score)
                        {
                            terrain = new Found { m_Area = index, m_Score = through, m_Obstacle = new ChartObstacle { m_Top = new float3(p.x, top, p.y), m_Height = -1f } };
                        }
                    }
                }
                if (terrain.m_Score > 0f)
                {
                    listed.Add(terrain);
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float4 top = m_Tops[i];
                    float2 offset = top.xz - area.m_A;
                    float d = math.dot(offset, outward);
                    if (d <= 0f || d > length || math.abs(math.dot(offset, side)) > kInnerHalfWidth + kFlare * d)
                    {
                        continue;
                    }
                    float through = top.y - (area.m_Base + Surface(area, d));
                    if (through > 0f)
                    {
                        listed.Add(new Found { m_Area = index, m_Score = through, m_Obstacle = new ChartObstacle { m_Top = top.xyz, m_Height = top.w } });
                    }
                }
            }

            /// <summary>Distance along the SID's polyline to the point nearest p, and how far beside it p is.</summary>
            private void AlongSid(in Area area, float2 p, out float along, out float lateral)
            {
                along = 0f;
                lateral = float.MaxValue;
                float walked = 0f;
                for (int k = 1; k < area.m_PointCount; k++)
                {
                    float2 a = m_Points[area.m_PointStart + k - 1], b = m_Points[area.m_PointStart + k];
                    float length = math.distance(a, b);
                    float2 unit = (b - a) / math.max(length, 1f);
                    float t = math.clamp(math.dot(p - a, unit), 0f, length);
                    float distance = math.distance(p, a + unit * t);
                    if (distance < lateral)
                    {
                        lateral = distance;
                        along = walked + t;
                    }
                    walked += length;
                }
            }

            private void Departure(int index, in Area area, NativeList<Found> listed)
            {
                Found terrain = default;
                for (int k = 1; k < area.m_PointCount; k++)
                {
                    float2 a = m_Points[area.m_PointStart + k - 1], b = m_Points[area.m_PointStart + k];
                    float length = math.distance(a, b);
                    float2 unit = (b - a) / math.max(length, 1f), side = new float2(-unit.y, unit.x);
                    for (float s = 0f; s <= length; s += kStep)
                    {
                        float2 center = a + unit * s;
                        AlongSid(area, center, out float along, out _);
                        float half = kSidHalfWidth + kSidSplay * along;
                        for (float l = -half; l <= half; l += kStep)
                        {
                            Check(index, area, center + side * l, Ground(center + side * l), -1f, ref terrain, listed, true);
                        }
                    }
                }
                if (terrain.m_Score > 0f)
                {
                    listed.Add(terrain);
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float4 top = m_Tops[i];
                    Check(index, area, top.xz, top.y, top.w, ref terrain, listed, false);
                }
            }

            private void Check(int index, in Area area, float2 p, float top, float height, ref Found terrain, NativeList<Found> listed, bool isTerrain)
            {
                AlongSid(area, p, out float along, out float lateral);
                if (along <= 0f || lateral > kSidHalfWidth + kSidSplay * along || top <= area.m_Base + kSidStartHeight + kSidSurface * along)
                {
                    return;
                }
                float gradient = (top - area.m_Base - kSidStartHeight) / along + kSidMoc;
                Found found = new Found
                {
                    m_Area = index,
                    m_Score = gradient,
                    m_Obstacle = new ChartObstacle { m_Top = new float3(p.x, top, p.y), m_Height = height, m_Gradient = gradient, m_Distance = along },
                };
                if (!isTerrain)
                {
                    listed.Add(found);
                }
                else if (gradient > terrain.m_Score)
                {
                    terrain = found;
                }
            }

            /// <summary>The worst few of a final's or a SID's list.</summary>
            private void Keep(NativeList<Found> listed)
            {
                for (int n = 0; n < kListed && listed.Length > 0; n++)
                {
                    int worst = 0;
                    for (int i = 1; i < listed.Length; i++)
                    {
                        if (listed[i].m_Score > listed[worst].m_Score)
                        {
                            worst = i;
                        }
                    }
                    m_Found.Add(listed[worst]);
                    listed.RemoveAtSwapBack(worst);
                }
            }
        }
    }
}
