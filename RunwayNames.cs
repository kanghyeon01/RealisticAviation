using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Runway designators as used in the airport panel and on the radio: the heading of travel in tens of degrees (01–36;
    /// from map north, the north of the game's sky), with L/R (or L/C/R) for parallel runway ends of the same airport, seen in the
    /// direction of travel.
    /// </summary>
    internal static class RunwayNames
    {
        public static int Designator(float2 direction) => Designator(Heading(direction));

        /// <summary>The whole number nearest a tenth of the heading, 1–36 (north is 36, not 0).</summary>
        public static int Designator(float heading)
        {
            return Wrap((int)math.round(heading / 10f));
        }

        /// <summary>The next-nearest number (heading 327°: 32), for the second group of four or five parallel runways.</summary>
        private static int AlternateDesignator(float heading)
        {
            float tenth = heading / 10f;
            int nearest = (int)math.round(tenth);
            return Wrap(tenth >= nearest ? nearest + 1 : nearest - 1);
        }

        private static int Wrap(int number)
        {
            return ((number - 1) % 36 + 36) % 36 + 1;
        }

        /// <summary>
        /// Heading of a map direction in degrees: map +z is north and +x east, as in the game's sky (the sun rises towards
        /// +x). The game has no magnetic variation.
        /// </summary>
        public static float Heading(float2 direction)
        {
            return math.degrees(math.atan2(direction.x, direction.y));
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

        /// <summary>The designator of <paramref name="runway"/> travelled in <paramref name="direction"/>, e.g. "07L" (ICAO style: two digits).</summary>
        public static string Name(IReadOnlyList<RunwayInfo> runways, Entity runway, float2 direction)
        {
            return Name(runways, runway, direction, out _);
        }

        /// <summary>
        /// The designator and its number (FAA AC 150/5340-1, ICAO Annex 14): the whole number nearest a tenth of the heading,
        /// 01–36, two digits as ICAO writes it ("09", spoken "zero niner"). Parallel runway ends of the same airport with that number are told apart by position, seen in the
        /// direction of travel: L and R, or L, C and R. Four or five parallels are split into two groups, as the FAA does
        /// (heading 327°: 32L, 32R, 33L, 33R): the two westernmost/southernmost ends (a fixed side for both directions, so
        /// the two ends of a runway stay reciprocal) take the next-nearest number. Beyond five they are numbered.
        /// </summary>
        public static string Name(IReadOnlyList<RunwayInfo> runways, Entity runway, float2 direction, out int number)
        {
            number = 0;
            if (math.lengthsq(direction) < 0.25f)
            {
                return "--";
            }
            float heading = Heading(direction);
            int designator = Designator(heading);
            number = designator;
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
            // A side that is the same seen from either end of the runways: right of the end heading 0–180°.
            float2 fixedRight = MathUtils.Right(heading < 180f ? ownEnd : -ownEnd);

            s_Parallel.Clear();
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
                    if ((ends & bit) == 0 || Designator(Heading(endDirection)) != designator)
                    {
                        continue;
                    }
                    ownCounted |= other == own;
                    s_Parallel.Add(new ParallelEnd(Lateral(other.Geometry, endDirection), math.dot(other.Geometry.Middle, fixedRight), other == own));
                }
            }
            if (!ownCounted)
            {
                s_Parallel.Add(new ParallelEnd(Lateral(own.Geometry, ownEnd), math.dot(own.Geometry.Middle, fixedRight), true));
            }

            int count = s_Parallel.Count;
            ParallelEnd self = s_Parallel.Find(e => e.m_Own);
            if (count == 4 || count == 5)
            {
                // The two ends furthest to the fixed left side form the group with the next-nearest number.
                int leftOf = 0;
                for (int i = 0; i < count; i++)
                {
                    if (s_Parallel[i].m_Fixed < self.m_Fixed)
                    {
                        leftOf++;
                    }
                }
                bool alternate = leftOf < 2;
                if (alternate)
                {
                    number = AlternateDesignator(heading);
                }
                int index = 0;
                int groupCount = 0;
                for (int i = 0; i < count; i++)
                {
                    ParallelEnd other = s_Parallel[i];
                    int otherLeftOf = 0;
                    for (int j = 0; j < count; j++)
                    {
                        if (s_Parallel[j].m_Fixed < other.m_Fixed)
                        {
                            otherLeftOf++;
                        }
                    }
                    if ((otherLeftOf < 2) != alternate)
                    {
                        continue;
                    }
                    groupCount++;
                    if (!other.m_Own && other.m_Lateral < self.m_Lateral)
                    {
                        index++;
                    }
                }
                return number.ToString("00") + Suffix(index, groupCount);
            }
            int position = 0;
            for (int i = 0; i < count; i++)
            {
                if (!s_Parallel[i].m_Own && s_Parallel[i].m_Lateral < self.m_Lateral)
                {
                    position++;
                }
            }
            return designator.ToString("00") + Suffix(position, count);
        }

        private readonly struct ParallelEnd
        {
            public readonly float m_Lateral;
            public readonly float m_Fixed;
            public readonly bool m_Own;

            public ParallelEnd(float lateral, float fixedSide, bool own)
            {
                m_Lateral = lateral;
                m_Fixed = fixedSide;
                m_Own = own;
            }
        }

        // Main thread only (panels and radio text).
        private static readonly List<ParallelEnd> s_Parallel = new List<ParallelEnd>();
    }
}
