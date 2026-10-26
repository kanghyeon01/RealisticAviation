using System.Collections.Generic;
using Unity.Mathematics;

namespace RealisticAviation
{
    internal static partial class IfrCharts
    {
        /// <summary>The fix names of one runway end's approach, from the city's registry.</summary>
        internal struct ApproachNames
        {
            public string m_IafLeft;
            public string m_IafRight;
            public string m_If;
            public string m_Faf;
            public string m_Upwind;
            public string m_Crosswind;
            public string m_Downwind;
            public string m_MissedHold;

            public static ApproachNames Get(WaypointNames names, in ApproachChartData data)
            {
                return new ApproachNames
                {
                    m_IafLeft = names.Get(data.m_IafLeft.xz),
                    m_IafRight = names.Get(data.m_IafRight.xz),
                    m_If = names.Get(data.m_If.xz),
                    m_Faf = names.Get(data.m_Faf.xz),
                    m_Upwind = names.Get(data.m_Upwind.xz),
                    m_Crosswind = names.Get(data.m_Crosswind.xz),
                    m_Downwind = names.Get(data.m_Downwind.xz),
                    m_MissedHold = names.Get(data.m_MissedHold.xz),
                };
            }
        }

        /// <summary>
        /// An approach chart ("RNP RWY 07") after the AIP's RNP charts: the runway outline; the "T" (both IAFs into the IF,
        /// then the FAF and final with the arrowhead at the threshold) as bold lines, where paths merge straight into the fix;
        /// the missed approach as the go-around pattern, dashed, with fly-by arcs at its corners and the turn onto final at
        /// the FAF. Each leg carries its distance and true course; each fix its symbol and (role) / name / altitude. Obstacles:
        /// the controlling one of each initial and the intermediate segment, and those through the final approach surface.
        /// </summary>
        public static void BuildApproach(IfrChartCanvas canvas, in ApproachChartData data, in ApproachNames names, List<ChartObstacle> obstacles,
            float holdingSpeedKnots, float seaLevel)
        {
            canvas.Height = data.m_Threshold.y;
            canvas.Color = kInk;
            float2 threshold = data.m_Threshold.xz, end = data.m_RunwayEnd.xz;
            float2 iafLeft = data.m_IafLeft.xz, iafRight = data.m_IafRight.xz, fix = data.m_If.xz, faf = data.m_Faf.xz;
            float2 up = data.m_Upwind.xz, cross = data.m_Crosswind.xz, down = data.m_Downwind.xz;
            float radius = data.m_TurnRadius;

            Runway(canvas, threshold, end);

            // Both IAF legs meet at the IF: where paths merge the lines run straight into the fix (AIP UD050 / UP050).
            Leg(canvas, iafLeft, fix, -1, kFixClear, kFixClear, -1f, false);
            Leg(canvas, iafRight, fix, 1, kFixClear, kFixClear, -1f, false);
            Leg(canvas, fix, faf, 1, kFixClear, kFixClear, -1f, false);
            Leg(canvas, faf, threshold, 1, kFixClear, 0f, 0f, false);

            // Missed approach: a single path, so its corners are fly-by arcs; the last one turns onto final at the FAF.
            float tu = math.max(Turn(canvas, end, up, cross, radius, false, true), kFixClear);
            float tc = math.max(Turn(canvas, up, cross, down, radius, false, true), kFixClear);
            float td = math.max(Turn(canvas, cross, down, faf, radius, false, true), kFixClear);
            float tf = math.max(Turn(canvas, down, faf, threshold, radius, true, true), kFixClear);
            Leg(canvas, end, up, 1, 0f, tu, -1f, true);
            Leg(canvas, up, cross, 1, tu, tc, -1f, true);
            Leg(canvas, cross, down, 1, tc, td, -1f, true);
            Leg(canvas, down, faf, 1, td, tf, -1f, true);

            Fix(canvas, up, FixKind.Waypoint, names.m_Upwind, null, null, AltitudeKind.None, Away(up, end, cross));
            Fix(canvas, cross, FixKind.Waypoint, names.m_Crosswind, null, Altitude(data.m_Crosswind.y, seaLevel), AltitudeKind.At, Away(cross, up, down));
            Fix(canvas, down, FixKind.Waypoint, names.m_Downwind, null, null, AltitudeKind.None, Away(down, cross, faf, data.m_MissedHold.xz));

            // Not cleared by the end of downwind: extended 2 NM, then the missed approach hold there at pattern height.
            float2 hold = data.m_MissedHold.xz, inbound = data.m_MissedInbound, holdRight = new float2(inbound.y, -inbound.x);
            string pattern = Altitude(data.m_MissedHold.y, seaLevel);
            Leg(canvas, down, hold, 1, kFixClear, kFixClear, -1f, true);
            Hold(canvas, hold, inbound, data.m_HoldLeg, radius, new[] { HoldSpeed(holdingSpeedKnots), "MNM ALT " + pattern, HoldLeg(data.m_HoldLeg) }, true);
            Fix(canvas, hold, FixKind.Waypoint, names.m_MissedHold, null, pattern, AltitudeKind.At,
                new[] { -inbound, holdRight, math.normalizesafe(holdRight - inbound) });
            Fix(canvas, iafLeft, FixKind.Waypoint, names.m_IafLeft, "(IAF)", Altitude(data.m_IafLeft.y, seaLevel), AltitudeKind.AtOrAbove, Away(iafLeft, fix));
            Fix(canvas, iafRight, FixKind.Waypoint, names.m_IafRight, "(IAF)", Altitude(data.m_IafRight.y, seaLevel), AltitudeKind.AtOrAbove, Away(iafRight, fix));
            Fix(canvas, fix, FixKind.Waypoint, names.m_If, "(IF)", Altitude(data.m_If.y, seaLevel), AltitudeKind.AtOrAbove, Away(fix, iafLeft, iafRight, faf));
            Fix(canvas, faf, FixKind.Waypoint, names.m_Faf, "(FAF)", Altitude(data.m_Faf.y, seaLevel), AltitudeKind.AtOrAbove, Away(faf, fix, threshold, down));
            Obstacles(canvas, obstacles, seaLevel);
        }
    }
}
