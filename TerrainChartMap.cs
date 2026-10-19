using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Rendering;
using Game.Simulation;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// The terrain of the VFR chart infoview, as a sectional chart shows it:
    /// <list type="bullet">
    /// <item>elevation tints: the playable area in <see cref="kCellSize"/> cells, each coloured by its height above sea level
    /// in the chart's eight tints (green at sea level to brown at the top). The real chart's bands are thousands of feet
    /// apart, more than a game map rises, so the bands are fitted to the map: sea level to its highest land in eight equal
    /// steps of whole hundreds of feet, at least <see cref="kMinBandFeet"/> each. Water is left uncoloured;</item>
    /// <item>spot elevations: peaks that stand at least <see cref="kProminenceFeet"/> above the ground about 1 km round them,
    /// at least <see cref="kPeakSpacing"/> apart, highest first, as a dot with the elevation;</item>
    /// <item>Maximum Elevation Figures: the area cut into <see cref="kMefCells"/> × <see cref="kMefCells"/> quadrants; this
    /// class gives each one's highest terrain, <see cref="AirspaceOverlaySystem"/> adds the obstacles.</item>
    /// </list>
    /// Sampled on a worker thread once each time the infoview opens; the tints are drawn as merged rectangles.
    /// </summary>
    internal sealed class TerrainChartMap
    {
        private const float kCellSize = 32f;
        private const float kHalfExtent = 7168f;
        private const int kCells = (int)(2f * kHalfExtent / kCellSize);
        private const float kFeetPerMetre = 3.28084f;
        private const byte kWater = 255;

        public const int kMefCells = 4;

        // The chart's eight elevation tints (from its legend), low to high, and the least height of one band.
        private const float kMinBandFeet = 200f;
        private static readonly Color[] kTints =
        {
            Tint(0xc6, 0xdb, 0xa6), Tint(0xd8, 0xe6, 0xb3), Tint(0xf4, 0xf0, 0xd0), Tint(0xf5, 0xe7, 0xa8),
            Tint(0xf2, 0xd4, 0x8d), Tint(0xe9, 0xbc, 0x96), Tint(0xe8, 0xa8, 0x7d), Tint(0xb9, 0x8b, 0x45),
        };

        // Peaks: the search radius and step (in cells), the least rise over the surrounding ring, and the spacing.
        private const int kPeakRadius = 32;
        private const int kPeakStep = 4;
        private const float kProminenceFeet = 100f;
        private const float kPeakSpacing = 1500f;

        private static readonly Color kSpotColor = new Color(0.1f, 0.1f, 0.1f, 1f);
        private const float kSpotDot = 20f;
        private const float kSpotText = 45f;

        private static Color Tint(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 0.8f);

        private struct SampleJob : IJob
        {
            [ReadOnly] public TerrainHeightData m_Terrain;
            [ReadOnly] public WaterSurfaceData<SurfaceWater> m_Water;
            public float m_SeaLevel;
            public NativeArray<float> m_Heights;
            public NativeArray<byte> m_Bands;
            public NativeArray<float> m_MefTerrain;
            public NativeList<float3> m_Peaks;

            /// <summary>The band height chosen for the map, in feet (one entry).</summary>
            public NativeArray<float> m_BandFeet;

            public void Execute()
            {
                for (int i = 0; i < m_MefTerrain.Length; i++)
                {
                    m_MefTerrain[i] = float.MinValue;
                }
                int perMef = kCells / kMefCells;
                float highestLand = m_SeaLevel;
                for (int z = 0; z < kCells; z++)
                {
                    for (int x = 0; x < kCells; x++)
                    {
                        float3 position = CellCenter(x, z);
                        float height = TerrainUtils.SampleHeight(ref m_Terrain, position);
                        position.y = height;
                        int index = x + z * kCells;
                        m_Heights[index] = height;
                        bool water = WaterUtils.SampleDepth(ref m_Water, position) > 0.5f;
                        m_Bands[index] = water ? kWater : (byte)0;
                        if (!water)
                        {
                            highestLand = math.max(highestLand, height);
                        }
                        int mef = math.min(x / perMef, kMefCells - 1) + math.min(z / perMef, kMefCells - 1) * kMefCells;
                        m_MefTerrain[mef] = math.max(m_MefTerrain[mef], height);
                    }
                }
                // Eight bands from sea level to the highest land, each a whole number of hundreds of feet and not too thin.
                float topFeet = (highestLand - m_SeaLevel) * kFeetPerMetre;
                float bandFeet = math.max(kMinBandFeet, math.ceil(topFeet / kTints.Length / 100f) * 100f);
                m_BandFeet[0] = bandFeet;
                for (int i = 0; i < m_Bands.Length; i++)
                {
                    if (m_Bands[i] != kWater)
                    {
                        float feet = (m_Heights[i] - m_SeaLevel) * kFeetPerMetre;
                        m_Bands[i] = (byte)math.clamp((int)math.floor(feet / bandFeet), 0, kTints.Length - 1);
                    }
                }
                FindPeaks();
            }

            private void FindPeaks()
            {
                m_Peaks.Clear();
                float prominence = kProminenceFeet / kFeetPerMetre;
                NativeList<float3> candidates = new NativeList<float3>(64, Allocator.Temp);
                for (int z = kPeakRadius; z < kCells - kPeakRadius; z += 2)
                {
                    for (int x = kPeakRadius; x < kCells - kPeakRadius; x += 2)
                    {
                        int index = x + z * kCells;
                        if (m_Bands[index] == kWater)
                        {
                            continue;
                        }
                        float height = m_Heights[index];
                        bool top = true;
                        float ring = float.MaxValue;
                        for (int dz = -kPeakRadius; dz <= kPeakRadius && top; dz += kPeakStep)
                        {
                            for (int dx = -kPeakRadius; dx <= kPeakRadius; dx += kPeakStep)
                            {
                                float other = m_Heights[(x + dx) + (z + dz) * kCells];
                                if (other > height)
                                {
                                    top = false;
                                    break;
                                }
                                if (math.abs(dx) == kPeakRadius || math.abs(dz) == kPeakRadius)
                                {
                                    ring = math.min(ring, other);
                                }
                            }
                        }
                        if (top && height - ring >= prominence)
                        {
                            float3 position = CellCenter(x, z);
                            candidates.Add(new float3(position.x, height, position.z));
                        }
                    }
                }
                // Highest first, each at least kPeakSpacing from those kept.
                candidates.Sort(new HigherFirst());
                float spacing = kPeakSpacing * kPeakSpacing;
                for (int i = 0; i < candidates.Length; i++)
                {
                    bool clear = true;
                    for (int j = 0; j < m_Peaks.Length && clear; j++)
                    {
                        clear = math.distancesq(m_Peaks[j].xz, candidates[i].xz) >= spacing;
                    }
                    if (clear)
                    {
                        m_Peaks.Add(candidates[i]);
                    }
                }
                candidates.Dispose();
            }
        }

        private struct HigherFirst : IComparer<float3>
        {
            public int Compare(float3 a, float3 b) => b.y.CompareTo(a.y);
        }

        private static float3 CellCenter(int x, int z) => new float3((x + 0.5f) * kCellSize - kHalfExtent, 0f, (z + 0.5f) * kCellSize - kHalfExtent);

        private NativeArray<float> m_Heights;
        private NativeArray<byte> m_Bands;
        private NativeArray<float> m_MefTerrain;
        private NativeList<float3> m_Peaks;
        private NativeArray<float> m_BandFeet;
        private JobHandle m_Job;
        private bool m_Pending;
        private bool m_Started;
        private bool m_HasData;
        private readonly List<ChartRect> m_Rects = new List<ChartRect>();
        private readonly List<float3> m_Spots = new List<float3>();
        private readonly List<string> m_SpotTexts = new List<string>();
        private readonly float[] m_MefTerrainCopy = new float[kMefCells * kMefCells];

        public TerrainChartMap()
        {
            m_Heights = new NativeArray<float>(kCells * kCells, Allocator.Persistent);
            m_Bands = new NativeArray<byte>(kCells * kCells, Allocator.Persistent);
            m_MefTerrain = new NativeArray<float>(kMefCells * kMefCells, Allocator.Persistent);
            m_Peaks = new NativeList<float3>(32, Allocator.Persistent);
            m_BandFeet = new NativeArray<float>(1, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_Job.Complete();
            m_Heights.Dispose();
            m_Bands.Dispose();
            m_MefTerrain.Dispose();
            m_Peaks.Dispose();
            m_BandFeet.Dispose();
        }

        /// <summary>Forgets the terrain, so it is sampled again as soon as the infoview opens.</summary>
        public void Reset()
        {
            m_Started = false;
            m_HasData = false;
            m_Rects.Clear();
            m_Spots.Clear();
            m_SpotTexts.Clear();
        }

        /// <summary>The highest terrain of a MEF quadrant (x, z from the south-west), in metres, or false before the first sampling.</summary>
        public bool TryGetMefTerrain(int x, int z, out float height)
        {
            height = m_MefTerrainCopy[x + z * kMefCells];
            return m_HasData;
        }

        /// <summary>The south-west corner and size of the MEF quadrants.</summary>
        public static float2 MefOrigin => new float2(-kHalfExtent, -kHalfExtent);
        public static float MefSize => 2f * kHalfExtent / kMefCells;

        /// <summary>Collects a finished sampling, or starts a new one when due. Returns the job to add to the system's dependency, or default.</summary>
        public JobHandle Update(TerrainSystem terrainSystem, WaterSystem waterSystem, float seaLevel, JobHandle dependency)
        {
            if (m_Pending)
            {
                if (!m_Job.IsCompleted)
                {
                    return m_Job;
                }
                m_Job.Complete();
                m_Pending = false;
                Collect(seaLevel);
            }
            // Once per opening of the chart, as a chart is printed rather than kept live: reopen it to see changes.
            if (m_Started)
            {
                return default;
            }
            m_Started = true;
            WaterSurfaceData<SurfaceWater> water = waterSystem.GetSurfaceData(out JobHandle waterDependency);
            m_Job = new SampleJob
            {
                m_Terrain = terrainSystem.GetHeightData(),
                m_Water = water,
                m_SeaLevel = seaLevel,
                m_Heights = m_Heights,
                m_Bands = m_Bands,
                m_MefTerrain = m_MefTerrain,
                m_Peaks = m_Peaks,
                m_BandFeet = m_BandFeet,
            }.Schedule(JobHandle.CombineDependencies(dependency, waterDependency));
            terrainSystem.AddCPUHeightReader(m_Job);
            waterSystem.AddSurfaceReader(m_Job);
            m_Pending = true;
            return m_Job;
        }

        /// <summary>Rectangles of equal band (runs per row, merged down the rows), the spot elevations and the MEF terrain.</summary>
        private void Collect(float seaLevel)
        {
            RectMerger merger = new RectMerger(kCellSize, kHalfExtent, m_Rects);
            for (int z = 0; z < kCells; z++)
            {
                merger.BeginRow(z);
                int start = 0;
                float heightSum = 0f;
                for (int x = 0; x <= kCells; x++)
                {
                    int index = x + z * kCells;
                    if (x < kCells && x > start && m_Bands[index] == m_Bands[index - 1])
                    {
                        heightSum += m_Heights[index];
                        continue;
                    }
                    if (x > start)
                    {
                        byte band = m_Bands[start + z * kCells];
                        if (band != kWater)
                        {
                            merger.AddRun(start, x, band, heightSum);
                        }
                    }
                    if (x < kCells)
                    {
                        start = x;
                        heightSum = m_Heights[index];
                    }
                }
                merger.EndRow();
            }
            merger.Finish();
            m_Spots.Clear();
            m_SpotTexts.Clear();
            for (int i = 0; i < m_Peaks.Length; i++)
            {
                m_Spots.Add(m_Peaks[i]);
                float feet = math.max(0f, (m_Peaks[i].y - seaLevel) * kFeetPerMetre);
                m_SpotTexts.Add(((int)math.round(feet)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            m_MefTerrain.CopyTo(m_MefTerrainCopy);
            m_HasData = true;
        }

        /// <summary>The elevation tints (under everything else on the chart).</summary>
        public void DrawTints(OverlayRenderSystem.Buffer buffer)
        {
            for (int i = 0; i < m_Rects.Count; i++)
            {
                ChartRect rect = m_Rects[i];
                Color tint = kTints[rect.m_Band];
                // Square ends, as wide as the rectangle is tall, so neighbouring rectangles join without gaps or overlaps.
                buffer.DrawLine(tint, tint, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(rect.m_From, rect.m_To), rect.m_Width, new float2(0f, 0f));
            }
        }

        /// <summary>The spot elevations: a dot with the elevation to its upper right.</summary>
        public void DrawSpots(OverlayRenderSystem.Buffer buffer)
        {
            for (int i = 0; i < m_Spots.Count; i++)
            {
                float3 spot = m_Spots[i];
                buffer.DrawCircle(kSpotColor, kSpotColor, 0f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), spot, kSpotDot);
                ChartLabels.DrawText(buffer, spot.xz + new float2(0.9f * kSpotDot, 0.5f * kSpotDot), spot.y, m_SpotTexts[i], kSpotText, kSpotColor, 0.12f * kSpotText);
            }
        }
    }
}
