using Colossal.Mathematics;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// A standard (right-turn) racetrack holding pattern in the horizontal plane.
    /// The loop is: inbound leg ending at the fix, 180-degree right turn, outbound leg, 180-degree right turn.
    /// </summary>
    public struct HoldingPattern
    {
        /// <summary>Holding fix; its y is the holding altitude.</summary>
        public float3 m_Fix;

        /// <summary>Unit inbound course (towards the fix and the runway).</summary>
        public float2 m_Inbound;

        /// <summary>Unit vector to the right of the inbound course, where the racetrack lies.</summary>
        public float2 m_Right;

        public float m_LegLength;

        public float m_TurnRadius;

        public HoldingPattern(float3 fix, float2 inbound, float legLength, float turnRadius)
        {
            m_Fix = fix;
            m_Inbound = inbound;
            m_Right = MathUtils.Right(inbound);
            m_LegLength = legLength;
            m_TurnRadius = turnRadius;
        }

        private float TurnLength => math.PI * m_TurnRadius;

        private float LoopLength => 2f * (m_LegLength + TurnLength);

        /// <summary>
        /// The point the airplane should fly towards: the closest point of the loop to <paramref name="position"/>,
        /// moved <paramref name="lookAhead"/> further along the loop. Steering towards it joins and then follows the loop.
        /// </summary>
        public float3 GetSteeringTarget(float3 position, float lookAhead)
        {
            float s = ClosestDistanceAlong(position.xz) + lookAhead;
            float loop = LoopLength;
            s -= math.floor(s / loop) * loop;
            float2 xz = PointAt(s);
            return new float3(xz.x, m_Fix.y, xz.y);
        }

        // Segment order and arc-length offsets: inbound [0, L), turn 1 [L, L+T), outbound [L+T, 2L+T), turn 2 [2L+T, 2L+2T).
        private float2 PointAt(float s)
        {
            float leg = m_LegLength;
            float turn = TurnLength;
            float2 fix = m_Fix.xz;
            if (s < leg)
            {
                return fix - m_Inbound * (leg - s);
            }
            s -= leg;
            if (s < turn)
            {
                math.sincos(s / m_TurnRadius, out float sin, out float cos);
                return fix + m_Right * (m_TurnRadius * (1f - cos)) + m_Inbound * (m_TurnRadius * sin);
            }
            s -= turn;
            float2 outboundStart = fix + m_Right * (2f * m_TurnRadius);
            if (s < leg)
            {
                return outboundStart - m_Inbound * s;
            }
            s -= leg;
            float2 legStart = fix - m_Inbound * leg;
            math.sincos(s / m_TurnRadius, out float sin2, out float cos2);
            return legStart + m_Right * (m_TurnRadius * (1f + cos2)) - m_Inbound * (m_TurnRadius * sin2);
        }

        private float ClosestDistanceAlong(float2 p)
        {
            float leg = m_LegLength;
            float turn = TurnLength;
            float r = m_TurnRadius;
            float2 fix = m_Fix.xz;

            // Work in the pattern's own frame: u along the inbound course (0 at the fix), v to the right.
            float2 d = p - fix;
            float u = math.dot(d, m_Inbound);
            float v = math.dot(d, m_Right);

            // Inbound leg: v = 0, u in [-L, 0].
            float bestS = leg + math.clamp(u, -leg, 0f);
            float best = math.lengthsq(new float2(u - math.clamp(u, -leg, 0f), v));

            // Outbound leg: v = 2r, u in [-L, 0], flown towards -u.
            float uo = math.clamp(u, -leg, 0f);
            float dist = math.lengthsq(new float2(u - uo, v - 2f * r));
            if (dist < best)
            {
                best = dist;
                bestS = leg + turn - uo;
            }

            // Turn 1: centre (0, r); angle 0 at the fix, PI at the outbound start, bulging towards +u.
            float angle = math.atan2(u, r - v);
            if (angle >= 0f)
            {
                dist = math.square(math.length(new float2(u, v - r)) - r);
                if (dist < best)
                {
                    best = dist;
                    bestS = leg + angle * r;
                }
            }

            // Turn 2: centre (-L, r); angle 0 at the outbound end, PI at the inbound start, bulging towards -u.
            float u2 = u + leg;
            angle = math.atan2(-u2, v - r);
            if (angle >= 0f)
            {
                dist = math.square(math.length(new float2(u2, v - r)) - r);
                if (dist < best)
                {
                    bestS = 2f * leg + turn + angle * r;
                }
            }
            return bestS;
        }
    }
}
