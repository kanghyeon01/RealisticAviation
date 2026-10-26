using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Streams on the VFR chart, as the paper chart draws them: water narrower than <see cref="kStreamWidth"/> as a single blue
    /// line along its middle instead of an outline (user, 2026-10-27: streams narrower than the terrain cells broke the
    /// shoreline into loops). The water is sampled on a grid of <see cref="kFineCell"/> cells; the wide water is where a disc
    /// of the stream width fits (the water opened by its half width), the rest of the water is narrow, and the narrow water
    /// is thinned to one cell (Zhang-Suen) and traced into smoothed lines.
    /// </summary>
    internal static class StreamLines
    {
        public const float kFineCell = 8f;
        private const float kHalfExtent = 7168f;
        public const int kFineCells = (int)(2f * kHalfExtent / kFineCell);

        /// <summary>Water up to this wide is a stream (user).</summary>
        public const float kStreamWidth = 40f;

        // Chamfer distances: 3 a side step, 4 a diagonal one (a unit is a third of a cell), held in a byte.
        private const float kUnit = kFineCell / 3f;
        private const byte kFar = 255;

        // Cell classes.
        public const byte kLand = 0;
        public const byte kWide = 1;
        public const byte kNarrow = 2;
        public const byte kCentre = 3;

        /// <summary>Spurs shorter than this many cells off a junction, and lone pieces shorter than this, are dropped.</summary>
        private const int kShortCells = 4;

        /// <summary>The middle of a fine cell.</summary>
        public static float2 CellCenter(int x, int z) => new float2((x + 0.5f) * kFineCell - kHalfExtent, (z + 0.5f) * kFineCell - kHalfExtent);

        /// <summary>
        /// On a worker thread: the water flags (1 water) into classes (land, wide, narrow, centre line of the narrow water).
        /// <paramref name="classes"/> holds the water flags on entry; <paramref name="distances"/> is scratch.
        /// </summary>
        public static void Classify(NativeArray<byte> classes, NativeArray<byte> distances, NativeList<int> narrow)
        {
            int cells = kFineCells;
            float half = kStreamWidth * 0.5f;
            // Distance of each water cell from the land; the cells a disc of the stream width fits round (the shore lies half
            // a cell beyond the land cell's middle).
            for (int i = 0; i < classes.Length; i++)
            {
                distances[i] = classes[i] != 0 ? kFar : (byte)0;
            }
            Chamfer(distances, cells);
            for (int i = 0; i < classes.Length; i++)
            {
                bool core = classes[i] != 0 && distances[i] * kUnit - kFineCell * 0.5f >= half;
                distances[i] = core ? (byte)0 : kFar;
            }
            // The wide water: within the half width of those cells.
            Chamfer(distances, cells);
            narrow.Clear();
            for (int i = 0; i < classes.Length; i++)
            {
                if (classes[i] == 0)
                {
                    continue;
                }
                if (distances[i] * kUnit <= half + kFineCell * 0.5f)
                {
                    classes[i] = kWide;
                }
                else
                {
                    classes[i] = kNarrow;
                    int x = i % cells, z = i / cells;
                    if (x > 0 && z > 0 && x < cells - 1 && z < cells - 1)
                    {
                        narrow.Add(i);
                    }
                }
            }
            Thin(classes, narrow, cells);
        }

        /// <summary>
        /// On a worker thread, after <see cref="Classify"/>: each piece of narrow water (its cells joined at sides or corners)
        /// whose water moves slower on average than <paramref name="minSpeed"/> is still water, a pond or an inlet, and is
        /// classed as wide (outlined, user: standing water came out as lines); the flowing pieces stay streams.
        /// </summary>
        public static void KeepFlowing(NativeArray<byte> classes, NativeArray<float> speeds, float minSpeed, NativeList<int> narrow)
        {
            int cells = kFineCells;
            NativeList<int> stack = new NativeList<int>(256, Allocator.Temp);
            NativeList<int> piece = new NativeList<int>(256, Allocator.Temp);
            for (int k = 0; k < narrow.Length; k++)
            {
                int seed = narrow[k];
                if (classes[seed] != kNarrow && classes[seed] != kCentre)
                {
                    continue;
                }
                // Flood the piece, marking its cells as measured on the way.
                stack.Clear();
                piece.Clear();
                stack.Add(seed);
                float speed = 0f;
                while (stack.Length > 0)
                {
                    int i = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    byte value = classes[i];
                    if (value != kNarrow && value != kCentre)
                    {
                        continue;
                    }
                    classes[i] = (byte)(value == kCentre ? kMeasuredCentre : kMeasuredNarrow);
                    piece.Add(i);
                    int x = i % cells, z = i / cells;
                    speed += speeds[i];
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, nz = z + dz;
                            if ((dx != 0 || dz != 0) && nx >= 0 && nz >= 0 && nx < cells && nz < cells)
                            {
                                stack.Add(nx + nz * cells);
                            }
                        }
                    }
                }
                // A still piece becomes wide water now; a flowing one keeps its marks until every piece is measured (put back
                // at once, it was flooded again from each of its cells).
                if (speed / math.max(1, piece.Length) < minSpeed)
                {
                    for (int p = 0; p < piece.Length; p++)
                    {
                        classes[piece[p]] = kWide;
                    }
                }
            }
            for (int k = 0; k < narrow.Length; k++)
            {
                int i = narrow[k];
                classes[i] = classes[i] == kMeasuredCentre ? kCentre : classes[i] == kMeasuredNarrow ? kNarrow : classes[i];
            }
            stack.Dispose();
            piece.Dispose();
        }

        // Marks of measured cells of flowing pieces.
        private const byte kMeasuredNarrow = 4;
        private const byte kMeasuredCentre = 5;

        private static void Chamfer(NativeArray<byte> distances, int cells)
        {
            for (int z = 0; z < cells; z++)
            {
                for (int x = 0; x < cells; x++)
                {
                    int i = x + z * cells;
                    int d = distances[i];
                    if (d == 0)
                    {
                        continue;
                    }
                    if (x > 0) d = math.min(d, distances[i - 1] + 3);
                    if (z > 0)
                    {
                        d = math.min(d, distances[i - cells] + 3);
                        if (x > 0) d = math.min(d, distances[i - cells - 1] + 4);
                        if (x < cells - 1) d = math.min(d, distances[i - cells + 1] + 4);
                    }
                    distances[i] = (byte)math.min(d, kFar);
                }
            }
            for (int z = cells - 1; z >= 0; z--)
            {
                for (int x = cells - 1; x >= 0; x--)
                {
                    int i = x + z * cells;
                    int d = distances[i];
                    if (d == 0)
                    {
                        continue;
                    }
                    if (x < cells - 1) d = math.min(d, distances[i + 1] + 3);
                    if (z < cells - 1)
                    {
                        d = math.min(d, distances[i + cells] + 3);
                        if (x < cells - 1) d = math.min(d, distances[i + cells + 1] + 4);
                        if (x > 0) d = math.min(d, distances[i + cells - 1] + 4);
                    }
                    distances[i] = (byte)math.min(d, kFar);
                }
            }
        }

        /// <summary>Zhang-Suen thinning of the narrow cells; what is left is marked as the centre line.</summary>
        private static void Thin(NativeArray<byte> classes, NativeList<int> narrow, int cells)
        {
            // Working flags: narrow cells still in the shape are kCentre, removed ones go back to kNarrow.
            for (int i = 0; i < narrow.Length; i++)
            {
                classes[narrow[i]] = kCentre;
            }
            NativeList<int> remove = new NativeList<int>(256, Allocator.Temp);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int pass = 0; pass < 2; pass++)
                {
                    remove.Clear();
                    for (int k = 0; k < narrow.Length; k++)
                    {
                        int i = narrow[k];
                        if (classes[i] != kCentre)
                        {
                            continue;
                        }
                        // Neighbours clockwise from north: p2 (z+1), p3 (x+1, z+1), p4 (x+1), ... p9 (x-1, z+1).
                        int p2 = On(classes, i + cells), p3 = On(classes, i + cells + 1), p4 = On(classes, i + 1), p5 = On(classes, i - cells + 1);
                        int p6 = On(classes, i - cells), p7 = On(classes, i - cells - 1), p8 = On(classes, i - 1), p9 = On(classes, i + cells - 1);
                        int count = p2 + p3 + p4 + p5 + p6 + p7 + p8 + p9;
                        if (count < 2 || count > 6)
                        {
                            continue;
                        }
                        int rises = (p2 == 0 && p3 == 1 ? 1 : 0) + (p3 == 0 && p4 == 1 ? 1 : 0) + (p4 == 0 && p5 == 1 ? 1 : 0) + (p5 == 0 && p6 == 1 ? 1 : 0)
                            + (p6 == 0 && p7 == 1 ? 1 : 0) + (p7 == 0 && p8 == 1 ? 1 : 0) + (p8 == 0 && p9 == 1 ? 1 : 0) + (p9 == 0 && p2 == 1 ? 1 : 0);
                        if (rises != 1)
                        {
                            continue;
                        }
                        bool remove1 = pass == 0 ? p2 * p4 * p6 == 0 && p4 * p6 * p8 == 0 : p2 * p4 * p8 == 0 && p2 * p6 * p8 == 0;
                        if (remove1)
                        {
                            remove.Add(i);
                        }
                    }
                    for (int k = 0; k < remove.Length; k++)
                    {
                        classes[remove[k]] = kNarrow;
                    }
                    changed |= remove.Length > 0;
                }
            }
            remove.Dispose();
        }

        private static int On(NativeArray<byte> classes, int i) => classes[i] == kCentre ? 1 : 0;

        /// <summary>On a worker thread, after <see cref="KeepFlowing"/>: the centre cells of the streams, for <see cref="Trace"/>.</summary>
        public static void Centres(NativeArray<byte> classes, NativeList<int> narrow, NativeList<int> centre)
        {
            centre.Clear();
            for (int k = 0; k < narrow.Length; k++)
            {
                if (classes[narrow[k]] == kCentre)
                {
                    centre.Add(narrow[k]);
                }
            }
        }

        /// <summary>
        /// On a worker thread: the shoreline on the fine grid (where the depth crosses <paramref name="waterDepth"/>), less the
        /// pieces by streams (<see cref="ByStreamOnly"/>), as segments (x, z of each end).
        /// </summary>
        public static void Shore(NativeArray<float> depths, NativeArray<byte> classes, float waterDepth, int streamCells, NativeList<float4> shore)
        {
            shore.Clear();
            NativeList<float4> all = new NativeList<float4>(4096, Allocator.Temp);
            ContourLines.Trace(depths, kFineCells, kFineCell, new float2(-kHalfExtent, -kHalfExtent), waterDepth, all);
            for (int i = 0; i < all.Length; i++)
            {
                float4 segment = all[i];
                if (!ByStreamOnly(classes, (segment.xy + segment.zw) * 0.5f, streamCells))
                {
                    shore.Add(segment);
                }
            }
            all.Dispose();
        }

        /// <summary>
        /// On the main thread: the centre lines into smoothed polylines, as pairs of segment ends (x, z), the heights left to
        /// the caller. The lines are walked from their ends and junctions, short spurs and pieces dropped, and each line
        /// smoothed. <paramref name="centre"/>: the centre cells (<see cref="Centres"/>).
        /// </summary>
        public static void Trace(NativeArray<byte> classes, NativeList<int> centre, List<float4> segments)
        {
            segments.Clear();
            int cells = kFineCells;
            HashSet<long> walked = new HashSet<long>();
            List<int> line = new List<int>();
            List<float2> points = new List<float2>();
            // From every end and junction, then round the loops left.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int k = 0; k < centre.Length; k++)
                {
                    int start = centre[k];
                    int degree = Degree(classes, start, cells);
                    if (pass == 0 ? degree == 2 : degree != 2)
                    {
                        continue;
                    }
                    if (degree == 0)
                    {
                        continue;
                    }
                    foreach (int next in Neighbours(classes, start, cells))
                    {
                        if (walked.Contains(Edge(start, next)))
                        {
                            continue;
                        }
                        line.Clear();
                        line.Add(start);
                        int previous = start, current = next;
                        walked.Add(Edge(previous, current));
                        line.Add(current);
                        while (current != start && Degree(classes, current, cells) == 2)
                        {
                            int following = -1;
                            foreach (int n in Neighbours(classes, current, cells))
                            {
                                if (n != previous && !walked.Contains(Edge(current, n)))
                                {
                                    following = n;
                                    break;
                                }
                            }
                            if (following < 0)
                            {
                                break;
                            }
                            walked.Add(Edge(current, following));
                            line.Add(following);
                            previous = current;
                            current = following;
                        }
                        bool startFree = Degree(classes, line[0], cells) == 1, endFree = Degree(classes, line[line.Count - 1], cells) == 1;
                        if ((startFree || endFree) && line.Count < kShortCells)
                        {
                            continue;
                        }
                        Add(line, points, segments, cells);
                    }
                }
            }
        }

        /// <summary>A line smoothed (two passes of 1-2-1 averaging, the ends kept) into segments.</summary>
        private static void Add(List<int> line, List<float2> points, List<float4> segments, int cells)
        {
            points.Clear();
            for (int k = 0; k < line.Count; k++)
            {
                points.Add(CellCenter(line[k] % cells, line[k] / cells));
            }
            for (int pass = 0; pass < 2; pass++)
            {
                float2 before = points[0];
                for (int k = 1; k < points.Count - 1; k++)
                {
                    float2 here = points[k];
                    points[k] = (before + here * 2f + points[k + 1]) * 0.25f;
                    before = here;
                }
            }
            for (int k = 0; k + 1 < points.Count; k++)
            {
                segments.Add(new float4(points[k], points[k + 1]));
            }
        }

        private static long Edge(int a, int b) => a < b ? (long)a * int.MaxValue + b : (long)b * int.MaxValue + a;

        private static bool IsCentre(NativeArray<byte> classes, int i, int cells, int from)
        {
            if (i < 0 || i >= classes.Length || math.abs(i % cells - from % cells) > 1)
            {
                return false;
            }
            return classes[i] == kCentre;
        }

        private static int Degree(NativeArray<byte> classes, int i, int cells)
        {
            int count = 0;
            foreach (int n in Neighbours(classes, i, cells))
            {
                count++;
            }
            return count;
        }

        /// <summary>
        /// The centre cells joined to a cell: those at its sides, and a diagonal one only where neither cell between them is
        /// a centre cell (otherwise the step goes through that cell; this keeps stair steps from looking like junctions).
        /// </summary>
        private static IEnumerable<int> Neighbours(NativeArray<byte> classes, int i, int cells)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                    {
                        continue;
                    }
                    int n = i + dx + dz * cells;
                    if (!IsCentre(classes, n, cells, i))
                    {
                        continue;
                    }
                    if (dx != 0 && dz != 0 && (IsCentre(classes, i + dx, cells, i) || IsCentre(classes, i + dz * cells, cells, i)))
                    {
                        continue;
                    }
                    yield return n;
                }
            }
        }

        /// <summary>
        /// Whether a point lies by a stream and not by wide water, so a shoreline piece there is left out (the stream's line
        /// stands for it): narrow water within <paramref name="radius"/> cells and no wide water.
        /// </summary>
        public static bool ByStreamOnly(NativeArray<byte> classes, float2 position, int radius)
        {
            int2 cell = (int2)math.floor((position + kHalfExtent) / kFineCell);
            bool stream = false;
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int2 c = cell + new int2(dx, dz);
                    if (math.any(c < 0) || math.any(c >= kFineCells))
                    {
                        continue;
                    }
                    byte value = classes[c.x + c.y * kFineCells];
                    if (value == kWide)
                    {
                        return false;
                    }
                    stream |= value == kNarrow || value == kCentre;
                }
            }
            return stream;
        }
    }
}
