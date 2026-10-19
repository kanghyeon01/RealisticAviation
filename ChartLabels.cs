using Colossal.Mathematics;
using Game.Rendering;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// Airspace altitude labels drawn on the map as on a VFR sectional chart: the ceiling over a bar over the floor in
    /// hundreds of feet ("100 / 30", "40 / SFC"), or the class D ceiling between four corner brackets with a minus
    /// ("-25": up to but not including 2,500 ft). The overlay can only draw lines, so the characters are a small stroke font. North-up, like the chart.
    /// </summary>
    internal static class ChartLabels
    {
        /// <summary>Character width and the gap after it, as shares of the character height.</summary>
        private const float kWidth = 0.6f;
        private const float kGap = 0.3f;

        /// <summary>Space between the bar and the numbers above and below it, as a share of the character height.</summary>
        private const float kBarGap = 0.25f;

        // Strokes of each character on a box 0.6 wide and 1 high, as polylines (x, y pairs): a simple sans-serif with
        // curves made of short pieces, in the manner of the Hershey plotter fonts. Built once.
        private static readonly float[][][] kDigits =
        {
            // 0: an oval.
            new[] { Arc(0.3f, 0.5f, 0.3f, 0.5f, 90f, 450f) },
            // 1: a flag and a stem.
            new[] { new[] { 0.12f, 0.8f, 0.34f, 1f, 0.34f, 0f } },
            // 2: a hook over a diagonal and a base.
            new[] { Join(Arc(0.3f, 0.72f, 0.28f, 0.28f, 160f, -35f), new[] { 0f, 0f, 0.6f, 0f }) },
            // 3: two bowls opening left.
            new[] { Join(Arc(0.3f, 0.76f, 0.26f, 0.24f, 150f, -90f), Arc(0.3f, 0.27f, 0.3f, 0.27f, 90f, -150f)) },
            // 4: stem, diagonal and crossbar.
            new[] { new[] { 0.45f, 0f, 0.45f, 1f, 0f, 0.3f, 0.6f, 0.3f } },
            // 5: top bar, short stem, bowl.
            new[] { Join(new[] { 0.55f, 1f, 0.1f, 1f }, Arc(0.3f, 0.32f, 0.3f, 0.32f, 128f, -145f)) },
            // 6: curve down the left into a bowl.
            new[] { Arc(0.3f, 0.5f, 0.3f, 0.5f, 60f, 200f), Arc(0.3f, 0.3f, 0.3f, 0.3f, 180f, 540f) },
            // 7: top bar and diagonal.
            new[] { new[] { 0f, 1f, 0.6f, 1f, 0.2f, 0f } },
            // 8: two stacked loops.
            new[] { Arc(0.3f, 0.77f, 0.25f, 0.23f, 270f, 630f), Arc(0.3f, 0.27f, 0.3f, 0.27f, 90f, 450f) },
            // 9: a loop and a curve down the right.
            new[] { Arc(0.3f, 0.7f, 0.3f, 0.3f, 0f, 360f), Arc(0.3f, 0.5f, 0.3f, 0.5f, 25f, -120f) },
        };

        // S: a bowl opening right over a bowl opening left.
        private static readonly float[][] kS = { Join(Arc(0.3f, 0.75f, 0.28f, 0.25f, 30f, 270f), Arc(0.3f, 0.25f, 0.3f, 0.25f, 90f, -150f)) };
        private static readonly float[][] kF = { new[] { 0.55f, 1f, 0f, 1f, 0f, 0f }, new[] { 0f, 0.52f, 0.42f, 0.52f } };
        // C: an open arc.
        private static readonly float[][] kC = { Arc(0.32f, 0.5f, 0.3f, 0.5f, 45f, 315f) };
        // Parentheses: tall arcs reaching a little below and above the digits.
        private static readonly float[][] kOpen = { Arc(0.55f, 0.5f, 0.3f, 0.62f, 125f, 235f) };
        private static readonly float[][] kClose = { Arc(0.05f, 0.5f, 0.3f, 0.62f, 55f, -55f) };
        // Letters for "MODE C ... NM".
        private static readonly float[][] kM = { new[] { 0f, 0f, 0f, 1f, 0.3f, 0.4f, 0.6f, 1f, 0.6f, 0f } };
        private static readonly float[][] kD = { Join(Join(new[] { 0f, 0f, 0f, 1f, 0.2f, 1f }, Arc(0.2f, 0.5f, 0.4f, 0.5f, 90f, -90f)), new[] { 0f, 0f }) };
        private static readonly float[][] kE = { new[] { 0.6f, 1f, 0f, 1f, 0f, 0f, 0.6f, 0f }, new[] { 0f, 0.52f, 0.45f, 0.52f } };
        private static readonly float[][] kN = { new[] { 0f, 0f, 0f, 1f, 0.6f, 0f, 0.6f, 1f } };
        // Minus, for "up to but not including" (the class D ceiling).
        private static readonly float[][] kMinus = { new[] { 0.08f, 0.5f, 0.52f, 0.5f } };

        /// <summary>An elliptic arc from one angle to the other (degrees, counter-clockwise when increasing), in pieces of 15°.</summary>
        private static float[] Arc(float cx, float cy, float rx, float ry, float from, float to)
        {
            int pieces = math.max(2, (int)math.ceil(math.abs(to - from) / 15f));
            float[] points = new float[(pieces + 1) * 2];
            for (int i = 0; i <= pieces; i++)
            {
                float angle = math.radians(math.lerp(from, to, (float)i / pieces));
                points[i * 2] = cx + rx * math.cos(angle);
                points[i * 2 + 1] = cy + ry * math.sin(angle);
            }
            return points;
        }

        private static float[] Join(float[] a, float[] b)
        {
            float[] points = new float[a.Length + b.Length];
            a.CopyTo(points, 0);
            b.CopyTo(points, a.Length);
            return points;
        }

        public static float TextWidth(string text, float height) => text.Length == 0 ? 0f : (text.Length * (kWidth + kGap) - kGap) * height;

        /// <summary>"Ceiling / floor" centred on <paramref name="center"/>, characters <paramref name="height"/> metres high.</summary>
        public static void DrawFraction(OverlayRenderSystem.Buffer buffer, float2 center, float y, string top, string bottom,
            float height, Color color, float width)
        {
            float half = (math.max(TextWidth(top, height), TextWidth(bottom, height)) * 0.5f) + kGap * height;
            DrawPiece(buffer, new float3(center.x - half, y, center.y), new float3(center.x + half, y, center.y), color, width);
            DrawText(buffer, new float2(center.x - TextWidth(top, height) * 0.5f, center.y + kBarGap * height), y, top, height, color, width);
            DrawText(buffer, new float2(center.x - TextWidth(bottom, height) * 0.5f, center.y - (1f + kBarGap) * height), y, bottom, height, color, width);
        }

        /// <summary>Text centred on <paramref name="center"/> between four L-shaped corner brackets (the class D ceiling).</summary>
        public static void DrawBracketed(OverlayRenderSystem.Buffer buffer, float2 center, float y, string text, float height, Color color, float width)
        {
            float textWidth = TextWidth(text, height);
            DrawText(buffer, center - new float2(textWidth, height) * 0.5f, y, text, height, color, width);
            float2 half = new float2(textWidth * 0.5f, height * 0.5f) + 0.4f * height;
            float arm = 0.45f * height;
            for (int corner = 0; corner < 4; corner++)
            {
                // Each corner an L: one arm along the top or bottom, one along the side, both pointing into the box.
                float2 sign = new float2(corner % 2 == 0 ? -1f : 1f, corner < 2 ? -1f : 1f);
                float2 at = center + half * sign;
                DrawPiece(buffer, new float3(at.x - sign.x * arm, y, at.y), new float3(at.x, y, at.y), color, width);
                DrawPiece(buffer, new float3(at.x, y, at.y), new float3(at.x, y, at.y - sign.y * arm), color, width);
            }
        }

        /// <summary>Text with its bottom-left corner at <paramref name="origin"/>.</summary>
        /// <summary>
        /// Text along the inside of a circle, centred on <paramref name="angle"/> (radians from +x towards +z): each character
        /// turned with the circle, reading left to right with its top towards the centre on the lower half and away from it on
        /// the upper half, as chart lettering along a ring stays readable north-up.
        /// </summary>
        public static void DrawTextOnCircle(OverlayRenderSystem.Buffer buffer, float2 center, float radius, float angle, float y, string text,
            float height, Color color, float width)
        {
            bool lower = math.sin(angle) < 0f;
            float gap = kBarGap * height;
            // Lower half: the letters stand on a circle just inside the ring and point inwards; upper half: their tops touch it.
            float baseline = lower ? radius - gap : radius - gap - height;
            float direction = lower ? 1f : -1f;
            float start = -TextWidth(text, height) * 0.5f;
            for (int i = 0; i < text.Length; i++)
            {
                float[][] glyph = Glyph(text[i]);
                if (glyph == null)
                {
                    continue;
                }
                float middle = start + (i * (kWidth + kGap) + kWidth * 0.5f) * height;
                float at = angle + direction * middle / baseline;
                float2 outward = new float2(math.cos(at), math.sin(at));
                float2 along = direction * new float2(-outward.y, outward.x);
                float2 up = lower ? -outward : outward;
                float2 corner = center + outward * baseline - along * (kWidth * 0.5f * height);
                for (int s = 0; s < glyph.Length; s++)
                {
                    float[] stroke = glyph[s];
                    for (int p = 2; p < stroke.Length; p += 2)
                    {
                        float2 a = corner + (along * stroke[p - 2] + up * stroke[p - 1]) * height;
                        float2 b = corner + (along * stroke[p] + up * stroke[p + 1]) * height;
                        DrawPiece(buffer, new float3(a.x, y, a.y), new float3(b.x, y, b.y), color, width);
                    }
                }
            }
        }

        internal static void DrawText(OverlayRenderSystem.Buffer buffer, float2 origin, float y, string text, float height, Color color, float width)
        {
            for (int i = 0; i < text.Length; i++)
            {
                float[][] glyph = Glyph(text[i]);
                if (glyph == null)
                {
                    continue;
                }
                float2 corner = origin + new float2(i * (kWidth + kGap) * height, 0f);
                for (int s = 0; s < glyph.Length; s++)
                {
                    float[] stroke = glyph[s];
                    for (int p = 2; p < stroke.Length; p += 2)
                    {
                        float2 a = corner + new float2(stroke[p - 2], stroke[p - 1]) * height;
                        float2 b = corner + new float2(stroke[p], stroke[p + 1]) * height;
                        DrawPiece(buffer, new float3(a.x, y, a.y), new float3(b.x, y, b.y), color, width);
                    }
                }
            }
        }

        private static float[][] Glyph(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return kDigits[c - '0'];
            }
            switch (c)
            {
                case 'S': return kS;
                case 'F': return kF;
                case 'C': return kC;
                case '(': return kOpen;
                case ')': return kClose;
                case 'M': return kM;
                case 'O': return kDigits[0];
                case 'D': return kD;
                case 'E': return kE;
                case 'N': return kN;
                case '-': return kMinus;
                default: return null;
            }
        }

        internal static void DrawPiece(OverlayRenderSystem.Buffer buffer, float3 from, float3 to, Color color, float width)
        {
            buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(from, to), width, new float2(1f, 1f));
        }
    }
}
