using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Contour lines through a grid of values (marching squares): where the values, interpolated between the cell centres,
    /// cross a threshold. For the VFR chart's hairlines along the shoreline and round the built-up areas.
    /// </summary>
    internal static class ContourLines
    {
        /// <summary>
        /// The pieces of the contour at <paramref name="threshold"/> of a square grid of <paramref name="cells"/>² values
        /// (row by row, cell centres <paramref name="cellSize"/> apart from <paramref name="origin"/> + half a cell), as
        /// segments (x, z of each end) added to <paramref name="segments"/>.
        /// </summary>
        public static void Trace(float[] values, int cells, float cellSize, float2 origin, float threshold, List<float4> segments)
        {
            Trace(new ManagedGrid { m_Values = values }, cells, cellSize, origin, threshold, new ListOut { m_Segments = segments });
        }

        /// <summary>As above, from a native grid into a native list (for jobs).</summary>
        public static void Trace(NativeArray<float> values, int cells, float cellSize, float2 origin, float threshold, NativeList<float4> segments)
        {
            Trace(new NativeGrid { m_Values = values }, cells, cellSize, origin, threshold, new NativeOut { m_Segments = segments });
        }

        private interface IGrid
        {
            float Get(int i);
        }

        private interface IOut
        {
            void Add(float4 segment);
        }

        private struct ManagedGrid : IGrid
        {
            public float[] m_Values;
            public float Get(int i) => m_Values[i];
        }

        private struct NativeGrid : IGrid
        {
            public NativeArray<float> m_Values;
            public float Get(int i) => m_Values[i];
        }

        private struct ListOut : IOut
        {
            public List<float4> m_Segments;
            public void Add(float4 segment) => m_Segments.Add(segment);
        }

        private struct NativeOut : IOut
        {
            public NativeList<float4> m_Segments;
            public void Add(float4 segment) => m_Segments.Add(segment);
        }

        private static void Trace<TGrid, TOut>(TGrid values, int cells, float cellSize, float2 origin, float threshold, TOut segments)
            where TGrid : struct, IGrid where TOut : struct, IOut
        {
            for (int z = 0; z < cells - 1; z++)
            {
                for (int x = 0; x < cells - 1; x++)
                {
                    float v00 = values.Get(x + z * cells), v10 = values.Get(x + 1 + z * cells);
                    float v01 = values.Get(x + (z + 1) * cells), v11 = values.Get(x + 1 + (z + 1) * cells);
                    int index = (v00 >= threshold ? 1 : 0) | (v10 >= threshold ? 2 : 0) | (v11 >= threshold ? 4 : 0) | (v01 >= threshold ? 8 : 0);
                    if (index == 0 || index == 15)
                    {
                        continue;
                    }
                    // The crossing on each side of the square: bottom (00-10), right (10-11), top (01-11), left (00-01).
                    float2 corner = origin + new float2((x + 0.5f) * cellSize, (z + 0.5f) * cellSize);
                    float2 bottom = corner + new float2(Cross(v00, v10, threshold) * cellSize, 0f);
                    float2 right = corner + new float2(cellSize, Cross(v10, v11, threshold) * cellSize);
                    float2 top = corner + new float2(Cross(v01, v11, threshold) * cellSize, cellSize);
                    float2 left = corner + new float2(0f, Cross(v00, v01, threshold) * cellSize);
                    switch (index)
                    {
                        case 1: case 14: segments.Add(new float4(left, bottom)); break;
                        case 2: case 13: segments.Add(new float4(bottom, right)); break;
                        case 3: case 12: segments.Add(new float4(left, right)); break;
                        case 4: case 11: segments.Add(new float4(right, top)); break;
                        case 6: case 9: segments.Add(new float4(bottom, top)); break;
                        case 7: case 8: segments.Add(new float4(left, top)); break;
                        case 5:
                            segments.Add(new float4(left, top));
                            segments.Add(new float4(bottom, right));
                            break;
                        case 10:
                            segments.Add(new float4(left, bottom));
                            segments.Add(new float4(right, top));
                            break;
                    }
                }
            }
        }

        private static float Cross(float a, float b, float threshold) => math.abs(b - a) < 1e-6f ? 0.5f : math.saturate((threshold - a) / (b - a));
    }
}
