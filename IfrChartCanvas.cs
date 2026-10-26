using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// What the IFR chart draws, built once when the shown charts change and replayed every frame: strokes on the map (x, z;
    /// north-up, lines projected on the terrain) and the label placement of docs/ifr-symbols-preview.html. Every line,
    /// symbol and fixed text is an obstacle (an oriented box); the labels are then placed in priority order, each at the
    /// first of its candidate spots that hits nothing placed so far; one with no free spot is left out, unless it must stay,
    /// then it takes the spot that hits least.
    /// </summary>
    internal sealed class IfrChartCanvas
    {
        /// <summary>Metres per pixel of the preview, whose sizes the chart keeps (1 px = 40 m there).</summary>
        internal const float kPreviewPx = 40f;

        /// <summary>
        /// Metres per preview pixel for the chart being built: the preview's 40 m, scaled down with the holds (user,
        /// 2026-10-26: the holds at the tower's real size looked odd among full-size lines; zoom in to read).
        /// </summary>
        internal static float Px { get; private set; } = kPreviewPx;

        /// <summary>Sets the chart's size against the preview's (1 = the preview's sizes).</summary>
        internal static void SetScale(float scale) => Px = kPreviewPx * scale;

        // Arcs: Bezier pieces of at most 90 degrees; obstacle boxes along them every 30 degrees.
        private const float kArcPiece = math.PI / 2f;
        private const float kArcObstaclePiece = math.PI / 6f;

        // A filled arrowhead is drawn as this many lines fanning from its tip (the overlay only draws lines).
        private const int kHeadStrips = 10;

        private struct Stroke
        {
            public Bezier4x3 m_Curve;
            public float m_Width;
            public float m_Dash;
            public float m_Gap;
            public bool m_Round;
            public Color m_Color;
        }

        private struct Obstacle
        {
            public float2 m_Center;
            public float2 m_Half;
            public float m_Angle;
        }

        internal struct Spot
        {
            public float2 m_Center;

            /// <summary>Direction of the text's baseline, radians from +x towards +z.</summary>
            public float m_Angle;
        }

        private sealed class Label
        {
            public int m_Priority;
            public int m_Order;
            public bool m_Keep;
            public float2 m_Half;
            public List<Spot> m_Spots;
            public Action<IfrChartCanvas, Spot> m_Render;
        }

        private readonly List<Stroke> m_Strokes = new List<Stroke>();
        private readonly List<Obstacle> m_Boxes = new List<Obstacle>();
        private readonly List<Label> m_Labels = new List<Label>();

        /// <summary>Colour of the strokes added from now on.</summary>
        public Color Color { get; set; } = Color.black;

        /// <summary>Height the strokes are given (they are projected on the terrain).</summary>
        public float Height { get; set; }

        public bool IsEmpty => m_Strokes.Count == 0;

        public void Clear()
        {
            m_Strokes.Clear();
            m_Boxes.Clear();
            m_Labels.Clear();
        }

        public void Draw(OverlayRenderSystem.Buffer buffer)
        {
            for (int i = 0; i < m_Strokes.Count; i++)
            {
                Stroke stroke = m_Strokes[i];
                Color color = stroke.m_Color;
                if (stroke.m_Dash > 0f)
                {
                    buffer.DrawDashedCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, stroke.m_Curve, stroke.m_Width, stroke.m_Dash, stroke.m_Gap);
                }
                else
                {
                    buffer.DrawCurve(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, stroke.m_Curve, stroke.m_Width,
                        stroke.m_Round ? new float2(1f, 1f) : default);
                }
            }
        }

        // ---------------------------------------------------------------- drawing

        private float3 At(float2 p) => new float3(p.x, Height, p.y);

        /// <summary>A straight line, an obstacle for the labels unless <paramref name="obstacle"/> is false.</summary>
        public void Line(float2 a, float2 b, float width, float dash = 0f, float gap = 0f, bool obstacle = true, bool round = true)
        {
            if (math.distancesq(a, b) < 1e-4f)
            {
                return;
            }
            m_Strokes.Add(new Stroke { m_Curve = NetUtils.StraightCurve(At(a), At(b)), m_Width = width, m_Dash = dash, m_Gap = gap, m_Round = round, m_Color = Color });
            if (obstacle)
            {
                Segment(a, b, width);
            }
        }

        /// <summary>A filled dot: a very short line with round ends as wide as the dot.</summary>
        public void Dot(float2 center, float diameter)
        {
            Line(center, center + new float2(0.05f, 0f), diameter, 0f, 0f, false);
        }

        /// <summary>A closed outline through the points.</summary>
        public void Polygon(float2[] points, float width)
        {
            for (int i = 0; i < points.Length; i++)
            {
                Line(points[i], points[(i + 1) % points.Length], width, 0f, 0f, false);
            }
        }

        /// <summary>
        /// An arc round <paramref name="center"/> from angle <paramref name="from"/> through <paramref name="sweep"/> radians
        /// (counter-clockwise when positive, seen north-up), an obstacle along its chords.
        /// </summary>
        public void Arc(float2 center, float radius, float from, float sweep, float width, float dash = 0f, float gap = 0f)
        {
            int pieces = math.max(1, (int)math.ceil(math.abs(sweep) / kArcPiece - 1e-3f));
            float step = sweep / pieces;
            for (int i = 0; i < pieces; i++)
            {
                float t0 = from + step * i, t1 = t0 + step;
                float k = 4f / 3f * math.tan(step / 4f) * radius;
                float2 p0 = center + radius * new float2(math.cos(t0), math.sin(t0));
                float2 p3 = center + radius * new float2(math.cos(t1), math.sin(t1));
                float2 p1 = p0 + k * new float2(-math.sin(t0), math.cos(t0));
                float2 p2 = p3 - k * new float2(-math.sin(t1), math.cos(t1));
                m_Strokes.Add(new Stroke { m_Curve = new Bezier4x3(At(p0), At(p1), At(p2), At(p3)), m_Width = width, m_Dash = dash, m_Gap = gap, m_Round = true, m_Color = Color });
            }
            int boxes = math.max(1, (int)math.ceil(math.abs(sweep) / kArcObstaclePiece));
            float boxStep = sweep / boxes;
            for (int i = 0; i < boxes; i++)
            {
                float t0 = from + boxStep * i, t1 = t0 + boxStep;
                Segment(center + radius * new float2(math.cos(t0), math.sin(t0)), center + radius * new float2(math.cos(t1), math.sin(t1)), width);
            }
        }

        /// <summary>A full circle (an outline).</summary>
        public void Circle(float2 center, float radius, float width) => Arc(center, radius, 0f, 2f * math.PI, width);

        /// <summary>A filled arrowhead with its tip at <paramref name="tip"/>, pointing along <paramref name="direction"/>.</summary>
        public void Head(float2 tip, float2 direction, float size)
        {
            float2 side = new float2(-direction.y, direction.x);
            float half = size * 0.45f;
            // A fan of lines from the tip to points along the base (parallel strips of different lengths left a staircase
            // along the sides), then a thin line down each side so the edges are straight.
            float width = 2f * half / kHeadStrips;
            float2 baseCenter = tip - direction * size;
            for (int i = 0; i < kHeadStrips; i++)
            {
                float offset = -half + width * (i + 0.5f);
                Line(tip - direction * (width * 0.5f), baseCenter + side * offset, width * 1.3f, 0f, 0f, false, false);
            }
            float edge = width * 0.6f;
            Line(tip, baseCenter + side * half, edge, 0f, 0f, false);
            Line(tip, baseCenter - side * half, edge, 0f, 0f, false);
            Line(baseCenter + side * half, baseCenter - side * half, edge, 0f, 0f, false);
            Box(tip - direction * size * 0.5f, new float2(size * 0.5f, half), math.atan2(direction.y, direction.x));
        }

        /// <summary>
        /// Text centred on <paramref name="center"/>, its baseline along <paramref name="angle"/>, characters
        /// <paramref name="height"/> high with strokes <paramref name="stroke"/> wide. Not an obstacle by itself.
        /// </summary>
        public void Text(string text, float2 center, float angle, float height, float stroke)
        {
            float2 right = new float2(math.cos(angle), math.sin(angle));
            float2 up = new float2(-right.y, right.x);
            float2 origin = center - right * (TextWidth(text, height) * 0.5f) - up * (height * 0.5f);
            for (int i = 0; i < text.Length; i++)
            {
                float[][] glyph = ChartLabels.Glyph(text[i]);
                if (glyph == null)
                {
                    continue;
                }
                float2 corner = origin + right * (i * (ChartLabels.kWidth + ChartLabels.kGap) * height);
                for (int s = 0; s < glyph.Length; s++)
                {
                    float[] points = glyph[s];
                    for (int p = 2; p < points.Length; p += 2)
                    {
                        float2 a = corner + (right * points[p - 2] + up * points[p - 1]) * height;
                        float2 b = corner + (right * points[p] + up * points[p + 1]) * height;
                        Line(a, b, stroke, 0f, 0f, false);
                    }
                }
            }
        }

        public static float TextWidth(string text, float height) => ChartLabels.TextWidth(text, height);

        /// <summary>A direction turned by half a turn where text along it would read upside down.</summary>
        public static float Readable(float angle)
        {
            return angle > math.PI / 2f ? angle - math.PI : angle < -math.PI / 2f ? angle + math.PI : angle;
        }

        public static float Angle(float2 direction) => math.atan2(direction.y, direction.x);

        // ---------------------------------------------------------------- label placement

        /// <summary>A line as an obstacle for the labels.</summary>
        public void Segment(float2 a, float2 b, float width)
        {
            float length = math.distance(a, b);
            if (length > 0.5f * Px)
            {
                Box((a + b) * 0.5f, new float2(length * 0.5f, width * 0.5f + 1.5f * Px), Angle(b - a));
            }
        }

        public void Box(float2 center, float2 half, float angle)
        {
            m_Boxes.Add(new Obstacle { m_Center = center, m_Half = half, m_Angle = angle });
        }

        /// <summary>
        /// A label of <paramref name="half"/> size, tried at <paramref name="spots"/> in order; lower priorities are placed
        /// first; <paramref name="render"/> draws it at the spot taken.
        /// </summary>
        public void AddLabel(int priority, bool keep, float2 half, List<Spot> spots, Action<IfrChartCanvas, Spot> render)
        {
            m_Labels.Add(new Label { m_Priority = priority, m_Order = m_Labels.Count, m_Keep = keep, m_Half = half, m_Spots = spots, m_Render = render });
        }

        public void Place()
        {
            m_Labels.Sort((a, b) => a.m_Priority != b.m_Priority ? a.m_Priority - b.m_Priority : a.m_Order - b.m_Order);
            for (int i = 0; i < m_Labels.Count; i++)
            {
                Label label = m_Labels[i];
                int best = -1, bestHits = int.MaxValue;
                for (int s = 0; s < label.m_Spots.Count && bestHits > 0; s++)
                {
                    Obstacle box = new Obstacle { m_Center = label.m_Spots[s].m_Center, m_Half = label.m_Half, m_Angle = label.m_Spots[s].m_Angle };
                    int hits = 0;
                    for (int o = 0; o < m_Boxes.Count && hits < bestHits; o++)
                    {
                        if (Overlaps(box, m_Boxes[o]))
                        {
                            hits++;
                        }
                    }
                    if (hits < bestHits)
                    {
                        best = s;
                        bestHits = hits;
                    }
                }
                if (best < 0 || (bestHits > 0 && !label.m_Keep))
                {
                    continue;
                }
                Spot spot = label.m_Spots[best];
                Box(spot.m_Center, label.m_Half, spot.m_Angle);
                label.m_Render(this, spot);
            }
            m_Labels.Clear();
        }

        /// <summary>Separating axis test of two oriented boxes.</summary>
        private static bool Overlaps(in Obstacle a, in Obstacle b)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                float t = axis < 2 ? a.m_Angle + axis * math.PI / 2f : b.m_Angle + (axis - 2) * math.PI / 2f;
                float2 unit = new float2(math.cos(t), math.sin(t));
                if (math.abs(math.dot(b.m_Center - a.m_Center, unit)) > Reach(a, t) + Reach(b, t))
                {
                    return false;
                }
            }
            return true;
        }

        private static float Reach(in Obstacle box, float axis)
        {
            float d = box.m_Angle - axis;
            return math.abs(math.cos(d)) * box.m_Half.x + math.abs(math.sin(d)) * box.m_Half.y;
        }
    }
}
