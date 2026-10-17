using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Runway designators as used in the airport panel and on the radio: the heading of travel in tens of degrees (01–36;
    /// the game has no magnetic variation), with L/R (or L/C/R) for parallel runway ends of the same airport, seen in the
    /// direction of travel.
    /// </summary>
    internal static class RunwayNames
    {
        public static int Designator(float2 direction)
        {
            float heading = math.degrees(math.atan2(direction.x, direction.y));
            int designator = (int)math.round(heading / 10f) % 36;
            if (designator <= 0)
            {
                designator += 36;
            }
            return designator;
        }

        public static string Suffix(int index, int count)
        {
            if (count == 1)
            {
                return string.Empty;
            }
            if (count == 2)
            {
                return index == 0 ? "L" : "R";
            }
            if (count == 3)
            {
                return index == 0 ? "L" : index == 1 ? "C" : "R";
            }
            return "-" + (index + 1);
        }

        /// <summary>The ends of a runway in use (game runways are usually one-way); both if its lanes say nothing.</summary>
        public static byte UsedEnds(in RunwayGeometry geometry)
        {
            return geometry.m_Landings != 0 ? geometry.m_Landings : (byte)(RunwayGeometry.kAlong | RunwayGeometry.kAgainst);
        }

        /// <summary>Distance to the right of the direction of travel, which orders parallel runway ends left to right.</summary>
        public static float Lateral(in RunwayGeometry geometry, float2 direction)
        {
            return math.dot(geometry.Middle, MathUtils.Right(direction));
        }

        /// <summary>The designator of <paramref name="runway"/> travelled in <paramref name="direction"/>, e.g. "07L".</summary>
        public static string Name(IReadOnlyList<RunwayInfo> runways, Entity runway, float2 direction)
        {
            if (math.lengthsq(direction) < 0.25f)
            {
                return "--";
            }
            int designator = Designator(direction);
            RunwayInfo own = null;
            for (int i = 0; i < runways.Count && own == null; i++)
            {
                if (runways[i].Key == runway)
                {
                    own = runways[i];
                }
            }
            if (own == null)
            {
                return designator.ToString("00");
            }
            float2 ownEnd = math.dot(own.Geometry.m_Direction, direction) >= 0f ? own.Geometry.m_Direction : -own.Geometry.m_Direction;
            float ownLateral = Lateral(own.Geometry, ownEnd);
            int count = 0;
            int index = 0;
            bool ownCounted = false;
            for (int i = 0; i < runways.Count; i++)
            {
                RunwayInfo other = runways[i];
                if (other.Airport != own.Airport)
                {
                    continue;
                }
                byte ends = UsedEnds(other.Geometry);
                for (int end = 0; end < 2; end++)
                {
                    byte bit = end == 0 ? RunwayGeometry.kAlong : RunwayGeometry.kAgainst;
                    float2 endDirection = end == 0 ? other.Geometry.m_Direction : -other.Geometry.m_Direction;
                    if ((ends & bit) == 0 || Designator(endDirection) != designator)
                    {
                        continue;
                    }
                    count++;
                    if (other == own)
                    {
                        ownCounted = true;
                    }
                    else if (Lateral(other.Geometry, endDirection) < ownLateral)
                    {
                        index++;
                    }
                }
            }
            if (!ownCounted)
            {
                count++;
            }
            return designator.ToString("00") + Suffix(index, count);
        }
    }
}
