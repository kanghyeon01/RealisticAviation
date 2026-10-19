using System.Collections.Generic;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>A filled rectangle of the VFR chart overlay, drawn as one line along x as wide as the rectangle is tall.</summary>
    internal struct ChartRect
    {
        public float3 m_From;
        public float3 m_To;
        public float m_Width;
        public byte m_Band;
    }

    /// <summary>
    /// Turns runs of equal cells, row by row, into as few rectangles as possible: a run with the same columns and band as one
    /// in the row before extends that rectangle instead of starting a new one. Large even areas (a hillside of one tint, a
    /// built-up district) become a handful of rectangles, which keeps the overlay cheap to draw.
    /// </summary>
    internal sealed class RectMerger
    {
        private struct Open
        {
            public int m_Z0;
            public int m_X0;
            public int m_X1;
            public byte m_Band;
            public float m_HeightSum;
            public int m_Cells;
        }

        private readonly float m_CellSize;
        private readonly float m_HalfExtent;
        private readonly List<ChartRect> m_Output;
        private Dictionary<int, Open> m_Previous = new Dictionary<int, Open>();
        private Dictionary<int, Open> m_Current = new Dictionary<int, Open>();
        private int m_Row;

        public RectMerger(float cellSize, float halfExtent, List<ChartRect> output)
        {
            m_CellSize = cellSize;
            m_HalfExtent = halfExtent;
            m_Output = output;
            m_Output.Clear();
        }

        private static int Key(int x0, int x1, byte band) => x0 | (x1 << 12) | (band << 24);

        /// <summary>Starts row <paramref name="z"/> (rows in increasing order).</summary>
        public void BeginRow(int z)
        {
            m_Row = z;
            m_Current.Clear();
        }

        /// <summary>Cells x0 (included) to x1 (excluded) of the current row, of one band, with the sum of their heights.</summary>
        public void AddRun(int x0, int x1, byte band, float heightSum)
        {
            int key = Key(x0, x1, band);
            if (m_Previous.TryGetValue(key, out Open open))
            {
                m_Previous.Remove(key);
                open.m_HeightSum += heightSum;
                open.m_Cells += x1 - x0;
            }
            else
            {
                open = new Open { m_Z0 = m_Row, m_X0 = x0, m_X1 = x1, m_Band = band, m_HeightSum = heightSum, m_Cells = x1 - x0 };
            }
            m_Current[key] = open;
        }

        /// <summary>Ends the current row: rectangles not continued in it are finished.</summary>
        public void EndRow()
        {
            foreach (Open open in m_Previous.Values)
            {
                Emit(open, m_Row);
            }
            Dictionary<int, Open> swap = m_Previous;
            m_Previous = m_Current;
            m_Current = swap;
        }

        /// <summary>Finishes every rectangle still open after the last row.</summary>
        public void Finish()
        {
            foreach (Open open in m_Previous.Values)
            {
                Emit(open, m_Row + 1);
            }
            m_Previous.Clear();
        }

        private void Emit(Open open, int z1)
        {
            float y = open.m_HeightSum / open.m_Cells;
            float centerZ = (open.m_Z0 + z1) * 0.5f * m_CellSize - m_HalfExtent;
            m_Output.Add(new ChartRect
            {
                m_From = new float3(open.m_X0 * m_CellSize - m_HalfExtent, y, centerZ),
                m_To = new float3(open.m_X1 * m_CellSize - m_HalfExtent, y, centerZ),
                m_Width = (z1 - open.m_Z0) * m_CellSize,
                m_Band = open.m_Band,
            });
        }
    }
}
