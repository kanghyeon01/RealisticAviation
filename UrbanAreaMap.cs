using System.Collections.Generic;
using Colossal.Entities;
using Colossal.Mathematics;
using Game.Rendering;
using UnityEngine;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Built-up areas for the airspace infoview, yellow as on a VFR sectional chart: where residents and workers are dense.
    /// The playable area is cut into <see cref="kCellSize"/> cells; each counts the residents of its homes and the workers of
    /// its companies (city services such as airports are not counted). A cell is built-up when the density over the square
    /// of <see cref="kWindow"/> cells around it (about 400 m, read from a summed-area table) reaches <see cref="kUrbanDensity"/>:
    /// fine cells give a smooth outline, the wide window keeps single buildings from showing.
    /// Counted on a worker thread once each time the infoview opens; coloured in the terrain'"'"'s infoview colours
    /// (<see cref="VfrChartTintSystem"/>), as the game'"'"'s heatmaps, so the outline is smooth and doesn'"'"'t flicker.
    /// </summary>
    internal sealed class UrbanAreaMap
    {
        private const float kCellSize = 32f;
        private const float kHalfExtent = 7168f;
        private const int kCells = (int)(2f * kHalfExtent / kCellSize);

        /// <summary>Cells on each side of a cell in the density window (13 × 13 cells, 416 m).</summary>
        private const int kWindow = 6;

        /// <summary>Residents and workers per square kilometre at which an area is built-up.</summary>
        private const float kUrbanDensity = 2500f;



        private struct CountJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buildings;
            [ReadOnly] public ComponentLookup<Game.Objects.Transform> m_Transforms;
            [ReadOnly] public BufferLookup<Renter> m_Renters;
            [ReadOnly] public BufferLookup<HouseholdCitizen> m_Citizens;
            [ReadOnly] public BufferLookup<Employee> m_Employees;
            public NativeArray<int> m_People;
            public NativeArray<float> m_HeightSum;
            public NativeArray<int> m_BuildingCount;

            /// <summary>Summed-area table of people, (kCells + 1)² entries, and the built-up flag of each cell.</summary>
            public NativeArray<int> m_Sums;
            public NativeArray<bool> m_Urban;
            public int m_Threshold;

            public void Execute()
            {
                for (int i = 0; i < m_People.Length; i++)
                {
                    m_People[i] = 0;
                    m_HeightSum[i] = 0f;
                    m_BuildingCount[i] = 0;
                }
                for (int i = 0; i < m_Buildings.Length; i++)
                {
                    Entity building = m_Buildings[i];
                    float3 position = m_Transforms[building].m_Position;
                    int2 cell = (int2)math.floor((position.xz + kHalfExtent) / kCellSize);
                    if (math.any(cell < 0) || math.any(cell >= kCells))
                    {
                        continue;
                    }
                    DynamicBuffer<Renter> renters = m_Renters[building];
                    int people = 0;
                    for (int r = 0; r < renters.Length; r++)
                    {
                        Entity renter = renters[r].m_Renter;
                        if (m_Citizens.TryGetBuffer(renter, out DynamicBuffer<HouseholdCitizen> citizens))
                        {
                            people += citizens.Length;
                        }
                        else if (m_Employees.TryGetBuffer(renter, out DynamicBuffer<Employee> employees))
                        {
                            people += employees.Length;
                        }
                    }
                    int index = cell.x + cell.y * kCells;
                    m_People[index] += people;
                    m_HeightSum[index] += position.y;
                    m_BuildingCount[index]++;
                }
                int stride = kCells + 1;
                for (int z = 0; z <= kCells; z++)
                {
                    for (int x = 0; x <= kCells; x++)
                    {
                        m_Sums[x + z * stride] = x == 0 || z == 0 ? 0
                            : m_People[(x - 1) + (z - 1) * kCells] + m_Sums[(x - 1) + z * stride] + m_Sums[x + (z - 1) * stride] - m_Sums[(x - 1) + (z - 1) * stride];
                    }
                }
                for (int z = 0; z < kCells; z++)
                {
                    for (int x = 0; x < kCells; x++)
                    {
                        int x0 = math.max(0, x - kWindow), x1 = math.min(kCells, x + kWindow + 1);
                        int z0 = math.max(0, z - kWindow), z1 = math.min(kCells, z + kWindow + 1);
                        int people = m_Sums[x1 + z1 * stride] - m_Sums[x0 + z1 * stride] - m_Sums[x1 + z0 * stride] + m_Sums[x0 + z0 * stride];
                        m_Urban[x + z * kCells] = people >= m_Threshold;
                    }
                }
            }
        }

        private NativeArray<int> m_People;
        private NativeArray<float> m_HeightSum;
        private NativeArray<int> m_BuildingCount;
        private NativeArray<int> m_Sums;
        private NativeArray<bool> m_UrbanCells;

        /// <summary>The last finished count's built-up flags, for <see cref="IsUrban"/> (the job writes <see cref="m_UrbanCells"/>).</summary>
        private readonly bool[] m_Urban = new bool[kCells * kCells];
        /// <summary>The built-up flags as a smoothed field (the fill and the outline cut it at one half).</summary>
        private readonly float[] m_Flags = new float[kCells * kCells];
        private readonly float[] m_Scratch = new float[kCells * kCells];
        private readonly List<float4> m_Contour = new List<float4>();

        /// <summary>The outline as pairs of segment ends.</summary>
        private readonly List<float3> m_Outline = new List<float3>();
        private readonly List<float3> m_Kept = new List<float3>();
        private int m_KeptVersion = -1;
        private int m_KeptTerrainVersion = -1;
        private NativeList<Entity> m_Buildings;
        private EntityManager m_EntityManager;
        private readonly List<Entity> m_Airports = new List<Entity>();
        private EntityQuery m_BuildingQuery;
        private JobHandle m_Job;
        private bool m_Pending;
        private bool m_Started;

        /// <summary>Whether this opening's count is done (the obstacle rules wait for it).</summary>
        public bool IsReady { get; private set; }

        public UrbanAreaMap(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
            m_BuildingQuery = entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<Renter>() },
                None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            m_People = new NativeArray<int>(kCells * kCells, Allocator.Persistent);
            m_HeightSum = new NativeArray<float>(kCells * kCells, Allocator.Persistent);
            m_BuildingCount = new NativeArray<int>(kCells * kCells, Allocator.Persistent);
            m_Sums = new NativeArray<int>((kCells + 1) * (kCells + 1), Allocator.Persistent);
            m_UrbanCells = new NativeArray<bool>(kCells * kCells, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_Job.Complete();
            if (m_Buildings.IsCreated)
            {
                m_Buildings.Dispose();
            }
            m_People.Dispose();
            m_HeightSum.Dispose();
            m_BuildingCount.Dispose();
            m_Sums.Dispose();
            m_UrbanCells.Dispose();
        }

        /// <summary>Forgets the areas, so they are counted again as soon as the infoview opens.</summary>
        public void Reset()
        {
            m_Started = false;
            IsReady = false;
            System.Array.Clear(m_Urban, 0, m_Urban.Length);
            m_Outline.Clear();
        }

        /// <summary>
        /// Collects a finished count, or starts a new one when due. Returns the job to add to the system's dependency (it
        /// reads buildings, renters and households), or default. The airports' lots are cut out of the areas.
        /// </summary>
        public JobHandle Update(ComponentLookup<Game.Objects.Transform> transforms, BufferLookup<Renter> renters,
            BufferLookup<HouseholdCitizen> citizens, BufferLookup<Employee> employees, NativeParallelHashMap<Entity, AirportSpace> airports,
            JobHandle dependency)
        {
            if (m_Pending)
            {
                if (!m_Job.IsCompleted)
                {
                    return m_Job;
                }
                m_Job.Complete();
                m_Pending = false;
                m_Buildings.Dispose();
                KeepFlags();
            }
            // Once per opening of the chart, as a chart is printed rather than kept live: reopen it to see changes.
            if (m_Started)
            {
                return default;
            }
            m_Started = true;
            m_Airports.Clear();
            if (airports.IsCreated)
            {
                NativeArray<Entity> keys = airports.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    m_Airports.Add(keys[i]);
                }
                keys.Dispose();
            }
            m_Buildings = m_BuildingQuery.ToEntityListAsync(Allocator.Persistent, out JobHandle listed);
            m_Job = new CountJob
            {
                m_Buildings = m_Buildings,
                m_Transforms = transforms,
                m_Renters = renters,
                m_Citizens = citizens,
                m_Employees = employees,
                m_People = m_People,
                m_HeightSum = m_HeightSum,
                m_BuildingCount = m_BuildingCount,
                m_Sums = m_Sums,
                m_Urban = m_UrbanCells,
                m_Threshold = (int)math.ceil(kUrbanDensity * math.square((2 * kWindow + 1) * kCellSize) / 1e6f),
            }.Schedule(JobHandle.CombineDependencies(dependency, listed));
            m_Pending = true;
            return m_Job;
        }

        private void KeepFlags()
        {
            m_UrbanCells.CopyTo(m_Urban);
            // The outline: where the flags, interpolated between the cell centres, cross one half (the yellow's own edge).
            float allHeights = 0f;
            int allBuildings = 0;
            for (int i = 0; i < m_Urban.Length; i++)
            {
                m_Flags[i] = m_Urban[i] ? 1f : 0f;
                allHeights += m_HeightSum[i];
                allBuildings += m_BuildingCount[i];
            }
            float averageHeight = allBuildings > 0 ? allHeights / allBuildings : 0f;
            m_Contour.Clear();
            // Smoothed (two 3 x 3 box blurs) so the edge is round rather than stepped at 45°: the yellow and its outline
            // both follow it (user: the outline zigzagged).
            Blur(m_Flags, m_Scratch);
            Blur(m_Flags, m_Scratch);
            CutAirports();
            ContourLines.Trace(m_Flags, kCells, kCellSize, new float2(-kHalfExtent, -kHalfExtent), 0.5f, m_Contour);
            m_Outline.Clear();
            for (int i = 0; i < m_Contour.Count; i++)
            {
                float4 segment = m_Contour[i];
                int2 cell = math.clamp((int2)math.floor(((segment.xy + segment.zw) * 0.5f + kHalfExtent) / kCellSize), 0, kCells - 1);
                int index = cell.x + cell.y * kCells;
                float y = m_BuildingCount[index] > 0 ? m_HeightSum[index] / m_BuildingCount[index] : averageHeight;
                m_Outline.Add(new float3(segment.x, y, segment.y));
                m_Outline.Add(new float3(segment.z, y, segment.w));
            }
            IsReady = true;
            Version++;
        }

        /// <summary>
        /// The outline of the built-up areas: a hairline, as on the chart, but not where it runs over water (the built-up cells
        /// reach past the shore; there the shoreline alone is the edge, user). Kept segments are worked out again when the
        /// count or the terrain sampling changes.
        /// </summary>
        public void DrawOutline(OverlayRenderSystem.Buffer buffer, Color color, float width, TerrainChartMap terrain)
        {
            int terrainVersion = terrain != null ? terrain.Version : -1;
            if (Version != m_KeptVersion || terrainVersion != m_KeptTerrainVersion)
            {
                m_KeptVersion = Version;
                m_KeptTerrainVersion = terrainVersion;
                m_Kept.Clear();
                for (int i = 0; i + 1 < m_Outline.Count; i += 2)
                {
                    float3 a = m_Outline[i], b = m_Outline[i + 1];
                    if (terrain == null || !terrain.IsWater((a.xz + b.xz) * 0.5f))
                    {
                        m_Kept.Add(a);
                        m_Kept.Add(b);
                    }
                }
            }
            for (int i = 0; i + 1 < m_Kept.Count; i += 2)
            {
                buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(m_Kept[i], m_Kept[i + 1]), width, new float2(1f, 1f));
            }
        }

        /// <summary>Counts finished counts, so a texture made from them knows when to be written again.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// The built-up areas into one channel of an RGBA texture of <see cref="TerrainChartMap.kTexels"/>² texels (full or 0):
        /// a texel is built-up where the cells' flags, interpolated there, reach one half, so the outline is smooth on the
        /// finer grid.
        /// </summary>
        public void WriteUrban(byte[] texture, int channel)
        {
            const int perCell = TerrainChartMap.kTexelsPerCell, texels = TerrainChartMap.kTexels;
            for (int tz = 0; tz < texels; tz++)
            {
                float cz = (tz + 0.5f) / perCell - 0.5f;
                int z0 = math.clamp((int)math.floor(cz), 0, kCells - 1), z1 = math.min(z0 + 1, kCells - 1);
                float fz = math.saturate(cz - z0);
                for (int tx = 0; tx < texels; tx++)
                {
                    float cx = (tx + 0.5f) / perCell - 0.5f;
                    int x0 = math.clamp((int)math.floor(cx), 0, kCells - 1), x1 = math.min(x0 + 1, kCells - 1);
                    float fx = math.saturate(cx - x0);
                    float urban = math.lerp(math.lerp(Flag(x0, z0), Flag(x1, z0), fx), math.lerp(Flag(x0, z1), Flag(x1, z1), fx), fz);
                    if (urban >= 0.5f)
                    {
                        texture[(tx + tz * texels) * 4 + channel] = 255;
                    }
                }
            }
        }

        private float Flag(int x, int z) => m_Flags[x + z * kCells];

        /// <summary>
        /// The airports' lots (the airport building's and each upgrade's, as the game lays them out) cut out of the smoothed
        /// flags, so the yellow and its outline go round an airport instead of across it (user). Each cell keeps at most its
        /// distance outside the nearest lot edge in cells plus one half: zero inside, one half on the edge, so the edge of the
        /// cut, interpolated between the cell centres, follows the lot's straight sides.
        /// </summary>
        private void CutAirports()
        {
            for (int i = 0; i < m_Airports.Count; i++)
            {
                Entity airport = m_Airports[i];
                if (!m_EntityManager.Exists(airport))
                {
                    continue;
                }
                CutLot(airport);
                if (m_EntityManager.TryGetBuffer(airport, true, out DynamicBuffer<InstalledUpgrade> upgrades))
                {
                    for (int u = 0; u < upgrades.Length; u++)
                    {
                        CutLot(upgrades[u].m_Upgrade);
                    }
                }
            }
        }

        private void CutLot(Entity building)
        {
            if (!m_EntityManager.TryGetComponent(building, out Game.Objects.Transform transform)
                || !m_EntityManager.TryGetComponent(building, out Game.Prefabs.PrefabRef prefab))
            {
                return;
            }
            int2 lotSize = m_EntityManager.TryGetComponent(prefab.m_Prefab, out Game.Prefabs.BuildingData data) ? data.m_LotSize
                : m_EntityManager.TryGetComponent(prefab.m_Prefab, out Game.Prefabs.BuildingExtensionData extension) ? extension.m_LotSize
                : int2.zero;
            if (math.any(lotSize <= 0))
            {
                return;
            }
            Quad3 corners = BuildingUtils.CalculateCorners(transform, lotSize);
            float2 a = corners.a.xz, b = corners.b.xz, c = corners.c.xz, d = corners.d.xz;
            // Each side's outward normal: the quad's winding decides the sign.
            float winding = math.sign((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x));
            float2 min = math.min(math.min(a, b), math.min(c, d)) - kCellSize;
            float2 max = math.max(math.max(a, b), math.max(c, d)) + kCellSize;
            int2 first = math.max((int2)math.floor((min + kHalfExtent) / kCellSize - 0.5f), 0);
            int2 last = math.min((int2)math.ceil((max + kHalfExtent) / kCellSize - 0.5f), kCells - 1);
            for (int z = first.y; z <= last.y; z++)
            {
                for (int x = first.x; x <= last.x; x++)
                {
                    float2 p = new float2((x + 0.5f) * kCellSize - kHalfExtent, (z + 0.5f) * kCellSize - kHalfExtent);
                    float outside = math.max(math.max(Outside(a, b, p, winding), Outside(b, c, p, winding)),
                        math.max(Outside(c, d, p, winding), Outside(d, a, p, winding)));
                    int index = x + z * kCells;
                    m_Flags[index] = math.min(m_Flags[index], math.max(0f, outside / kCellSize + 0.5f));
                }
            }
        }

        /// <summary>How far a point lies outside a side of a lot (negative inside).</summary>
        private static float Outside(float2 from, float2 to, float2 point, float winding)
        {
            float2 side = to - from;
            float length = math.length(side);
            if (length < 1e-3f)
            {
                return float.MinValue;
            }
            return -winding * (side.x * (point.y - from.y) - side.y * (point.x - from.x)) / length;
        }

        /// <summary>A 3 x 3 box blur of the grid in place (through the scratch grid).</summary>
        private static void Blur(float[] values, float[] scratch)
        {
            for (int z = 0; z < kCells; z++)
            {
                for (int x = 0; x < kCells; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int nz = z + dz;
                        if (nz < 0 || nz >= kCells)
                        {
                            continue;
                        }
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx >= 0 && nx < kCells)
                            {
                                sum += values[nx + nz * kCells];
                                count++;
                            }
                        }
                    }
                    scratch[x + z * kCells] = sum / count;
                }
            }
            System.Array.Copy(scratch, values, values.Length);
        }

        /// <summary>
        /// Whether a point lies in a built-up area as last counted (false before the first count, or while the built-up areas
        /// are turned off in the legend, when they are not counted).
        /// </summary>
        public bool IsUrban(float2 position)
        {
            int2 cell = (int2)math.floor((position + kHalfExtent) / kCellSize);
            return math.all(cell >= 0) && math.all(cell < kCells) && m_Urban[cell.x + cell.y * kCells];
        }
    }
}
