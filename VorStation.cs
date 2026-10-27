using System.Text;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// An airport's VOR on the IFR charts (user, 2026-10-26; <see cref="VorSetting"/>): at the airport's centre, its name,
    /// frequency and ident as the AIP's info box gives them. The game has no navaids, so they are made up the way the
    /// other names are, from what stays the same after a reload: the frequency from the position, the ident from the
    /// airport's code.
    /// </summary>
    internal struct VorStation
    {
        public float2 m_Position;

        /// <summary>The airport's name in the chart font's letters and digits, upper case.</summary>
        public string m_Name;

        /// <summary>"113.60".</summary>
        public string m_Frequency;

        /// <summary>Three letters.</summary>
        public string m_Ident;

        /// <summary>
        /// VOR channels (ICAO Annex 10): 108.00-111.95 MHz where the tenths are even (the odd ones are ILS), and 112.00-117.95,
        /// 50 kHz apart: 40 + 120.
        /// </summary>
        private const int kLowChannels = 40;
        private const int kChannels = kLowChannels + 120;

        public static VorStation Create(EntityManager entityManager, NameSystem nameSystem, Entity airport, float2 position)
        {
            string code = AirportCodes.Get(entityManager, nameSystem, airport) ?? string.Empty;
            string ident = Ident(code);
            string name = ChartName(nameSystem.GetRenderedLabelName(airport));
            return new VorStation
            {
                m_Position = position,
                m_Name = name.Length != 0 ? name : ident,
                m_Frequency = Frequency(position),
                m_Ident = ident,
            };
        }

        /// <summary>A channel picked by the position (rounded to 50 m, so it stays the same after a reload).</summary>
        private static string Frequency(float2 position)
        {
            uint hash = math.hash((int2)math.round(position / 50f));
            int channel = (int)(hash % kChannels);
            int khz;
            if (channel < kLowChannels)
            {
                // Pairs of 50 kHz steps in every other tenth: 108.00, 108.05, 108.20, 108.25...
                khz = 108000 + channel / 2 * 200 + channel % 2 * 50;
            }
            else
            {
                khz = 112000 + (channel - kLowChannels) * 50;
            }
            return (khz / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "."
                + (khz % 1000 / 10).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Three letters from the airport's code: its last three letters (KPLD -> PLD), "VOR" without three.</summary>
        private static string Ident(string code)
        {
            StringBuilder letters = new StringBuilder(4);
            for (int i = 0; i < code.Length; i++)
            {
                char c = char.ToUpperInvariant(code[i]);
                if (c >= 'A' && c <= 'Z')
                {
                    letters.Append(c);
                }
            }
            return letters.Length >= 3 ? letters.ToString(letters.Length - 3, 3) : "VOR";
        }

        /// <summary>The name in upper case with only the letters, digits and spaces the chart font has.</summary>
        private static string ChartName(string name)
        {
            StringBuilder text = new StringBuilder(name?.Length ?? 0);
            bool space = false;
            for (int i = 0; name != null && i < name.Length; i++)
            {
                char c = char.ToUpperInvariant(name[i]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    if (space && text.Length != 0)
                    {
                        text.Append(' ');
                    }
                    text.Append(c);
                    space = false;
                }
                else
                {
                    space = true;
                }
            }
            return text.ToString();
        }

        /// <summary>The ident in Morse, as dots and dashes ('.' and '-'), letters apart by a space.</summary>
        public static string Morse(string ident)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < ident.Length; i++)
            {
                int letter = ident[i] - 'A';
                if (letter < 0 || letter >= kMorse.Length)
                {
                    continue;
                }
                if (text.Length != 0)
                {
                    text.Append(' ');
                }
                text.Append(kMorse[letter]);
            }
            return text.ToString();
        }

        private static readonly string[] kMorse =
        {
            ".-", "-...", "-.-.", "-..", ".", "..-.", "--.", "....", "..", ".---", "-.-", ".-..", "--",
            "-.", "---", ".--.", "--.-", ".-.", "...", "-", "..-", "...-", ".--", "-..-", "-.--", "--..",
        };
    }
}
