using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// Obstacle symbols of the VFR chart infoview, as the FAA chart legend draws them (the shapes approved in
    /// docs/vfr-symbols-preview.html, with the same coordinates: height 1, foot at the origin, y up, north-up on the map).
    /// The overlay only draws lines, so a solid shape is filled with horizontal strips, cut once when the class loads,
    /// and edged with a thin outline that also rounds its corners. Dots are overlay circles.
    /// </summary>
    internal static class ObstacleSymbols
    {
        /// <summary>Symbol heights in metres: the tall spire (1,000 ft and over), the low ^ (under 1,000 ft), a wind turbine.</summary>
        public const float kTallSize = 150f;
        public const float kLowSize = 100f;
        public const float kTurbineSize = 110f;

        private const float kTextHeight = 0.4f;
        private const float kTextStroke = 0.08f;
        private const int kCurvePieces = 10;
        private const int kFillRows = 40;

        /// <summary>A solid shape: its outline (unit coordinates), the fill strips (y, x from, x to, height) and the outline width.</summary>
        private sealed class Shape
        {
            public float2[] m_Outline;
            public float4[] m_Strips;
            public float m_OutlineWidth;

            /// <summary>Half its width at the ground, so labels start clear of it.</summary>
            public float m_Reach;
        }

        private static readonly Shape kTallSpire = TallSpire();
        private static readonly Shape kTallGroup = TallGroup();
        private static readonly Shape kLow = Low();
        private static readonly Shape kLowGroup = LowGroup();

        // Lightning rays of a lighted obstacle: a thin line from near the top with one small sideways jog.
        private static readonly float[] kRayAngles = { 90f, 35f, 145f, -25f, 205f };

        // Wind turbine: a solid nacelle dot with an open gap round it; mast and blades start beyond the gap.
        private const float kHub = 0.72f;
        private const float kBlade = 0.3f;
        private const float kGap = 0.085f;
        private static readonly float[] kBladeAngles = { 90f, 200f, 340f };

        /// <summary>A single obstacle: the spire from 1,000 ft above the ground, else the ^; rays when lighted; "MSL (AGL)" beside it.</summary>
        public static void DrawObstacle(OverlayRenderSystem.Buffer buffer, float3 foot, bool tall, bool lighted, string top, string height, Color color)
        {
            float size = tall ? kTallSize : kLowSize;
            if (tall)
            {
                DrawShape(buffer, kTallSpire, foot, size, color);
                DrawDot(buffer, foot, new float2(0f, 0.05f), 0.045f, size, color);
                if (lighted)
                {
                    DrawRays(buffer, foot, new float2(0f, 1.02f), 0.08f, 0.3f, 0.02f, size, color);
                }
            }
            else
            {
                DrawShape(buffer, kLow, foot, size, color);
                DrawDot(buffer, foot, new float2(0f, 0.075f), 0.075f, size, color);
                if (lighted)
                {
                    DrawRays(buffer, foot, new float2(0f, 1.04f), 0.14f, 0.55f, 0.03f, size, color);
                }
            }
            DrawLabel(buffer, foot, (tall ? kTallSpire : kLow).m_Reach, size, top, height, color);
        }

        /// <summary>A group of obstacles, shaped by its highest member; only its top above sea level, as in congested areas.</summary>
        public static void DrawGroup(OverlayRenderSystem.Buffer buffer, float3 foot, bool tall, string top, Color color)
        {
            float size = tall ? kTallSize : kLowSize;
            if (tall)
            {
                DrawShape(buffer, kTallGroup, foot, size, color);
                DrawDot(buffer, foot, new float2(-0.12f, 0.05f), 0.04f, size, color);
                DrawDot(buffer, foot, new float2(0.12f, 0.05f), 0.04f, size, color);
            }
            else
            {
                DrawShape(buffer, kLowGroup, foot, size, color);
                DrawDot(buffer, foot, new float2(-0.23f, 0.075f), 0.07f, size, color);
                DrawDot(buffer, foot, new float2(0.23f, 0.075f), 0.07f, size, color);
            }
            DrawLabel(buffer, foot, (tall ? kTallGroup : kLowGroup).m_Reach, size, top, null, color);
        }

        /// <summary>One wind turbine with "MSL (AGL)" beside it.</summary>
        public static void DrawTurbine(OverlayRenderSystem.Buffer buffer, float3 foot, string top, string height, Color color)
        {
            DrawTurbineShape(buffer, foot, kTurbineSize, color);
            DrawLabel(buffer, foot, kGap + kBlade, kTurbineSize, top, height, color);
        }

        /// <summary>A few wind turbines close together: two overlapping turbines, the second smaller, and the highest top above sea level.</summary>
        public static void DrawTurbineGroup(OverlayRenderSystem.Buffer buffer, float3 foot, string top, Color color)
        {
            float size = kTurbineSize;
            DrawTurbineShape(buffer, foot - new float3(0.13f * size, 0f, 0f), size, color);
            DrawTurbineShape(buffer, foot + new float3(0.15f * size, 0f, -0.05f * size), size * 0.85f, color);
            DrawLabel(buffer, foot, 0.15f + 0.85f * (kGap + kBlade), size, top, null, color);
        }

        /// <summary>
        /// A wind farm: a dashed line along <paramref name="outline"/> (the turbines' rounded hull), and in its middle the
        /// turbine symbol with the highest top above sea level in a box beside it.
        /// </summary>
        public static void DrawWindFarm(OverlayRenderSystem.Buffer buffer, float2[] outline, float2 center, float y, string top, Color color)
        {
            float size = kTurbineSize;
            float stroke = kTextStroke * size;
            float textHeight = kTextHeight * size;
            float boxWidth = ChartLabels.TextWidth(top, textHeight) + 0.3f * size;
            float groupWidth = 2f * (kGap + kBlade) * size + 0.35f * size + boxWidth;
            float2 turbine = center + new float2((kGap + kBlade) * size - groupWidth * 0.5f, -kHub * size * 0.5f);
            // Dashes of equal length along the whole outline, the pattern running on across its corners.
            float dash = 0.3f * size;
            float along = 0f;
            for (int i = 0; i < outline.Length; i++)
            {
                float2 a = outline[i];
                float2 b = outline[(i + 1) % outline.Length];
                float length = math.distance(a, b);
                float done = 0f;
                while (done < length)
                {
                    float phase = along % (2f * dash);
                    float step = math.min(length - done, (phase < dash ? dash : 2f * dash) - phase);
                    if (phase < dash)
                    {
                        float2 from = math.lerp(a, b, done / length);
                        float2 to = math.lerp(a, b, (done + step) / length);
                        ChartLabels.DrawPiece(buffer, new float3(from.x, y, from.y), new float3(to.x, y, to.y), color, stroke);
                    }
                    done += step;
                    along += step;
                }
            }
            DrawTurbineShape(buffer, new float3(turbine.x, y, turbine.y), size, color);
            float2 textAt = turbine + new float2((kGap + kBlade + 0.35f) * size, (size - textHeight) * 0.5f);
            ChartLabels.DrawText(buffer, textAt, y, top, textHeight, color, stroke);
            float pad = 0.15f * size;
            float2 low = textAt - pad;
            float2 high = textAt + new float2(ChartLabels.TextWidth(top, textHeight), textHeight) + pad;
            ChartLabels.DrawPiece(buffer, new float3(low.x, y, low.y), new float3(high.x, y, low.y), color, stroke);
            ChartLabels.DrawPiece(buffer, new float3(high.x, y, low.y), new float3(high.x, y, high.y), color, stroke);
            ChartLabels.DrawPiece(buffer, new float3(high.x, y, high.y), new float3(low.x, y, high.y), color, stroke);
            ChartLabels.DrawPiece(buffer, new float3(low.x, y, high.y), new float3(low.x, y, low.y), color, stroke);
        }

        private static void DrawTurbineShape(OverlayRenderSystem.Buffer buffer, float3 foot, float size, Color color)
        {
            float2 at = foot.xz;
            float y = foot.y;
            float2 hub = at + new float2(0f, kHub * size);
            DrawBar(buffer, at, at + new float2(0f, (kHub - kGap) * size), y, 0.065f * size, color);
            DrawBar(buffer, at - new float2(0.13f * size, 0f), at + new float2(0.13f * size, 0f), y, 0.065f * size, color);
            for (int i = 0; i < kBladeAngles.Length; i++)
            {
                float2 d = Direction(kBladeAngles[i]);
                DrawBar(buffer, hub + d * kGap * size, hub + d * (kGap + kBlade) * size, y, 0.055f * size, color);
            }
            DrawDot(buffer, foot, new float2(0f, kHub), 0.045f, size, color);
        }

        /// <summary>"MSL" over "(AGL)" (or MSL alone) to the right of a symbol reaching <paramref name="reach"/> × size from its foot.</summary>
        private static void DrawLabel(OverlayRenderSystem.Buffer buffer, float3 foot, float reach, float size, string top, string height, Color color)
        {
            float textHeight = kTextHeight * size;
            float stroke = kTextStroke * size;
            float left = foot.x + (reach + 0.2f) * size;
            if (height == null)
            {
                ChartLabels.DrawText(buffer, new float2(left, foot.z + (size - textHeight) * 0.5f), foot.y, top, textHeight, color, stroke);
                return;
            }
            ChartLabels.DrawText(buffer, new float2(left, foot.z + size - textHeight), foot.y, top, textHeight, color, stroke);
            ChartLabels.DrawText(buffer, new float2(left, foot.z), foot.y, height, textHeight, color, stroke);
        }

        private static void DrawShape(OverlayRenderSystem.Buffer buffer, Shape shape, float3 foot, float size, Color color)
        {
            float y = foot.y;
            for (int i = 0; i < shape.m_Strips.Length; i++)
            {
                float4 strip = shape.m_Strips[i];
                float z = foot.z + strip.x * size;
                buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected,
                    new Line3.Segment(new float3(foot.x + strip.y * size, y, z), new float3(foot.x + strip.z * size, y, z)), strip.w * size, new float2(0f, 0f));
            }
            float width = shape.m_OutlineWidth * size;
            float2[] outline = shape.m_Outline;
            for (int i = 0; i < outline.Length; i++)
            {
                float2 a = foot.xz + outline[i] * size;
                float2 b = foot.xz + outline[(i + 1) % outline.Length] * size;
                ChartLabels.DrawPiece(buffer, new float3(a.x, y, a.y), new float3(b.x, y, b.y), color, width);
            }
        }

        private static void DrawDot(OverlayRenderSystem.Buffer buffer, float3 foot, float2 at, float radius, float size, Color color)
        {
            float3 position = new float3(foot.x + at.x * size, foot.y, foot.z + at.y * size);
            buffer.DrawCircle(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new float2(0f, 1f), position, 2f * radius * size);
        }

        /// <summary>A straight bar with square ends (mast, base, blade).</summary>
        private static void DrawBar(OverlayRenderSystem.Buffer buffer, float2 from, float2 to, float y, float width, Color color)
        {
            buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(new float3(from.x, y, from.y), new float3(to.x, y, to.y)), width, new float2(0f, 0f));
        }

        private static void DrawRays(OverlayRenderSystem.Buffer buffer, float3 foot, float2 apex, float from, float to, float width, float size, Color color)
        {
            float jog = (to - from) * 0.09f;
            for (int i = 0; i < kRayAngles.Length; i++)
            {
                float2 d = Direction(kRayAngles[i]);
                float2 n = new float2(-d.y, d.x);
                float2 a = apex + d * from;
                float2 b = apex + d * math.lerp(from, to, 0.52f) + n * jog;
                float2 c = apex + d * math.lerp(from, to, 0.44f) - n * jog;
                float2 e = apex + d * to;
                RayPiece(buffer, foot, a, b, width, size, color);
                RayPiece(buffer, foot, b, c, width, size, color);
                RayPiece(buffer, foot, c, e, width, size, color);
            }
        }

        private static void RayPiece(OverlayRenderSystem.Buffer buffer, float3 foot, float2 a, float2 b, float width, float size, Color color)
        {
            ChartLabels.DrawPiece(buffer, foot + new float3(a.x * size, 0f, a.y * size), foot + new float3(b.x * size, 0f, b.y * size), color, width * size);
        }

        private static float2 Direction(float degrees) => new float2(math.cos(math.radians(degrees)), math.sin(math.radians(degrees)));

        // ---- Shapes (unit coordinates, as in the approved preview) ----

        private static Shape TallSpire()
        {
            const float r = 0.025f;
            Path path = new Path(new float2(-0.33f, 0f));
            path.Cubic(new float2(-0.22f, 0.14f), new float2(-r, 0.3f), new float2(-r, 0.62f));
            path.Line(new float2(-r, 1f - r));
            path.TopArc(new float2(0f, 1f - r), r);
            path.Line(new float2(r, 0.62f));
            path.Cubic(new float2(r, 0.3f), new float2(0.22f, 0.14f), new float2(0.33f, 0f));
            path.Cubic(new float2(0.22f, 0.07f), new float2(0.105f, 0.15f), new float2(0f, 0.23f));
            path.Cubic(new float2(-0.105f, 0.15f), new float2(-0.22f, 0.07f), new float2(-0.33f, 0f));
            return path.ToShape(0.035f, 0.33f);
        }

        private static Shape TallGroup()
        {
            const float r = 0.025f, a = -0.12f, b = 0.12f;
            Path path = new Path(new float2(-0.46f, 0f));
            path.Cubic(new float2(-0.34f, 0.14f), new float2(a - r, 0.32f), new float2(a - r, 0.66f));
            path.Line(new float2(a - r, 1f - r));
            path.TopArc(new float2(a, 1f - r), r);
            path.Line(new float2(a + r, 0.72f));
            path.Cubic(new float2(a + r, 0.5f), new float2(-0.03f, 0.44f), new float2(0f, 0.36f));
            path.Cubic(new float2(0.03f, 0.44f), new float2(b - r, 0.5f), new float2(b - r, 0.72f));
            path.Line(new float2(b - r, 1f - r));
            path.TopArc(new float2(b, 1f - r), r);
            path.Line(new float2(b + r, 0.66f));
            path.Cubic(new float2(b + r, 0.32f), new float2(0.34f, 0.14f), new float2(0.46f, 0f));
            path.Cubic(new float2(0.34f, 0.07f), new float2(0.23f, 0.15f), new float2(b, 0.22f));
            path.Cubic(new float2(0.08f, 0.17f), new float2(0.05f, 0.12f), new float2(0.03f, 0.1f));
            path.Quadratic(new float2(0f, 0.075f), new float2(-0.03f, 0.1f));
            path.Cubic(new float2(-0.05f, 0.12f), new float2(-0.08f, 0.17f), new float2(a, 0.22f));
            path.Cubic(new float2(-0.23f, 0.15f), new float2(-0.34f, 0.07f), new float2(-0.46f, 0f));
            return path.ToShape(0.035f, 0.46f);
        }

        // The low ^ and M are bands of even width: outer half-width 0.41 at the ground, legs 0.13 wide, notch sides parallel
        // to the outer sides.
        private const float kLowSpread = 0.41f;
        private const float kLowLeg = 0.13f;
        private const float kLowGroupPeak = 0.23f;

        private static Shape Low()
        {
            float notch = 1f - kLowLeg / kLowSpread;
            Path path = new Path(new float2(-kLowSpread, 0f));
            path.Line(new float2(0f, 1f));
            path.Line(new float2(kLowSpread, 0f));
            path.Line(new float2(kLowSpread - kLowLeg, 0f));
            path.Line(new float2(0f, notch));
            path.Line(new float2(-kLowSpread + kLowLeg, 0f));
            return path.ToShape(0.012f, kLowSpread);
        }

        private static Shape LowGroup()
        {
            float a = kLowGroupPeak, s = kLowSpread, w = kLowLeg;
            Path path = new Path(new float2(-a - s, 0f));
            path.Line(new float2(-a, 1f));
            path.Line(new float2(0f, 1f - a / s));
            path.Line(new float2(a, 1f));
            path.Line(new float2(a + s, 0f));
            path.Line(new float2(a + s - w, 0f));
            path.Line(new float2(a, 1f - w / s));
            path.Line(new float2(0f, 1f - (a + w) / s));
            path.Line(new float2(-a, 1f - w / s));
            path.Line(new float2(-a - s + w, 0f));
            return path.ToShape(0.012f, a + s);
        }

        /// <summary>Builds an outline from lines and curves, then cuts it into fill strips (even-odd scanlines).</summary>
        private sealed class Path
        {
            private readonly List<float2> m_Points = new List<float2>();

            public Path(float2 start)
            {
                m_Points.Add(start);
            }

            private float2 Last => m_Points[m_Points.Count - 1];

            public void Line(float2 to) => m_Points.Add(to);

            public void Cubic(float2 c1, float2 c2, float2 to)
            {
                float2 from = Last;
                for (int i = 1; i <= kCurvePieces; i++)
                {
                    float t = (float)i / kCurvePieces;
                    float u = 1f - t;
                    m_Points.Add(u * u * u * from + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * to);
                }
            }

            public void Quadratic(float2 c, float2 to)
            {
                float2 from = Last;
                for (int i = 1; i <= kCurvePieces; i++)
                {
                    float t = (float)i / kCurvePieces;
                    float u = 1f - t;
                    m_Points.Add(u * u * from + 2f * u * t * c + t * t * to);
                }
            }

            /// <summary>The upper half circle from the left side of <paramref name="center"/> over the top to its right side.</summary>
            public void TopArc(float2 center, float radius)
            {
                for (int i = 1; i <= kCurvePieces; i++)
                {
                    float angle = math.radians(180f - 180f * i / kCurvePieces);
                    m_Points.Add(center + new float2(math.cos(angle), math.sin(angle)) * radius);
                }
            }

            public Shape ToShape(float outlineWidth, float reach)
            {
                // The path ends where it started; drop the repeated point.
                if (math.distancesq(m_Points[0], Last) < 1e-8f)
                {
                    m_Points.RemoveAt(m_Points.Count - 1);
                }
                float2[] outline = m_Points.ToArray();
                float low = float.MaxValue, high = float.MinValue;
                for (int i = 0; i < outline.Length; i++)
                {
                    low = math.min(low, outline[i].y);
                    high = math.max(high, outline[i].y);
                }
                List<float4> strips = new List<float4>();
                List<float> crossings = new List<float>();
                float row = (high - low) / kFillRows;
                for (int r = 0; r < kFillRows; r++)
                {
                    float y = low + (r + 0.5f) * row;
                    crossings.Clear();
                    for (int i = 0; i < outline.Length; i++)
                    {
                        float2 a = outline[i];
                        float2 b = outline[(i + 1) % outline.Length];
                        if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))
                        {
                            crossings.Add(a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x));
                        }
                    }
                    crossings.Sort();
                    for (int i = 0; i + 1 < crossings.Count; i += 2)
                    {
                        // A hair taller than the row, so neighbouring strips join without a seam.
                        strips.Add(new float4(y, crossings[i], crossings[i + 1], row * 1.05f));
                    }
                }
                return new Shape { m_Outline = outline, m_Strips = strips.ToArray(), m_OutlineWidth = outlineWidth, m_Reach = reach };
            }
        }
    }
}
