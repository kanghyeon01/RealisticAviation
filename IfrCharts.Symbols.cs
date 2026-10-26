using System.Collections.Generic;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// The IFR chart's plan views, as docs/ifr-symbols-preview.html draws them after the AIP: this part holds the symbols
    /// they share (legs, fly-by turns, fixes, holds, airways, the runway). Sizes are the preview's, in its pixels
    /// (<see cref="IfrChartCanvas.Px"/>).
    /// </summary>
    internal static partial class IfrCharts
    {
        internal static readonly Color kInk = new Color(0.07f, 0.07f, 0.07f, 1f);
        internal static readonly Color kAirwayGrey = new Color(0.48f, 0.48f, 0.48f, 1f);

        // Sizes in preview pixels: properties, as the chart's scale is set per build (IfrChartCanvas.Px).
        private static float kPx => IfrChartCanvas.Px;

        /// <summary>
        /// Text is drawn at this share of the preview's size (user, 2026-10-26: at full size it crowded the map and held
        /// little), with the gaps and boxes that go with it; lines and symbols keep the preview's size.
        /// </summary>
        private const float kTextScale = 0.5f;
        private static float kTx => kPx * kTextScale;
        /// <summary>Line widths at this share of the preview's (user, 2026-10-26: too heavy over the city).</summary>
        private const float kLineScale = 0.5f;
        private static float kLegWidth => 3f * kPx * kLineScale;
        private static float kMissedWidth => 2.4f * kPx * kLineScale;
        private static float kMissedDash => 5f * kPx;
        private static float kMissedGap => 4f * kPx;
        private static float kHeadSize => 11f * kPx;
        private static float kFixRadius => 11f * kPx;
        private static float kFixStroke => 1.5f * kPx * kLineScale;

        /// <summary>Lines into a waypoint stop at its circle (the preview's symbols are filled and hide them).</summary>
        private static float kFixClear => kFixRadius * 0.62f;
        private static float kRouteFixRadius => 10f * kPx;
        private static float kRouteFixStroke => 1.6f * kPx * kLineScale;
        private static float kDistanceHeight => 10f * kTx;
        private static float kDistanceStroke => 1.3f * kTx;
        private static float kCourseHeight => 11f * kTx;
        private static float kCourseOffset => 20f * kTx;
        private static float kProcedureNameHeight => 12f * kTx;
        private static float kNameHeight => 11f * kTx;
        private static float kRoleHeight => 9f * kTx;
        private static float kRoleStroke => 1.9f * kTx;
        private static float kAltitudeHeight => 10f * kTx;
        private static float kLineGap => 5f * kTx;
        private static float kRunwayHalfWidth => 4f * kPx;
        private static float kRunwayStroke => 2.2f * kPx * kLineScale;
        private static float kHoldWidth => 2f * kPx * kLineScale;
        private static float kHoldHeadSize => 10f * kPx;
        private static float kHoldCourseHeight => 8f * kTx;
        private static float kHoldTextMax => 7f * kTx;
        /// <summary>The preview's hold (1.5 NM legs, radius 1,300 m): the chart is scaled down from the preview by the holds' radius against it.</summary>
        internal const float kPreviewHoldRadius = 1300f;
        private static float kAirwayWidth => 1.6f * kPx * kLineScale;
        private static float kAirwayNameHeight => 10f * kTx;
        private static float kTallObstacleSize => 24f * kPx;
        private static float kLowObstacleSize => 15f * kPx;
        private static float kTerrainObstacleSize => 6f * kPx;
        private static float kTerrainDot => 5.2f * kPx;
        private static float kObstacleTextHeight => 9f * kTx;
        private static float kNoteHeight => 9f * kTx;

        /// <summary>The spire from 1,000 ft above the ground, as on the VFR chart.</summary>
        private const float kTallObstacleHeight = 1000f * 0.3048f;

        private static readonly float2[] kObstacleDirections =
        {
            new float2(1f, 0f), new float2(-1f, 0f), new float2(0f, -1f), new float2(0f, 1f),
            new float2(0.7f, -0.7f), new float2(-0.7f, -0.7f), new float2(0.7f, 0.7f), new float2(-0.7f, 0.7f),
        };

        /// <summary>Normal text strokes, as a share of the character height; bold procedure names.</summary>
        private const float kTextStroke = 0.13f;
        private const float kBoldStroke = 0.2f;

        // Placement order: fix labels, obstacles and notes, courses, procedure names, hold courses, airway names.
        private const int kPriorityFix = 1;
        private const int kPriorityObstacle = 2;
        private const int kPriorityCourse = 3;
        private const int kPriorityName = 4;
        private const int kPriorityHoldCourse = 5;
        private const int kPriorityAirway = 6;

        private static readonly float[] kCourseShares = { 0.5f, 0.42f, 0.58f, 0.34f, 0.66f, 0.26f, 0.74f };
        private static readonly float[] kNameShares = { 0.5f, 0.38f, 0.62f, 0.28f, 0.72f, 0.2f, 0.8f };
        private static readonly float[] kAirwayShares = { 0.5f, 0.4f, 0.6f, 0.3f, 0.7f, 0.2f, 0.8f };

        /// <summary>Route letters of the procedure designators, one per runway end: A, B, C... without I and O (Annex 11).</summary>
        private const string kRouteLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ";

        /// <summary>The validity number of every procedure for now (kept as one place to change).</summary>
        private const int kValidity = 1;

        private enum AltitudeKind
        {
            None,
            AtOrAbove,
            At,
        }

        private enum FixKind
        {
            Waypoint,
            Route,
        }

        /// <summary>A procedure's designator: its fix, the validity number and the route letter of its runway end ("KOVAN 1A").</summary>
        private static string Designator(string fix, int runwayIndex)
        {
            return fix + " " + kValidity.ToString(CultureInfo.InvariantCulture) + kRouteLetters[runwayIndex % kRouteLetters.Length];
        }

        /// <summary>Above sea level in feet, rounded up to 100.</summary>
        internal static int AltitudeFeet(float y, float seaLevel) => (int)math.ceil(math.max(0f, y - seaLevel) / 0.3048f / 100f) * 100;

        /// <summary>Above sea level in feet, rounded up to 100, as text.</summary>
        private static string Altitude(float y, float seaLevel)
        {
            int feet = (int)math.ceil(math.max(0f, y - seaLevel) / 0.3048f / 100f) * 100;
            return feet.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The true course along a direction, "070°".</summary>
        internal static string Course(float2 direction)
        {
            int degrees = ((int)math.round(RunwayNames.Heading(direction)) % 360 + 360) % 360;
            return degrees.ToString("000", CultureInfo.InvariantCulture) + "°";
        }

        private static string Distance(float2 a, float2 b)
        {
            return (math.distance(a, b) / Setting.kMetersPerNauticalMile).ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>Unit vectors from a fix towards the fixes its lines run to.</summary>
        private static float2[] Away(float2 fix, params float2[] others)
        {
            float2[] directions = new float2[others.Length];
            for (int i = 0; i < others.Length; i++)
            {
                directions[i] = math.normalizesafe(others[i] - fix);
            }
            return directions;
        }

        /// <summary>The runway outline as on the AIP approach chart, at least a few pixels wide.</summary>
        private static void Runway(IfrChartCanvas canvas, float2 threshold, float2 end)
        {
            float2 along = math.normalizesafe(end - threshold);
            float2 side = new float2(-along.y, along.x) * kRunwayHalfWidth;
            canvas.Polygon(new[] { threshold + side, end + side, end - side, threshold - side }, kRunwayStroke);
            canvas.Segment(threshold, end, kRunwayHalfWidth * 2f);
        }

        /// <summary>
        /// A leg from a to b: the line (trimmed where a fly-by arc takes over), the distance in an arrow outline open at the
        /// back, in the line (if <paramref name="distance"/>), the course beside it as a label, the arrowhead
        /// (<paramref name="head"/> &gt;= 0: its tip that far short of b) at the end of a procedure, and the procedure's name on the
        /// other side (if <paramref name="name"/>).
        /// </summary>
        private static void Leg(IfrChartCanvas canvas, float2 a, float2 b, int side, float trimStart, float trimEnd, float head, bool dashed,
            bool distance = true, string name = null)
        {
            float2 unit = math.normalizesafe(b - a);
            float2 x1 = a + unit * trimStart, x2 = b - unit * trimEnd, middle = (x1 + x2) * 0.5f;
            float width = dashed ? kMissedWidth : kLegWidth, dash = dashed ? kMissedDash : 0f, gap = dashed ? kMissedGap : 0f;
            if (head >= 0f)
            {
                canvas.Head(b - unit * head, unit, kHeadSize);
            }
            float angle = IfrChartCanvas.Angle(unit);
            if (distance)
            {
                string text = Distance(a, b);
                float textWidth = IfrChartCanvas.TextWidth(text, kDistanceHeight);
                float back = textWidth * 0.5f + 5f * kTx, ahead = textWidth * 0.5f + 12f * kTx;
                canvas.Line(x1, middle - unit * back, width, dash, gap);
                canvas.Line(middle + unit * ahead, x2, width, dash, gap);

                // The arrow outline round the figure, pointing along the leg; the figure turned to stay readable.
                float2 across = new float2(-unit.y, unit.x);
                float half = 8.5f * kTx;
                float2 p0 = middle - unit * back - across * half, p1 = middle + unit * back - across * half, tip = middle + unit * ahead;
                float2 p3 = middle + unit * back + across * half, p4 = middle - unit * back + across * half;
                canvas.Line(p0, p1, kDistanceStroke, 0f, 0f, false);
                canvas.Line(p1, tip, kDistanceStroke, 0f, 0f, false);
                canvas.Line(tip, p3, kDistanceStroke, 0f, 0f, false);
                canvas.Line(p3, p4, kDistanceStroke, 0f, 0f, false);
                canvas.Text(text, middle, IfrChartCanvas.Readable(angle), kDistanceHeight, kDistanceHeight * kTextStroke);
                canvas.Box(middle + unit * ((ahead - back) * 0.5f), new float2((back + ahead) * 0.5f, 9f * kTx), angle);
            }
            else
            {
                canvas.Line(x1, x2, width, dash, gap);
            }
            AlongLabel(canvas, Course(b - a), kCourseHeight, kTextStroke, x1, x2, side, kCourseOffset, kCourseShares, kPriorityCourse);
            if (name != null)
            {
                AlongLabel(canvas, name, kProcedureNameHeight, kBoldStroke, x1, x2, -side, kCourseOffset, kNameShares, kPriorityName);
            }
        }

        /// <summary>A text label along a segment, at the given shares of it, on the preferred side first.</summary>
        private static void AlongLabel(IfrChartCanvas canvas, string text, float height, float stroke, float2 a, float2 b, int side, float offset,
            float[] shares, int priority)
        {
            float2 delta = b - a, unit = math.normalizesafe(delta), normal = new float2(-unit.y, unit.x);
            float angle = IfrChartCanvas.Readable(IfrChartCanvas.Angle(unit));
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(shares.Length * 2);
            for (int k = 0; k < 2; k++)
            {
                int s = k == 0 ? side : -side;
                for (int i = 0; i < shares.Length; i++)
                {
                    spots.Add(new IfrChartCanvas.Spot { m_Center = a + delta * shares[i] + normal * (s * offset), m_Angle = angle });
                }
            }
            float2 half = new float2(IfrChartCanvas.TextWidth(text, height) * 0.5f + 2f * kTx, height * 0.5f + 2f * kTx);
            Color color = canvas.Color;
            canvas.AddLabel(priority, false, half, spots, (c, spot) =>
            {
                c.Color = color;
                c.Text(text, spot.m_Center, spot.m_Angle, height, height * stroke);
            });
        }

        /// <summary>
        /// A fly-by turn at <paramref name="v"/> from a to b, as the AIP draws it: the track rounds the corner on the inside,
        /// with the turn radius (cut down when a leg is too short for it). Returns how much each leg loses at the fix.
        /// </summary>
        private static float Turn(IfrChartCanvas canvas, float2 a, float2 v, float2 b, float radius, bool head, bool dashed)
        {
            float l1 = math.distance(a, v), l2 = math.distance(v, b);
            float2 u1 = (v - a) / math.max(l1, 1f), u2 = (b - v) / math.max(l2, 1f);
            float turn = math.acos(math.clamp(math.dot(u1, u2), -1f, 1f));
            if (turn < 0.05f)
            {
                return 0f;
            }
            float half = math.tan(turn * 0.5f);
            float r = math.min(radius, 0.45f * math.min(l1, l2) / half), trim = r * half;
            float2 start = v - u1 * trim, finish = v + u2 * trim;
            bool left = u1.x * u2.y - u1.y * u2.x > 0f;
            float2 center = start + (left ? new float2(-u1.y, u1.x) : new float2(u1.y, -u1.x)) * r;
            canvas.Arc(center, r, IfrChartCanvas.Angle(start - center), left ? turn : -turn, dashed ? kMissedWidth : kLegWidth,
                dashed ? kMissedDash : 0f, dashed ? kMissedGap : 0f);
            if (head)
            {
                canvas.Head(finish + u2 * (4f * kPx), u2, kHeadSize);
            }
            return trim;
        }

        /// <summary>
        /// A fix: the RNAV waypoint symbol (an open circle with four points) or, where a procedure meets an airway, an open
        /// triangle; and its label block, (role) / name / altitude, on the side farthest from the lines meeting it
        /// (<paramref name="away"/>: unit vectors towards them).
        /// </summary>
        private static void Fix(IfrChartCanvas canvas, float2 p, FixKind kind, string name, string role, string altitude, AltitudeKind altitudeKind, float2[] away)
        {
            float radius;
            if (kind == FixKind.Route)
            {
                radius = kRouteFixRadius;
                canvas.Polygon(new[] { p + new float2(0f, radius), p + new float2(0.87f * radius, -0.5f * radius), p + new float2(-0.87f * radius, -0.5f * radius) },
                    kRouteFixStroke);
            }
            else
            {
                radius = kFixRadius;
                float r0 = radius * 0.62f;
                float2[] star = new float2[12];
                for (int i = 0; i < 4; i++)
                {
                    float a = i * math.PI / 2f;
                    star[i * 3] = p + r0 * new float2(math.cos(a - 0.5f), math.sin(a - 0.5f));
                    star[i * 3 + 1] = p + radius * new float2(math.cos(a), math.sin(a));
                    star[i * 3 + 2] = p + r0 * new float2(math.cos(a + 0.5f), math.sin(a + 0.5f));
                }
                canvas.Polygon(star, kFixStroke);
                canvas.Circle(p, r0, kFixStroke);
            }
            canvas.Box(p, new float2(radius, radius), 0f);

            int lineCount = (role != null ? 1 : 0) + 1 + (altitude != null ? 1 : 0);
            float widest = IfrChartCanvas.TextWidth(name, kNameHeight);
            float total = kNameHeight;
            if (role != null)
            {
                widest = math.max(widest, IfrChartCanvas.TextWidth(role, kRoleHeight));
                total += kRoleHeight;
            }
            if (altitude != null)
            {
                widest = math.max(widest, IfrChartCanvas.TextWidth(altitude, kAltitudeHeight));
                total += kAltitudeHeight;
            }
            float2 half = new float2(widest * 0.5f + 3f * kTx, (total + kLineGap * (lineCount - 1)) * 0.5f + 3f * kTx);

            // Sixteen directions, the clearest first (largest angle to the nearest line), at three distances.
            float2[] directions = new float2[16];
            float[] clear = new float[16];
            for (int i = 0; i < 16; i++)
            {
                float t = i * math.PI / 8f;
                directions[i] = new float2(math.cos(t), math.sin(t));
                clear[i] = math.PI;
                for (int k = 0; k < away.Length; k++)
                {
                    clear[i] = math.min(clear[i], math.acos(math.clamp(math.dot(directions[i], away[k]), -1f, 1f)));
                }
            }
            int[] order = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
            System.Array.Sort(order, (x, y) => clear[x] != clear[y] ? clear[y].CompareTo(clear[x]) : x.CompareTo(y));
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(48);
            for (int e = 0; e < 3; e++)
            {
                for (int i = 0; i < 16; i++)
                {
                    float2 v = directions[order[i]];
                    float distance = radius + 4f * kTx + e * 14f * kTx + math.abs(v.x) * half.x + math.abs(v.y) * half.y;
                    spots.Add(new IfrChartCanvas.Spot { m_Center = p + v * distance, m_Angle = 0f });
                }
            }
            canvas.AddLabel(kPriorityFix, true, half, spots, (c, spot) =>
            {
                c.Color = kInk;
                float top = spot.m_Center.y + half.y - 3f * kTx;
                if (role != null)
                {
                    c.Text(role, new float2(spot.m_Center.x, top - kRoleHeight * 0.5f), 0f, kRoleHeight, kRoleStroke);
                    top -= kRoleHeight + kLineGap;
                }
                c.Text(name, new float2(spot.m_Center.x, top - kNameHeight * 0.5f), 0f, kNameHeight, kNameHeight * kTextStroke);
                top -= kNameHeight + kLineGap;
                if (altitude != null)
                {
                    float2 at = new float2(spot.m_Center.x, top - kAltitudeHeight * 0.5f);
                    c.Text(altitude, at, 0f, kAltitudeHeight, kAltitudeHeight * kTextStroke);
                    // Underline: at or above; overline too: at (AIP / ICAO Annex 4).
                    float reach = IfrChartCanvas.TextWidth(altitude, kAltitudeHeight) * 0.5f + kTx;
                    float bar = kAltitudeHeight * 0.85f, stroke = kAltitudeHeight * 0.12f;
                    c.Line(at + new float2(-reach, -bar), at + new float2(reach, -bar), stroke, 0f, 0f, false);
                    if (altitudeKind == AltitudeKind.At)
                    {
                        c.Line(at + new float2(-reach, bar), at + new float2(reach, bar), stroke, 0f, 0f, false);
                    }
                }
            });
        }

        /// <summary>
        /// A racetrack hold with right turns (as the tower flies it, <see cref="HoldingPattern"/>): the inbound leg ends at the
        /// fix. As on the AIP: an arrow on each straight leg (inbound towards the fix, outbound away), each leg's course
        /// outside it, and the limits inside along the axis, sized to fit, all of them (user: shrink them, don't drop any;
        /// the whole chart is scaled with the holds, IfrChartCanvas.Px).
        /// </summary>
        private static void Hold(IfrChartCanvas canvas, float2 fix, float2 inbound, float leg, float radius, string[] lines, bool dashed = false)
        {
            // The missed approach's hold dashed, as the rest of the missed approach.
            float width = dashed ? kMissedWidth : kHoldWidth, head = kHoldHeadSize;
            float dash = dashed ? kMissedDash : 0f, gap = dashed ? kMissedGap : 0f;
            float2 right = new float2(inbound.y, -inbound.x);
            float2 start = fix - inbound * leg, far = fix + right * (2f * radius), back = start + right * (2f * radius);
            float angle = IfrChartCanvas.Angle(inbound);
            canvas.Line(start, fix, width, dash, gap);
            canvas.Line(far, back, width, dash, gap);
            // Turn from the fix round to the outbound leg, and from its end back to the inbound leg: both clockwise.
            canvas.Arc(fix + right * radius, radius, IfrChartCanvas.Angle(-right), -math.PI, width, dash, gap);
            canvas.Arc(start + right * radius, radius, IfrChartCanvas.Angle(right), -math.PI, width, dash, gap);
            canvas.Head(fix - inbound * (leg * 0.35f), inbound, head);
            canvas.Head(far - inbound * (leg * 0.65f), -inbound, head);

            // The limits inside, along the axis, the first line on top as the text reads: as large as fits, at most the
            // size of the chart.
            float textAngle = IfrChartCanvas.Readable(angle);
            float2 up = new float2(-math.sin(textAngle), math.cos(textAngle));
            float2 middle = fix - inbound * (leg * 0.5f) + right * radius;
            float longest = 0f;
            for (int i = 0; i < lines.Length; i++)
            {
                longest = math.max(longest, IfrChartCanvas.TextWidth(lines[i], 1f));
            }
            float height = math.min(kHoldTextMax, math.min(leg * 0.9f / math.max(longest, 0.1f),
                (2f * radius - 6f * kTx) / (lines.Length * 1.45f)));
            for (int i = 0; i < lines.Length; i++)
            {
                float offset = ((lines.Length - 1) * 0.5f - i) * height * 1.45f;
                canvas.Text(lines[i], middle + up * offset, textAngle, height, height * kTextStroke);
            }
            canvas.Box(middle, new float2(leg * 0.45f, lines.Length * height * 0.8f), angle);

            // Each leg's course outside it.
            float2 inMiddle = fix - inbound * (leg * 0.5f);
            HoldCourse(canvas, Course(inbound), inMiddle, inbound, -right, leg, textAngle);
            HoldCourse(canvas, Course(-inbound), inMiddle + right * (2f * radius), inbound, right, leg, textAngle);
        }

        /// <summary>A hold's speed limit: the highest holding speed among the airplane types, shown knots up to the next 10.</summary>
        private static string HoldSpeed(float knots)
        {
            return "MAX SPD " + ((int)math.ceil(knots / 10f) * 10).ToString(CultureInfo.InvariantCulture) + " KT IAS";
        }

        /// <summary>A hold's leg length ("1.5 NM"): the tower flies distance legs, as RNAV holds.</summary>
        private static string HoldLeg(float meters)
        {
            return (meters / Setting.kMetersPerNauticalMile).ToString("0.#", CultureInfo.InvariantCulture) + " NM";
        }

        private static void HoldCourse(IfrChartCanvas canvas, string text, float2 middle, float2 inbound, float2 outside, float leg, float angle)
        {
            float height = kHoldCourseHeight;
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(10);
            float[] shares = { 0f, -0.2f, 0.2f, -0.35f, 0.35f };
            for (int o = 0; o < 2; o++)
            {
                float offset = (o == 0 ? 9f : 15f) * kTx;
                for (int k = 0; k < shares.Length; k++)
                {
                    spots.Add(new IfrChartCanvas.Spot { m_Center = middle + inbound * (leg * shares[k]) + outside * offset, m_Angle = angle });
                }
            }
            float2 half = new float2(IfrChartCanvas.TextWidth(text, height) * 0.5f + 2f * kTx, height * 0.5f + 2f * kTx);
            canvas.AddLabel(kPriorityHoldCourse, false, half, spots, (c, spot) =>
            {
                c.Color = kInk;
                c.Text(text, spot.m_Center, spot.m_Angle, height, height * kTextStroke);
            });
        }

        /// <summary>Feet with the thousands spaced, as the AIP writes elevations ("1 506").</summary>
        internal static string Spaced(int feet)
        {
            string digits = feet.ToString(CultureInfo.InvariantCulture);
            for (int i = digits.Length - 3; i > 0; i -= 3)
            {
                digits = digits.Insert(i, " ");
            }
            return digits;
        }

        private static int ElevationFeet(float y, float seaLevel) => (int)math.ceil(math.max(0f, y - seaLevel) / 0.3048f);

        /// <summary>
        /// An obstacle: the VFR chart's shapes (the spire from 1,000 ft above the ground, else the ^, each with its foot dot;
        /// no lighting marks), a terrain high point a dot; its elevation above sea level as a label on the freest side, with
        /// its number in a circle for a SID's climb gradient notes. The symbol is centred on the obstacle's point.
        /// </summary>
        private static void Obstacle(IfrChartCanvas canvas, in ChartObstacle obstacle, float seaLevel)
        {
            float2 p = obstacle.m_Top.xz;
            bool terrain = obstacle.m_Height < 0f, tall = !terrain && obstacle.m_Height >= kTallObstacleHeight;
            float size = terrain ? kTerrainObstacleSize : tall ? kTallObstacleSize : kLowObstacleSize;
            canvas.Color = kInk;
            if (terrain)
            {
                canvas.Dot(p, kTerrainDot);
            }
            else
            {
                ObstacleSymbols.AddToCanvas(canvas, p - new float2(0f, size * 0.5f), tall, size);
            }
            canvas.Box(p, new float2(size * (tall || terrain ? 0.34f : 0.42f), size * 0.5f), 0f);

            string text = Spaced(ElevationFeet(obstacle.m_Top.y, seaLevel));
            int number = obstacle.m_Number;
            float numberSpace = number > 0 ? 8f * kTx : 0f;
            float2 half = new float2(IfrChartCanvas.TextWidth(text, kObstacleTextHeight) * 0.5f + 2f * kTx + numberSpace, 7f * kTx);
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(24);
            for (int e = 0; e < 3; e++)
            {
                for (int i = 0; i < kObstacleDirections.Length; i++)
                {
                    float2 v = kObstacleDirections[i];
                    float distance = size * 0.5f + 3f * kTx + e * 12f * kTx + math.abs(v.x) * half.x + math.abs(v.y) * half.y;
                    spots.Add(new IfrChartCanvas.Spot { m_Center = p + v * distance, m_Angle = 0f });
                }
            }
            canvas.AddLabel(kPriorityObstacle, true, half, spots, (c, spot) =>
            {
                c.Color = kInk;
                c.Text(text, spot.m_Center - new float2(numberSpace, 0f), 0f, kObstacleTextHeight, kObstacleTextHeight * kTextStroke);
                if (number > 0)
                {
                    float2 at = spot.m_Center + new float2(half.x - 8f * kTx, 0f);
                    c.Circle(at, 6f * kTx, 1.1f * kTx);
                    c.Text(number.ToString(CultureInfo.InvariantCulture), at, 0f, 7f * kTx, 7f * kTx * kTextStroke);
                }
            });
        }

        private static void Obstacles(IfrChartCanvas canvas, List<ChartObstacle> obstacles, float seaLevel)
        {
            if (obstacles == null)
            {
                return;
            }
            for (int i = 0; i < obstacles.Count; i++)
            {
                Obstacle(canvas, obstacles[i], seaLevel);
            }
        }

        /// <summary>An airway: a thin grey line, its designator along it beside the line, no box (AIP).</summary>
        private static void Airway(IfrChartCanvas canvas, float2 a, float2 b, string designator)
        {
            Color ink = canvas.Color;
            canvas.Color = kAirwayGrey;
            canvas.Line(a, b, kAirwayWidth);
            canvas.Color = kInk;
            AlongLabel(canvas, designator, kAirwayNameHeight, kTextStroke, a, b, 1, 10f * kTx, kAirwayShares, kPriorityAirway);
            canvas.Color = ink;
        }
    }
}
