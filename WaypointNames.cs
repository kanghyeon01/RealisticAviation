using System.Collections.Generic;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Names of the procedures' waypoints: pronounceable five-letter names in the ICAO 5LNC style (consonant, vowel,
    /// consonant, vowel, consonant, e.g. KOVAN), from a hash of the fix's position rounded to a grid, so the same place gets
    /// the same name after a reload. Unique within the city: on a clash, or a word to avoid, the next hash is taken.
    /// </summary>
    internal sealed class WaypointNames
    {
        private const float kCell = 50f;
        private const string kConsonants = "BDFGKLMNPRSTVZ";
        private const string kVowels = "AEIOU";

        private static readonly HashSet<string> kAvoid = new HashSet<string>
        {
            "NIGER", "NIGAR", "FAGOT", "PENIS", "KIKES", "SEMEN", "RAPED", "RAPES", "NAZIS", "DAMEN",
        };

        private readonly Dictionary<long, string> m_ByCell = new Dictionary<long, string>();
        private readonly HashSet<string> m_Used = new HashSet<string>();
        private readonly char[] m_Letters = new char[5];

        public void Clear()
        {
            m_ByCell.Clear();
            m_Used.Clear();
        }

        /// <summary>The name of the fix at <paramref name="position"/> (x, z); the same for the same place.</summary>
        public string Get(float2 position)
        {
            int x = (int)math.round(position.x / kCell), z = (int)math.round(position.y / kCell);
            long cell = ((long)x << 32) ^ (uint)z;
            if (m_ByCell.TryGetValue(cell, out string name))
            {
                return name;
            }
            for (int attempt = 0; ; attempt++)
            {
                uint hash = math.hash(new int3(x, z, attempt));
                for (int i = 0; i < 5; i++)
                {
                    string letters = (i & 1) == 0 ? kConsonants : kVowels;
                    m_Letters[i] = letters[(int)(hash % (uint)letters.Length)];
                    hash /= (uint)letters.Length;
                }
                name = new string(m_Letters);
                if (!m_Used.Contains(name) && !kAvoid.Contains(name))
                {
                    m_Used.Add(name);
                    m_ByCell.Add(cell, name);
                    return name;
                }
            }
        }
    }
}
