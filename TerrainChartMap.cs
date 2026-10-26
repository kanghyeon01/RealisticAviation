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
    /// in <see cref="kBands"/> steps from green at sea level to brown at the top. The real chart's bands are thousands of feet
    /// apart, more than a game map rises, so the bands are fitted to the map: sea level to its highest land in equal
    /// steps of whole hundreds of feet, at least <see cref="kMinBandFeet"/> each. Water is left uncoloured;</item>
    /// <item>spot elevations: peaks that stand at least <see cref="kProminenceFeet"/> above the ground about 1 km round them,
    /// at least <see cref="kPeakSpacing"/> apart, highest first, as a dot with the elevation;</item>
    /// <item>Maximum Elevation Figures: the area cut into <see cref="kMefCells"/> × <see cref="kMefCells"/> quadrants; this
    /// class gives each one's highest terrain, <see cref="AirspaceOverlaySystem"/> adds the obstacles.</item>
    /// </list>
    /// Sampled on a worker thread once each time the infoview opens; the tints go into the terrain'"'"'s infoview colours
    /// (<see cref="VfrChartTintSystem"/>), as the game'"'"'s heatmaps, so they are smooth and don'"'"'t flicker.
    /// </summary>
    internal sealed class TerrainChartMap
    {
        private const float kCellSize = 32f;
        private const float kHalfExtent = 7168f;
        internal const int kCells = (int)(2f * kHalfExtent / kCellSize);
        private const float kFeetPerMetre = 3.28084f;
        private const byte kWater = 255;

        /// <summary>
        /// A cell is water from this depth (and the shoreline is drawn there). Shallow: rivers are mostly shallower than half a
        /// metre, and at 0.5 m only their deeper pools counted, so the shoreline broke into pieces inside the water (user).
        /// </summary>
        private const float kWaterDepth = 0.1f;

        public const int kMefCells = 4;

        // Elevation tints: the chart's eight (the user's legend, sea level to 12,000 ft and over), coloured into the terrain
        // in three groups, one terrain colour entry each (the shader takes two exact colours per entry once cells outside
        // its group are left uncoloured, plus any colour between them; the groups below). The least height of one band.
        public const int kBands = 8;
        private const float kMinBandFeet = 200f;
        private static readonly Color[] kTints =
        {
            Tint(0xde, 0xea, 0xc5), Tint(0xd0, 0xe1, 0xb1), Tint(0xfd, 0xf7, 0xdc), Tint(0xf8, 0xeb, 0xb9),
            Tint(0xf4, 0xd5, 0x8b), Tint(0xe3, 0xbd, 0x9a), Tint(0xf1, 0xb4, 0x7d), Tint(0xb7, 0x8e, 0x3f),
        };

        // The ground has four colour channels (one is the built-up areas'), so not every band can have an entry of its own;
        // each group's entry gives two exact tints (at half and full), six in all, and the other two bands are blends
        // between their group's two (in linear light, as the shader blends). The groups run in order (sea level-2,000,
        // 2,000-7,000, 7,000 and over), each band's tint gain from the calibration file on its own: 3,000-5,000 between
        // 2,000-3,000 and 5,000-7,000 (CIE76 difference 2.9) and 9,000-12,000 between 7,000-9,000 and 12,000+ (12.1; user,
        // 2026-10-27: looked closest of the layouts that keep the greens apart, a blend with a green took the green's gain
        // and came out lemon).

        /// <summary>Each band's group, and its value in the group's channel (128: the group's first tint, 255: its last).</summary>
        private static readonly int[] kBandGroup = { 0, 0, 1, 1, 1, 2, 2, 2 };
        private static readonly byte[] kBandValue = { 128, 255, 128, 195, 255, 128, 192, 255 };

        /// <summary>Each group's first tint (half) and last (full), as bands.</summary>
        private static readonly int[] kGroupFirst = { 0, 2, 5 };
        private static readonly int[] kGroupLast = { 1, 4, 7 };

        public const int kGroups = 3;

        /// <summary>A band's tint (opaque).</summary>
        public static Color BandTint(int band) => kTints[band];

        /// <summary>The colours a group's entry gives the shader: its first tint and its last.</summary>
        public static Color GroupFirst(int group) => kTints[kGroupFirst[group]];

        public static Color GroupLast(int group) => kTints[kGroupLast[group]];

        /// <summary>The bands of a group's first and last tint.</summary>
        public static int GroupFirstBand(int group) => kGroupFirst[group];

        public static int GroupLastBand(int group) => kGroupLast[group];
        // Peaks: the search radius and step (in cells), the least rise over the surrounding ring, and the spacing.
        private const int kPeakRadius = 32;
        private const int kPeakStep = 4;
        private const float kProminenceFeet = 100f;
        private const float kPeakSpacing = 1500f;

        private static readonly Color kSpotColor = new Color(0.1f, 0.1f, 0.1f, 1f);
        private const float kSpotDot = 20f;
        private const float kSpotText = 45f;

        private static Color Tint(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f, 1f);

        /// <summary>
        /// The terrain heights and water depths of the cells, and the water depths and speeds of the streams' finer grid, one
        /// row a time on every worker: the terrain and water systems wait for this before changing their data, so it must
        /// be short (one long job held up the game's frames each time the chart opened, user).
        /// </summary>
        private struct SampleRowsJob : IJobParallelFor
        {
            [ReadOnly] public TerrainHeightData m_Terrain;
            [ReadOnly] public WaterSurfaceData<SurfaceWater> m_Water;
            [NativeDisableParallelForRestriction] public NativeArray<float> m_Heights;
            [NativeDisableParallelForRestriction] public NativeArray<float> m_Depths;
            [NativeDisableParallelForRestriction] public NativeArray<float> m_FineDepths;
            [NativeDisableParallelForRestriction] public NativeArray<float> m_FineSpeeds;

            public void Execute(int row)
            {
                if (row < kCells)
                {
                    for (int x = 0; x < kCells; x++)
                    {
                        float3 position = CellCenter(x, row);
                        position.y = TerrainUtils.SampleHeight(ref m_Terrain, position);
                        int index = x + row * kCells;
                        m_Heights[index] = position.y;
                        m_Depths[index] = WaterUtils.SampleDepth(ref m_Water, position);
                    }
                    return;
                }
                int z = row - kCells;
                for (int x = 0; x < StreamLines.kFineCells; x++)
                {
                    float2 centre = StreamLines.CellCenter(x, z);
                    float3 position = new float3(centre.x, 0f, centre.y);
                    position.y = TerrainUtils.SampleHeight(ref m_Terrain, position);
                    float depth = WaterUtils.SampleDepth(ref m_Water, position);
                    int index = x + z * StreamLines.kFineCells;
                    m_FineDepths[index] = depth;
                    m_FineSpeeds[index] = depth > kWaterDepth ? math.length(WaterUtils.SampleVelocity(ref m_Water, position)) : 0f;
                }
            }
        }

        /// <summary>What the chart takes from the samples: bands, MEF terrain, peaks, the shoreline and the streams.</summary>
        private struct ProcessJob : IJob
        {
            public float m_SeaLevel;
            public NativeArray<float> m_Heights;

            /// <summary>Water depth at each cell centre (the shoreline).</summary>
            public NativeArray<float> m_Depths;
            public NativeArray<byte> m_Bands;
            public NativeArray<float> m_MefTerrain;
            public NativeList<float3> m_Peaks;

            /// <summary>The band height chosen for the map, in feet (one entry).</summary>
            public NativeArray<float> m_BandFeet;

            /// <summary>The water on the streams' finer grid, classed (StreamLines), with scratch.</summary>
            public NativeArray<byte> m_FineClasses;
            public NativeArray<byte> m_FineDistances;
            public NativeList<int> m_Narrow;
            [ReadOnly] public NativeArray<float> m_FineDepths;
            [ReadOnly] public NativeArray<float> m_FineSpeeds;
            public float m_MinStreamSpeed;
            public NativeList<int> m_Centre;
            public NativeList<float4> m_Shore;

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
                        int index = x + z * kCells;
                        float height = m_Heights[index];
                        bool water = m_Depths[index] > kWaterDepth;
                        m_Bands[index] = water ? kWater : (byte)0;
                        if (!water)
                        {
                            highestLand = math.max(highestLand, height);
                        }
                        int mef = math.min(x / perMef, kMefCells - 1) + math.min(z / perMef, kMefCells - 1) * kMefCells;
                        m_MefTerrain[mef] = math.max(m_MefTerrain[mef], height);
                    }
                }
                // kBands bands from sea level to the highest land, each a whole number of hundreds of feet and not too thin.
                float topFeet = (highestLand - m_SeaLevel) * kFeetPerMetre;
                float bandFeet = math.max(kMinBandFeet, math.ceil(topFeet / kBands / 100f) * 100f);
                m_BandFeet[0] = bandFeet;
                for (int i = 0; i < m_Bands.Length; i++)
                {
                    if (m_Bands[i] != kWater)
                    {
                        float feet = (m_Heights[i] - m_SeaLevel) * kFeetPerMetre;
                        m_Bands[i] = (byte)math.clamp((int)math.floor(feet / bandFeet), 0, kBands - 1);
                    }
                }
                FindPeaks();
                // The water on the finer grid: streams, and the shoreline (less the pieces by streams).
                for (int i = 0; i < m_FineClasses.Length; i++)
                {
                    m_FineClasses[i] = m_FineDepths[i] > kWaterDepth ? (byte)1 : (byte)0;
                }
                StreamLines.Classify(m_FineClasses, m_FineDistances, m_Narrow);
                StreamLines.KeepFlowing(m_FineClasses, m_FineSpeeds, m_MinStreamSpeed, m_Narrow);
                StreamLines.Centres(m_FineClasses, m_Narrow, m_Centre);
                StreamLines.Shore(m_FineDepths, m_FineClasses, kWaterDepth, kShoreStreamCells, m_Shore);
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
        private NativeArray<float> m_Depths;
        private readonly float[] m_DepthCopy = new float[kCells * kCells];
        private readonly float[] m_HeightCopy = new float[kCells * kCells];
        private readonly byte[] m_BandCopy = new byte[kCells * kCells];
        private readonly List<float4> m_Contour = new List<float4>();

        /// <summary>The shoreline as pairs of segment ends.</summary>
        private readonly List<float3> m_Shore = new List<float3>();

        // Streams (StreamLines): the water classed on the finer grid, the lines as pairs of segment ends, their colour.
        private NativeArray<byte> m_FineClasses;
        private NativeArray<byte> m_FineDistances;
        private NativeList<int> m_Narrow;
        private NativeArray<float> m_FineDepths;
        private NativeArray<float> m_FineSpeeds;
        private NativeList<int> m_Centre;
        private NativeList<float4> m_ShoreSegments;

        /// <summary>
        /// Narrow water moving slower than this on average is still (outlined). Measured on the user's map (2026-10-27): ponds
        /// and inlets 0 to 0.28, streams from 2.9 up.
        /// </summary>
        private const float kMinStreamSpeed = 1f;
        private readonly List<float3> m_Streams = new List<float3>();

        /// <summary>A stream's line: the paper chart's blue, darker than the water's (user).</summary>
        private static readonly Color kStreamColor = new Color(0x4a / 255f, 0x7f / 255f, 0xc0 / 255f, 1f);

        /// <summary>Shoreline pieces with stream water within this many fine cells and no wide water are left out.</summary>
        private const int kShoreStreamCells = 2;
        private NativeArray<byte> m_Bands;
        private NativeArray<float> m_MefTerrain;
        private NativeList<float3> m_Peaks;
        private NativeArray<float> m_BandFeet;
        private float m_SeaLevel;

        /// <summary>The band height of the last sampling, in metres (for writing the tints off the main thread).</summary>
        private float m_BandMetres;
        private JobHandle m_Job;
        private bool m_Pending;
        private bool m_Started;
        private bool m_HasData;
        private readonly List<float3> m_Spots = new List<float3>();
        private readonly List<string> m_SpotTexts = new List<string>();
        private readonly float[] m_MefTerrainCopy = new float[kMefCells * kMefCells];

        public TerrainChartMap()
        {
            m_Heights = new NativeArray<float>(kCells * kCells, Allocator.Persistent);
            m_Depths = new NativeArray<float>(kCells * kCells, Allocator.Persistent);
            m_Bands = new NativeArray<byte>(kCells * kCells, Allocator.Persistent);
            m_MefTerrain = new NativeArray<float>(kMefCells * kMefCells, Allocator.Persistent);
            m_Peaks = new NativeList<float3>(32, Allocator.Persistent);
            m_BandFeet = new NativeArray<float>(1, Allocator.Persistent);
            m_FineClasses = new NativeArray<byte>(StreamLines.kFineCells * StreamLines.kFineCells, Allocator.Persistent);
            m_FineDistances = new NativeArray<byte>(StreamLines.kFineCells * StreamLines.kFineCells, Allocator.Persistent);
            m_Narrow = new NativeList<int>(1024, Allocator.Persistent);
            m_FineDepths = new NativeArray<float>(StreamLines.kFineCells * StreamLines.kFineCells, Allocator.Persistent);
            m_FineSpeeds = new NativeArray<float>(StreamLines.kFineCells * StreamLines.kFineCells, Allocator.Persistent);
            m_Centre = new NativeList<int>(1024, Allocator.Persistent);
            m_ShoreSegments = new NativeList<float4>(4096, Allocator.Persistent);
        }

        public void Dispose()
        {
            m_Job.Complete();
            m_Heights.Dispose();
            m_Depths.Dispose();
            m_Bands.Dispose();
            m_MefTerrain.Dispose();
            m_Peaks.Dispose();
            m_BandFeet.Dispose();
            m_FineClasses.Dispose();
            m_FineDistances.Dispose();
            m_Narrow.Dispose();
            m_FineDepths.Dispose();
            m_FineSpeeds.Dispose();
            m_Centre.Dispose();
            m_ShoreSegments.Dispose();
        }

        /// <summary>Forgets the terrain, so it is sampled again as soon as the infoview opens.</summary>
        public void Reset()
        {
            m_Started = false;
            m_HasData = false;
            m_Spots.Clear();
            m_SpotTexts.Clear();
            m_Shore.Clear();
            m_Streams.Clear();
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

        /// <summary>Collects a finished sampling, or starts a new one when due. Returns default: the jobs read no entities.</summary>
        public JobHandle Update(TerrainSystem terrainSystem, WaterSystem waterSystem, float seaLevel, JobHandle dependency)
        {
            if (m_Pending)
            {
                if (!m_Job.IsCompleted)
                {
                    return default;
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
            // The sampling holds the terrain and the water (their systems wait for it before changing them), so it runs on
            // every worker, row by row, and is short; the rest is worked out after it without holding them, and is not part
            // of the system's dependency (it reads no entities), so nothing waits for it.
            WaterSurfaceData<SurfaceWater> water = waterSystem.GetSurfaceData(out JobHandle waterDependency);
            JobHandle sampled = new SampleRowsJob
            {
                m_Terrain = terrainSystem.GetHeightData(),
                m_Water = water,
                m_Heights = m_Heights,
                m_Depths = m_Depths,
                m_FineDepths = m_FineDepths,
                m_FineSpeeds = m_FineSpeeds,
            }.Schedule(kCells + StreamLines.kFineCells, 8, waterDependency);
            terrainSystem.AddCPUHeightReader(sampled);
            waterSystem.AddSurfaceReader(sampled);
            m_Job = new ProcessJob
            {
                m_SeaLevel = seaLevel,
                m_Heights = m_Heights,
                m_Depths = m_Depths,
                m_Bands = m_Bands,
                m_MefTerrain = m_MefTerrain,
                m_Peaks = m_Peaks,
                m_BandFeet = m_BandFeet,
                m_FineClasses = m_FineClasses,
                m_FineDistances = m_FineDistances,
                m_Narrow = m_Narrow,
                m_FineDepths = m_FineDepths,
                m_FineSpeeds = m_FineSpeeds,
                m_MinStreamSpeed = kMinStreamSpeed,
                m_Centre = m_Centre,
                m_Shore = m_ShoreSegments,
            }.Schedule(sampled);
            m_Pending = true;
            return default;
        }

        /// <summary>The spot elevations and the MEF terrain; the bands are kept for <see cref="WriteTints"/>.</summary>
        private void Collect(float seaLevel)
        {
            m_SeaLevel = seaLevel;
            m_BandMetres = m_BandFeet[0] / kFeetPerMetre;
            // The cell depths, for IsWater.
            m_Depths.CopyTo(m_DepthCopy);
            // Managed copies for writing the tint texture (much faster to index than native arrays).
            m_Heights.CopyTo(m_HeightCopy);
            m_Bands.CopyTo(m_BandCopy);
            // The shoreline (traced on the fine grid in the job, less the pieces by streams: their line stands for it).
            m_Shore.Clear();
            for (int i = 0; i < m_ShoreSegments.Length; i++)
            {
                float4 segment = m_ShoreSegments[i];
                float y = HeightAt((segment.xy + segment.zw) * 0.5f);
                m_Shore.Add(new float3(segment.x, y, segment.y));
                m_Shore.Add(new float3(segment.z, y, segment.w));
            }
            StreamLines.Trace(m_FineClasses, m_Centre, m_Contour);
            m_Streams.Clear();
            for (int i = 0; i < m_Contour.Count; i++)
            {
                float4 segment = m_Contour[i];
                float y = HeightAt((segment.xy + segment.zw) * 0.5f);
                m_Streams.Add(new float3(segment.x, y, segment.y));
                m_Streams.Add(new float3(segment.z, y, segment.w));
            }
            m_Spots.Clear();
            m_SpotTexts.Clear();
            for (int i = 0; i < m_Peaks.Length; i++)
            {
                m_Spots.Add(m_Peaks[i]);
                float feet = math.max(0f, (m_Peaks[i].y - seaLevel) * kFeetPerMetre);
                m_SpotTexts.Add(((int)math.round(feet)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            m_MefTerrain.CopyTo(m_MefTerrainCopy);
            Version++;
            m_HasData = true;
        }

        /// <summary>Counts finished samplings, so a texture made from them knows when to be written again.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Texels per cell of the tint texture: band edges are drawn crisp (point filtering) on a finer grid, each texel's band
        /// from the height interpolated there, so the contours are smooth. (Filtering between cells made a group's channel
        /// fall from its last tint to nothing through its other tints: a pale stripe at every group edge, user.) The shader
        /// filters between texels anyway, so where a group's full tint meets another group the channel still passes its
        /// half tint over one texel (a pale ring round the brown peaks, user, 2026-10-27; no pairing of the bands avoids it
        /// without blending the orange): six texels a cell keep that ring about 5 m wide.
        /// </summary>
        public const int kTexelsPerCell = 6;

        public const int kTexels = kCells * kTexelsPerCell;

        /// <summary>
        /// Each texel's band into its group's channel of an RGBA texture of <see cref="kTexels"/>² texels (channels: one per
        /// group, -1 to leave a group out), as the group's entry reads it: 0 uncoloured, half its first tint, full its last,
        /// a blended band in between. Water is left uncoloured.
        /// </summary>
        public void WriteTints(byte[] texture, int first, int second, int third)
        {
            if (!m_HasData)
            {
                return;
            }
            float bandMetres = m_BandMetres;
            for (int tz = 0; tz < kTexels; tz++)
            {
                float cz = (tz + 0.5f) / kTexelsPerCell - 0.5f;
                int z0 = math.clamp((int)math.floor(cz), 0, kCells - 1), z1 = math.min(z0 + 1, kCells - 1);
                float fz = math.saturate(cz - z0);
                int nearestZ = math.clamp(tz / kTexelsPerCell, 0, kCells - 1);
                for (int tx = 0; tx < kTexels; tx++)
                {
                    if (m_BandCopy[tx / kTexelsPerCell + nearestZ * kCells] == kWater)
                    {
                        continue;
                    }
                    float cx = (tx + 0.5f) / kTexelsPerCell - 0.5f;
                    int x0 = math.clamp((int)math.floor(cx), 0, kCells - 1), x1 = math.min(x0 + 1, kCells - 1);
                    float fx = math.saturate(cx - x0);
                    float height = math.lerp(math.lerp(m_HeightCopy[x0 + z0 * kCells], m_HeightCopy[x1 + z0 * kCells], fx),
                        math.lerp(m_HeightCopy[x0 + z1 * kCells], m_HeightCopy[x1 + z1 * kCells], fx), fz);
                    int band = math.clamp((int)math.floor((height - m_SeaLevel) / bandMetres), 0, kBands - 1);
                    int group = kBandGroup[band];
                    int channel = group == 0 ? first : group == 1 ? second : third;
                    if (channel < 0)
                    {
                        continue;
                    }
                    texture[(tx + tz * kTexels) * 4 + channel] = kBandValue[band];
                }
            }
        }

        /// <summary>The ground height of the cell a point is in.</summary>
        private float HeightAt(float2 position)
        {
            int2 cell = math.clamp((int2)math.floor((position + kHalfExtent) / kCellSize), 0, kCells - 1);
            return m_Heights[cell.x + cell.y * kCells];
        }

        /// <summary>Whether a point is on water, by the sampled depths interpolated there (false before the first sampling).</summary>
        public bool IsWater(float2 position)
        {
            if (!m_HasData)
            {
                return false;
            }
            float2 cell = (position + kHalfExtent) / kCellSize - 0.5f;
            int x0 = math.clamp((int)math.floor(cell.x), 0, kCells - 1), z0 = math.clamp((int)math.floor(cell.y), 0, kCells - 1);
            int x1 = math.min(x0 + 1, kCells - 1), z1 = math.min(z0 + 1, kCells - 1);
            float fx = math.saturate(cell.x - x0), fz = math.saturate(cell.y - z0);
            float depth = math.lerp(math.lerp(m_DepthCopy[x0 + z0 * kCells], m_DepthCopy[x1 + z0 * kCells], fx),
                math.lerp(m_DepthCopy[x0 + z1 * kCells], m_DepthCopy[x1 + z1 * kCells], fx), fz);
            return depth > kWaterDepth;
        }

        /// <summary>The streams: a blue line along each, as on the chart.</summary>
        public void DrawStreams(OverlayRenderSystem.Buffer buffer, float width)
        {
            for (int i = 0; i + 1 < m_Streams.Count; i += 2)
            {
                buffer.DrawLine(kStreamColor, kStreamColor, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(m_Streams[i], m_Streams[i + 1]), width, new float2(1f, 1f));
            }
        }

        /// <summary>The shoreline: a hairline round the water, as on the chart.</summary>
        public void DrawShore(OverlayRenderSystem.Buffer buffer, Color color, float width)
        {
            for (int i = 0; i + 1 < m_Shore.Count; i += 2)
            {
                buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(m_Shore[i], m_Shore[i + 1]), width, new float2(1f, 1f));
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
