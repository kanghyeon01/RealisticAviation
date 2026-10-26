using System.Collections.Generic;
using System.Globalization;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>A SID of one runway end towards one airway, as the tower flies it (AtcSystem.GetSid).</summary>
    internal struct SidPath
    {
        public float2 m_Start;
        public float2 m_Center;
        public float2 m_Finish;
        public float2 m_Fix;
        public bool m_Turns;
        public string m_FixName;
    }

    /// <summary>
    /// One airplane outside connection seen from an airport: the airway, taken as the straight line from the airport to the
    /// connection (the game's airways are a grid its paths cross the map on); the STAR's first fix where an arrival on it
    /// first calls (the contact ring); and the SID of each runway end towards it.
    /// </summary>
    internal sealed class AirwayRoute
    {
        public float2 m_Connection;
        public string m_Designator;
        public float2 m_Entry;
        public string m_EntryName;
        public readonly List<SidPath> m_Sids = new List<SidPath>();
    }

    /// <summary>An airport's procedures for its STAR and SID charts: its runway ends in use (by name) and its airways.</summary>
    internal sealed class AirportProcedures
    {
        public Entity m_Airport;
        public float2 m_Center;
        public readonly List<ApproachChartData> m_Ends = new List<ApproachChartData>();
        public readonly List<IfrCharts.ApproachNames> m_EndNames = new List<IfrCharts.ApproachNames>();
        public readonly List<AirwayRoute> m_Airways = new List<AirwayRoute>();
    }

    internal static partial class IfrCharts
    {
        /// <summary>
        /// The STAR chart: each airway in grey up to the STAR's first fix (an open triangle: where the arrival first calls and
        /// leaves the airway), from there a STAR per runway end to the IAF of its sector, or the IF from straight in (the TAA
        /// areas the tower picks the fix by), named after the first fix with its route letter ("NOLAN 1A"); a hold at each
        /// fix a STAR leads to, with MAX SPD (the highest holding speed among the airplane types), MNM ALT (the lowest level
        /// of the stack there) and the leg length, drawn with the largest turn radius the tower flies a hold with among
        /// the airplane types (<paramref name="holdRadius"/>). Obstacles: the controlling one of each STAR and each hold.
        /// </summary>
        public static void BuildStar(IfrChartCanvas canvas, AirportProcedures procedures, List<ChartObstacle> obstacles, float holdingSpeedKnots,
            float holdRadius, float seaLevel)
        {
            if (procedures.m_Ends.Count == 0)
            {
                return;
            }
            canvas.Height = procedures.m_Ends[0].m_Threshold.y;
            canvas.Color = kInk;
            Runways(canvas, procedures);
            int ends = procedures.m_Ends.Count;
            // Per runway end and side (-1, 0, +1): the STAR first fixes leading there.
            List<float2>[] incoming = new List<float2>[ends * 3];
            for (int a = 0; a < procedures.m_Airways.Count; a++)
            {
                AirwayRoute airway = procedures.m_Airways[a];
                Airway(canvas, airway.m_Connection, airway.m_Entry, airway.m_Designator);
                List<float2> away = new List<float2> { math.normalizesafe(airway.m_Connection - airway.m_Entry) };
                for (int i = 0; i < ends; i++)
                {
                    ApproachChartData end = procedures.m_Ends[i];
                    int side = AtcSystem.ApproachSide(end, airway.m_Entry);
                    float2 target = HoldFix(end, side);
                    float arrow = 12f * kPx;
                    Leg(canvas, airway.m_Entry, target, 1, kRouteFixRadius * 0.5f, arrow, arrow, false, true, Designator(airway.m_EntryName, i));
                    away.Add(math.normalizesafe(target - airway.m_Entry));
                    int slot = i * 3 + side + 1;
                    (incoming[slot] ??= new List<float2>()).Add(airway.m_Entry);
                }
                Fix(canvas, airway.m_Entry, FixKind.Route, airway.m_EntryName, null, null, AltitudeKind.None, away.ToArray());
            }
            string speed = HoldSpeed(holdingSpeedKnots);
            for (int i = 0; i < ends; i++)
            {
                ApproachChartData end = procedures.m_Ends[i];
                IfrCharts.ApproachNames names = procedures.m_EndNames[i];
                string legText = HoldLeg(end.m_HoldLeg);
                for (int side = -1; side <= 1; side++)
                {
                    List<float2> from = incoming[i * 3 + side + 1];
                    if (from == null)
                    {
                        continue;
                    }
                    float2 fix = HoldFix(end, side);
                    // Inbound: the next leg of the approach (towards the IF; along the final course from straight in).
                    float2 inbound = side == 0 ? end.m_Course : math.normalizesafe(end.m_If.xz - fix);
                    float height = side < 0 ? end.m_HoldLeft : side > 0 ? end.m_HoldRight : end.m_HoldStraight;
                    string minimum = Altitude(height, seaLevel);
                    Hold(canvas, fix, inbound, end.m_HoldLeg, holdRadius, new[] { speed, "MNM ALT " + minimum, legText });
                    float2 right = new float2(inbound.y, -inbound.x);
                    List<float2> away = new List<float2> { -inbound, right, math.normalizesafe(right - inbound) };
                    for (int k = 0; k < from.Count; k++)
                    {
                        away.Add(math.normalizesafe(from[k] - fix));
                    }
                    Fix(canvas, fix, FixKind.Waypoint, side < 0 ? names.m_IafLeft : side > 0 ? names.m_IafRight : names.m_If,
                        side == 0 ? "(IF)" : "(IAF)", minimum, AltitudeKind.AtOrAbove, away.ToArray());
                }
            }
            Obstacles(canvas, obstacles, seaLevel);
        }

        /// <summary>
        /// The SID chart: from each runway end straight ahead, the turn towards the airway, and direct to the SID's fix on it (an
        /// open triangle) with the arrowhead, named after that fix with the runway end's route letter ("KOVAN 1A"); then the
        /// airway in grey on to the map edge. Obstacles through the 2.5 % surface, numbered, with the climb gradient notes
        /// ("MNM CLIMB 200 FT/NM", the tower's least climb, then "MNM CLIMB 3.4% UNTIL 1 200 FOR OBST (1)").
        /// </summary>
        public static void BuildSid(IfrChartCanvas canvas, AirportProcedures procedures, List<ChartObstacle> obstacles, float seaLevel)
        {
            if (procedures.m_Ends.Count == 0)
            {
                return;
            }
            canvas.Height = procedures.m_Ends[0].m_Threshold.y;
            canvas.Color = kInk;
            Runways(canvas, procedures);
            for (int a = 0; a < procedures.m_Airways.Count; a++)
            {
                AirwayRoute airway = procedures.m_Airways[a];
                float2 inner = airway.m_Connection;
                for (int i = 0; i < airway.m_Sids.Count; i++)
                {
                    ApproachChartData end = procedures.m_Ends[i];
                    SidPath sid = airway.m_Sids[i];
                    canvas.Line(end.m_RunwayEnd.xz, sid.m_Start, kLegWidth);
                    if (sid.m_Turns)
                    {
                        float sweep = SidSweep(end, sid, out float from);
                        canvas.Arc(sid.m_Center, math.distance(sid.m_Start, sid.m_Center), from, sweep, kLegWidth);
                    }
                    float arrow = 11f * kPx;
                    Leg(canvas, sid.m_Finish, sid.m_Fix, 1, 0f, arrow, arrow, false, false, Designator(sid.m_FixName, i));
                    Fix(canvas, sid.m_Fix, FixKind.Route, sid.m_FixName, null, null, AltitudeKind.None,
                        Away(sid.m_Fix, sid.m_Finish, airway.m_Connection));
                    if (math.distancesq(sid.m_Fix, procedures.m_Center) < math.distancesq(inner, procedures.m_Center))
                    {
                        inner = sid.m_Fix;
                    }
                }
                Airway(canvas, inner, airway.m_Connection, airway.m_Designator);
            }
            Obstacles(canvas, obstacles, seaLevel);
            SidNotes(canvas, procedures, obstacles, seaLevel);
        }

        /// <summary>The SID's turn: where on its circle it starts, and how far round it goes (counter-clockwise positive).</summary>
        internal static float SidSweep(in ApproachChartData end, in SidPath sid, out float from)
        {
            float2 to = sid.m_Fix - sid.m_Start;
            bool left = end.m_Course.x * to.y - end.m_Course.y * to.x > 0f;
            from = IfrChartCanvas.Angle(sid.m_Start - sid.m_Center);
            float sweep = IfrChartCanvas.Angle(sid.m_Finish - sid.m_Center) - from;
            return left ? sweep - math.floor(sweep / (2f * math.PI)) * 2f * math.PI
                : sweep - math.ceil(sweep / (2f * math.PI)) * 2f * math.PI;
        }

        /// <summary>The SID's track as a polyline (the turn in 15° pieces), from the runway end to its fix.</summary>
        internal static void SidTrack(in ApproachChartData end, in SidPath sid, List<float2> points)
        {
            points.Clear();
            points.Add(end.m_RunwayEnd.xz);
            points.Add(sid.m_Start);
            if (sid.m_Turns)
            {
                float sweep = SidSweep(end, sid, out float from);
                float radius = math.distance(sid.m_Start, sid.m_Center);
                int pieces = math.max(1, (int)math.ceil(math.abs(sweep) / math.radians(15f)));
                for (int i = 1; i <= pieces; i++)
                {
                    float angle = from + sweep * i / pieces;
                    points.Add(sid.m_Center + radius * new float2(math.cos(angle), math.sin(angle)));
                }
            }
            points.Add(sid.m_Fix);
        }

        /// <summary>
        /// The climb gradient notes as a block near the airport: the tower's least climb (200 ft/NM), then for each numbered
        /// obstacle the gradient that clears it by 0.8 % of its distance, and the altitude it is needed up to.
        /// </summary>
        private static void SidNotes(IfrChartCanvas canvas, AirportProcedures procedures, List<ChartObstacle> obstacles, float seaLevel)
        {
            List<string> lines = new List<string> { "MNM CLIMB 200 FT/NM" };
            for (int i = 0; obstacles != null && i < obstacles.Count; i++)
            {
                ChartObstacle obstacle = obstacles[i];
                if (obstacle.m_Number <= 0)
                {
                    continue;
                }
                int until = (int)math.ceil((math.max(0f, obstacle.m_Top.y - seaLevel) + IfrChartObstacles.kSidMoc * obstacle.m_Distance) / 0.3048f / 100f) * 100;
                lines.Add("MNM CLIMB " + (obstacle.m_Gradient * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "% UNTIL " + Spaced(until)
                    + " FOR OBST (" + obstacle.m_Number.ToString(CultureInfo.InvariantCulture) + ")");
            }
            float widest = 0f;
            for (int i = 0; i < lines.Count; i++)
            {
                widest = math.max(widest, IfrChartCanvas.TextWidth(lines[i], kNoteHeight));
            }
            float step = kNoteHeight + 6f * kTx;
            float2 half = new float2(widest * 0.5f + 2f * kTx, (lines.Count * step - 6f * kTx) * 0.5f + 2f * kTx);
            // Round the airport: sixteen directions, nearer first.
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(64);
            float[] distances = { 3000f, 4500f, 6000f, 7500f };
            for (int d = 0; d < distances.Length; d++)
            {
                for (int i = 0; i < 16; i++)
                {
                    float t = i * math.PI / 8f;
                    spots.Add(new IfrChartCanvas.Spot { m_Center = procedures.m_Center + new float2(math.cos(t), math.sin(t)) * distances[d], m_Angle = 0f });
                }
            }
            canvas.AddLabel(kPriorityObstacle, true, half, spots, (c, spot) =>
            {
                c.Color = kInk;
                float top = spot.m_Center.y + half.y - 2f * kTx - kNoteHeight * 0.5f;
                for (int i = 0; i < lines.Count; i++)
                {
                    // Left-aligned, as a note block.
                    float width = IfrChartCanvas.TextWidth(lines[i], kNoteHeight);
                    c.Text(lines[i], new float2(spot.m_Center.x - half.x + 2f * kTx + width * 0.5f, top - i * step), 0f, kNoteHeight, kNoteHeight * kTextStroke);
                }
            });
        }

        /// <summary>The fix a STAR of this side leads to: the left or right IAF, or the IF from straight in.</summary>
        private static float2 HoldFix(in ApproachChartData end, int side)
        {
            return side < 0 ? end.m_IafLeft.xz : side > 0 ? end.m_IafRight.xz : end.m_If.xz;
        }

        /// <summary>Each runway of the airport's ends once.</summary>
        private static void Runways(IfrChartCanvas canvas, AirportProcedures procedures)
        {
            for (int i = 0; i < procedures.m_Ends.Count; i++)
            {
                ApproachChartData end = procedures.m_Ends[i];
                bool drawn = false;
                for (int k = 0; k < i && !drawn; k++)
                {
                    drawn = math.distancesq(procedures.m_Ends[k].m_Threshold.xz, end.m_RunwayEnd.xz) < 1f;
                }
                if (!drawn)
                {
                    Runway(canvas, end.m_Threshold.xz, end.m_RunwayEnd.xz);
                }
            }
        }
    }
}
