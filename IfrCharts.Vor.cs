using System.Collections.Generic;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// The VOR on the IFR charts, as docs/vor-preview.html draws it (user, 2026-10-27: symbol 1-a, info box 2-a, rose 1 NM):
    /// the ICAO VOR symbol (a hexagon with a dot) at the airport's centre, a compass rose round it (ticks every 10 degrees,
    /// longer every 30, 0 9 18 27, true north with an arrowhead) and the AIP's info box ("PORTLAND VOR", "113.60 PLD", the
    /// ident in Morse) placed where it is clear.
    /// </summary>
    internal static partial class IfrCharts
    {
        /// <summary>The rose's radius on the ground (user: 1 NM; the map is about 7.7 NM wide).</summary>
        private const float kRoseRadius = Setting.kMetersPerNauticalMile;

        private static float kRoseStroke => 1.4f * kPx * kLineScale;
        private static float kRoseTickStroke => 1.1f * kPx * kLineScale;
        private static float kRoseTick => 4f * kPx;
        private static float kRoseLongTick => 7f * kPx;
        private static float kRoseNumberHeight => 7f * kTx;
        private static float kRoseHeadSize => 8f * kPx;
        private static float kVorRadius => 9f * kPx;
        private static float kVorStroke => 1.6f * kPx * kLineScale;
        private static float kVorBoxHeight => 10f * kTx;
        private static float kVorBoxStroke => 1.3f * kPx * kLineScale;

        /// <summary>The airport's VOR, its rose and its info box, if it has one.</summary>
        public static void Vor(IfrChartCanvas canvas, AirportProcedures procedures)
        {
            if (!procedures.m_HasVor)
            {
                return;
            }
            VorStation vor = procedures.m_Vor;
            float2 c = vor.m_Position;
            canvas.Color = kInk;

            // Compass rose: north is +z, a heading t points (sin t, cos t).
            canvas.Circle(c, kRoseRadius, kRoseStroke);
            for (int degrees = 0; degrees < 360; degrees += 10)
            {
                float t = math.radians(degrees);
                float2 direction = new float2(math.sin(t), math.cos(t));
                float inner = kRoseRadius - (degrees % 30 == 0 ? kRoseLongTick : kRoseTick);
                canvas.Line(c + direction * inner, c + direction * kRoseRadius, kRoseTickStroke, 0f, 0f, false);
            }
            string[] numbers = { "0", "9", "18", "27" };
            for (int i = 0; i < 4; i++)
            {
                float t = math.radians(i * 90f);
                float2 at = c + new float2(math.sin(t), math.cos(t)) * (kRoseRadius - kRoseLongTick - kRoseNumberHeight);
                canvas.Text(numbers[i], at, 0f, kRoseNumberHeight, kRoseNumberHeight * kTextStroke);
            }
            canvas.Head(c + new float2(0f, kRoseRadius + kRoseHeadSize + 1f * kPx), new float2(0f, 1f), kRoseHeadSize);

            // The VOR symbol.
            float2[] hexagon = new float2[6];
            for (int i = 0; i < 6; i++)
            {
                float a = i * math.PI / 3f;
                hexagon[i] = c + kVorRadius * new float2(math.cos(a), math.sin(a));
            }
            canvas.Polygon(hexagon, kVorStroke);
            canvas.Dot(c, kVorRadius * 0.32f);
            canvas.Box(c, new float2(kVorRadius, kVorRadius), 0f);

            // The info box: two lines and the Morse below, left-aligned in a frame.
            string first = vor.m_Name + " VOR", second = vor.m_Frequency + " " + vor.m_Ident;
            string morse = VorStation.Morse(vor.m_Ident);
            float h = kVorBoxHeight, gap = 0.7f * h, pad = 0.7f * h;
            float morseWidth = MorseWidth(morse, h);
            float width = math.max(math.max(IfrChartCanvas.TextWidth(first, h), IfrChartCanvas.TextWidth(second, h)), morseWidth);
            float2 half = new float2(width * 0.5f + pad, (3f * h + 2f * gap) * 0.5f + pad);
            List<IfrChartCanvas.Spot> spots = new List<IfrChartCanvas.Spot>(48);
            for (int ring = 0; ring < 3; ring++)
            {
                for (int i = 0; i < 16; i++)
                {
                    // Lower right first (where the preview has it), then round.
                    float t = -math.PI / 4f + (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * math.PI / 8f;
                    float2 v = new float2(math.cos(t), math.sin(t));
                    float distance = kVorRadius + 4f * kTx + ring * 14f * kTx + math.abs(v.x) * half.x + math.abs(v.y) * half.y;
                    spots.Add(new IfrChartCanvas.Spot { m_Center = c + v * distance, m_Angle = 0f });
                }
            }
            canvas.AddLabel(kPriorityFix, true, half, spots, (canvas2, spot) =>
            {
                canvas2.Color = kInk;
                float2 lo = spot.m_Center - half, hi = spot.m_Center + half;
                canvas2.Polygon(new[] { lo, new float2(hi.x, lo.y), hi, new float2(lo.x, hi.y) }, kVorBoxStroke);
                float left = lo.x + pad, top = hi.y - pad;
                canvas2.Text(first, new float2(left + IfrChartCanvas.TextWidth(first, h) * 0.5f, top - 0.5f * h), 0f, h, h * kTextStroke);
                canvas2.Text(second, new float2(left + IfrChartCanvas.TextWidth(second, h) * 0.5f, top - 1.5f * h - gap), 0f, h, h * kTextStroke);
                DrawMorse(canvas2, morse, new float2(left, top - 2.5f * h - 2f * gap), h);
            });
        }

        // Morse marks: a dot a fifth of the text height across, a dash 0.7 of it long; marks 0.45 / 0.95 apart, letters 0.6 more.
        private static float MorseWidth(string morse, float h)
        {
            float x = 0f;
            for (int i = 0; i < morse.Length; i++)
            {
                x += morse[i] == '.' ? 0.45f * h : morse[i] == '-' ? 0.95f * h : 0.6f * h;
            }
            return x;
        }

        private static void DrawMorse(IfrChartCanvas canvas, string morse, float2 start, float h)
        {
            float x = start.x;
            for (int i = 0; i < morse.Length; i++)
            {
                if (morse[i] == '.')
                {
                    canvas.Dot(new float2(x + 0.12f * h, start.y), 0.24f * h);
                    x += 0.45f * h;
                }
                else if (morse[i] == '-')
                {
                    canvas.Line(new float2(x, start.y), new float2(x + 0.7f * h, start.y), 0.2f * h, 0f, 0f, false);
                    x += 0.95f * h;
                }
                else
                {
                    x += 0.6f * h;
                }
            }
        }
    }
}
