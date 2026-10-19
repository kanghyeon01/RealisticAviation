using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Buildings;
using Game.Citizens;
using Game.Companies;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// Built-up areas for the airspace infoview, yellow as on a VFR sectional chart: where residents and workers are dense.
    /// The playable area is cut into <see cref="kCellSize"/> cells; each counts the residents of its homes and the workers of
    /// its companies (city services such as airports are not counted). A cell is built-up when the density over the square
    /// of <see cref="kWindow"/> cells around it (about 400 m, read from a summed-area table) reaches <see cref="kUrbanDensity"/>:
    /// fine cells give a smooth outline, the wide window keeps single buildings from showing.
    /// Counted on a worker thread once each time the infoview opens; drawn every frame as merged rectangles
    /// (<see cref="RectMerger"/>).
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


        private static readonly Color kYellow = new Color(1f, 0.85f, 0.25f, 0.7f);

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
        private NativeList<Entity> m_Buildings;
        private EntityQuery m_BuildingQuery;
        private JobHandle m_Job;
        private bool m_Pending;
        private bool m_Started;

        /// <summary>Whether this opening's count is done (the obstacle rules wait for it).</summary>
        public bool IsReady { get; private set; }
        private readonly List<ChartRect> m_Rects = new List<ChartRect>();

        public UrbanAreaMap(EntityManager entityManager)
        {
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
            m_Rects.Clear();
            System.Array.Clear(m_Urban, 0, m_Urban.Length);
        }

        /// <summary>
        /// Collects a finished count, or starts a new one when due. Returns the job to add to the system's dependency (it
        /// reads buildings, renters and households), or default.
        /// </summary>
        public JobHandle Update(ComponentLookup<Game.Objects.Transform> transforms, BufferLookup<Renter> renters,
            BufferLookup<HouseholdCitizen> citizens, BufferLookup<Employee> employees, JobHandle dependency)
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
                BuildStrips();
            }
            // Once per opening of the chart, as a chart is printed rather than kept live: reopen it to see changes.
            if (m_Started)
            {
                return default;
            }
            m_Started = true;
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

        private void BuildStrips()
        {
            RectMerger merger = new RectMerger(kCellSize, kHalfExtent, m_Rects);
            // Height of a strip without buildings of its own (filled in by its neighbours): the city's average.
            float allHeights = 0f;
            int allBuildings = 0;
            for (int i = 0; i < m_BuildingCount.Length; i++)
            {
                allHeights += m_HeightSum[i];
                allBuildings += m_BuildingCount[i];
            }
            float averageHeight = allBuildings > 0 ? allHeights / allBuildings : 0f;
            for (int z = 0; z < kCells; z++)
            {
                merger.BeginRow(z);
                int start = -1;
                float heightSum = 0f;
                int heightCount = 0;
                for (int x = 0; x <= kCells; x++)
                {
                    bool urban = x < kCells && m_UrbanCells[x + z * kCells];
                    if (x < kCells)
                    {
                        m_Urban[x + z * kCells] = urban;
                    }
                    if (urban)
                    {
                        if (start < 0)
                        {
                            start = x;
                            heightSum = 0f;
                            heightCount = 0;
                        }
                        int index = x + z * kCells;
                        heightSum += m_HeightSum[index];
                        heightCount += m_BuildingCount[index];
                    }
                    else if (start >= 0)
                    {
                        float y = heightCount > 0 ? heightSum / heightCount : averageHeight;
                        merger.AddRun(start, x, 0, y * (x - start));
                        start = -1;
                    }
                }
                merger.EndRow();
            }
            merger.Finish();
            IsReady = true;
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

        public void Draw(OverlayRenderSystem.Buffer buffer)
        {
            for (int i = 0; i < m_Rects.Count; i++)
            {
                // Square ends, as wide as the rectangle is tall, so neighbouring rectangles join without gaps or overlaps.
                buffer.DrawLine(kYellow, kYellow, 0f, OverlayRenderSystem.StyleFlags.Projected,
                    new Line3.Segment(m_Rects[i].m_From, m_Rects[i].m_To), m_Rects[i].m_Width, new float2(0f, 0f));
            }
        }
    }
}
