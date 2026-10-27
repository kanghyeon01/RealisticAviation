using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Legacy (until 2026-10-19): the tag that turned "line up and wait" on for an airport. No longer read, now that the
    /// default depends on the airspace class (<see cref="LineUpSetting"/>); kept so that older saves still load cleanly.
    /// </summary>
    public struct LineUpAndWaitAirport : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }

    /// <summary>
    /// "Long pushback" for an airport (user, 2026-10-27; its checkbox in the airport panel): airplanes push back about their
    /// own length instead of half of it. Saved with the city by its presence on the airport building.
    /// </summary>
    public struct LongPushbackAirport : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
    }

    /// <summary>
    /// Version of the settings saved with the city. Each one starts with a byte <see cref="kFlag"/> | version, so that a later
    /// version can add fields and still read older saves. Saves from before 2026-10-20 have no such byte: their first byte
    /// is the first field, always below 0x80 (a bool, a small count, a class, the bits of a pattern, the low byte of a code
    /// letter), which tells the two apart. A save's data runs on from one component to the next, so every reader must take
    /// exactly the bytes that were written.
    /// </summary>
    internal static class SaveFormat
    {
        public const byte kFlag = 0x80;
        public const byte kVersion = 1;

        public static void WriteVersion<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write((byte)(kFlag | kVersion));
        }

        /// <summary>The version of what follows, or 0 for an old save: then <paramref name="first"/> is its first field's byte.</summary>
        public static int ReadVersion<TReader>(TReader reader, out byte first) where TReader : IReader
        {
            reader.Read(out first);
            return (first & kFlag) != 0 ? first & ~kFlag : 0;
        }
    }

    /// <summary>
    /// The player's "line up and wait" choice for an airport, set in its info panel and stored on the airport building,
    /// together with the airspace class it was made for. It holds only while the airport keeps that class: when the class
    /// changes (chosen in the panel, or automatically after gates or runways changed), the new class's default applies
    /// again. Default: on at class B and C airports (busy enough to need it), off at class D. Saved with the city.
    /// </summary>
    public struct LineUpSetting : IComponentData, IQueryTypeParameter, ISerializable
    {
        public bool m_Enabled;

        /// <summary>The <see cref="AirspaceClass"/> the choice was made under.</summary>
        public byte m_Class;

        public static bool DefaultFor(AirspaceClass airspaceClass) => airspaceClass != AirspaceClass.D;

        /// <summary>Line up and wait at an airport of this class, with or without a choice of the player.</summary>
        public static bool IsEnabled(bool hasSetting, in LineUpSetting setting, AirspaceClass airspaceClass)
        {
            return hasSetting && setting.m_Class == (byte)airspaceClass ? setting.m_Enabled : DefaultFor(airspaceClass);
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_Enabled);
            writer.Write(m_Class);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Without a version byte (saves before 2026-10-20) the first byte is m_Enabled itself.
            if (SaveFormat.ReadVersion(reader, out byte first) == 0)
            {
                m_Enabled = first != 0;
            }
            else
            {
                reader.Read(out m_Enabled);
            }
            reader.Read(out m_Class);
        }
    }

    /// <summary>
    /// "VOR" for an airport (user, 2026-10-26: its checkbox in the airport panel): a VOR at the airport's centre on the IFR
    /// charts, with its info box and a compass rose round it, as on the AIP. On by default at class B and C airports, off
    /// at class D (a small field is less likely to have one); a choice is kept for the class it was made under, as for
    /// <see cref="LineUpSetting"/>. The procedures are RNP either way.
    /// </summary>
    public struct VorSetting : IComponentData, IQueryTypeParameter, ISerializable
    {
        public bool m_Enabled;

        /// <summary>The <see cref="AirspaceClass"/> the choice was made under.</summary>
        public byte m_Class;

        public static bool DefaultFor(AirspaceClass airspaceClass) => airspaceClass != AirspaceClass.D;

        public static bool IsEnabled(bool hasSetting, in VorSetting setting, AirspaceClass airspaceClass)
        {
            return hasSetting && setting.m_Class == (byte)airspaceClass ? setting.m_Enabled : DefaultFor(airspaceClass);
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_Enabled);
            writer.Write(m_Class);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            SaveFormat.ReadVersion(reader, out _);
            reader.Read(out m_Enabled);
            reader.Read(out m_Class);
        }
    }

    /// <summary>
    /// Traffic pattern direction per runway end, set in the airport info panel and stored on every edge of the runway.
    /// Bit <see cref="kWithCurve"/> is the end landing in the edge curve's own direction, <see cref="kAgainstCurve"/> the
    /// other end; the low bits say right-hand, the same bits shifted by <see cref="kSetShift"/> say the player chose it.
    /// An end the player never set uses the default (right-hand only when a parallel runway lies to its left). Saved with
    /// the city; an edge that is rebuilt (runway redrawn or split) goes back to the default.
    /// </summary>
    public struct RunwayPattern : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const byte kWithCurve = 1;
        public const byte kAgainstCurve = 2;
        public const int kSetShift = 2;

        public byte m_RightHand;

        public static byte EndBit(float2 edgeDirection, float2 landing) => math.dot(edgeDirection, landing) >= 0f ? kWithCurve : kAgainstCurve;

        /// <summary>The player's choice for the end landing along <paramref name="landing"/>, if one was made.</summary>
        public bool TryGetRightHand(float2 edgeDirection, float2 landing, out bool rightHand)
        {
            byte bit = EndBit(edgeDirection, landing);
            rightHand = (m_RightHand & bit) != 0;
            return (m_RightHand & (bit << kSetShift)) != 0;
        }

        public void SetRightHand(float2 edgeDirection, float2 landing, bool rightHand)
        {
            byte bit = EndBit(edgeDirection, landing);
            m_RightHand = (byte)((rightHand ? m_RightHand | bit : m_RightHand & ~bit) | (bit << kSetShift));
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_RightHand);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Without a version byte the first byte is m_RightHand itself (its bits never reach 0x80).
            if (SaveFormat.ReadVersion(reader, out byte first) == 0)
            {
                m_RightHand = first;
            }
            else
            {
                reader.Read(out m_RightHand);
            }
        }
    }

    /// <summary>
    /// Ground flow control setting of one airport (phase 4-5), set in the airport info panel and stored on the airport
    /// building: whether it is on and the number of airplanes moving on its ground at which the delay starts. An airport
    /// without it has it off (<see cref="Default"/>: limit 1.5 airplanes per gate once turned on). Saved with the city.
    /// </summary>
    public struct GroundDelaySetting : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const byte kDefaultLimit = 6;
        public const byte kMinLimit = 2;
        public const byte kMaxLimit = 20;

        /// <summary>Airplanes moving on the ground at which the delay starts (kept while it is turned off).</summary>
        public byte m_Limit;

        public bool m_Enabled;

        /// <summary>Airplanes per gate for the default limit: an airport with more gates can have more airplanes moving.</summary>
        public const float kLimitPerGate = 1.5f;

        /// <summary>The default limit: <see cref="kLimitPerGate"/> per gate (4 gates: 6), or <see cref="kDefaultLimit"/> without gates.</summary>
        public static int DefaultLimit(int gates)
        {
            return gates <= 0 ? kDefaultLimit : math.clamp((int)math.round(gates * kLimitPerGate), kMinLimit, kMaxLimit);
        }

        /// <summary>The setting of an airport the player hasn't changed: off, with the default limit for its gates ready.</summary>
        public static GroundDelaySetting Default(int gates) => new GroundDelaySetting { m_Limit = (byte)DefaultLimit(gates), m_Enabled = false };

        /// <summary>The limit in force: 0 when off.</summary>
        public int ActiveLimit => m_Enabled ? m_Limit : 0;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_Limit);
            writer.Write(m_Enabled);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Without a version byte the first byte is m_Limit itself (at most 20).
            if (SaveFormat.ReadVersion(reader, out byte first) == 0)
            {
                m_Limit = first;
            }
            else
            {
                reader.Read(out m_Limit);
            }
            reader.Read(out m_Enabled);
        }
    }

    /// <summary>
    /// Airspace class the player chose for an airport in its info panel (phase 4-3), overriding the automatic one; an airport
    /// without it uses the class from its size. Holds the <see cref="AirspaceClass"/> value. Saved with the city.
    /// </summary>
    public struct AirspaceClassSetting : IComponentData, IQueryTypeParameter, ISerializable
    {
        public byte m_Class;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_Class);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Without a version byte the first byte is m_Class itself (0 to 2).
            if (SaveFormat.ReadVersion(reader, out byte first) == 0)
            {
                m_Class = first;
            }
            else
            {
                reader.Read(out m_Class);
            }
        }
    }

    /// <summary>
    /// The airport's location code (ICAO, or a 3-letter local identifier) as the player entered it in the info panel: up to
    /// four letters or digits, packed one ASCII byte each from the lowest byte. An airport without it uses an automatic code
    /// (<see cref="AirportCodes"/>). Saved with the city.
    /// </summary>
    public struct AirportCode : IComponentData, IQueryTypeParameter, ISerializable
    {
        public const int kMaxLength = 4;

        public uint m_Packed;

        public static bool TryCreate(string text, out AirportCode code)
        {
            code = default;
            if (text == null || text.Length < 3 || text.Length > kMaxLength)
            {
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = char.ToUpperInvariant(text[i]);
                if (!(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9'))
                {
                    return false;
                }
                code.m_Packed |= (uint)c << (8 * i);
            }
            return true;
        }

        public override string ToString()
        {
            char[] text = new char[kMaxLength];
            int length = 0;
            for (uint packed = m_Packed; packed != 0 && length < kMaxLength; packed >>= 8)
            {
                text[length++] = (char)(packed & 0xFF);
            }
            return new string(text, 0, length);
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            SaveFormat.WriteVersion(writer);
            writer.Write(m_Packed);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            // Without a version byte the first byte is the lowest byte of m_Packed (the first letter, below 0x80, as the
            // game writes a uint lowest byte first): read the other three and put it back together.
            if (SaveFormat.ReadVersion(reader, out byte first) == 0)
            {
                m_Packed = first;
                for (int shift = 8; shift < 32; shift += 8)
                {
                    reader.Read(out byte next);
                    m_Packed |= (uint)next << shift;
                }
            }
            else
            {
                reader.Read(out m_Packed);
            }
        }
    }

    /// <summary>
    /// Airport codes, for the airport panel and for other mods (Aviation Weather Add-on): the code the player entered, or an
    /// automatic one from the airport's name (its first four Latin letters, "ZZAA" when it has fewer), made unique among the
    /// city's airports by stepping the last two letters (airports in entity order; entered codes come first).
    /// </summary>
    internal static class AirportCodes
    {
        /// <summary>The code the player entered for this airport, or null.</summary>
        public static string GetEntered(EntityManager entityManager, Entity airport)
        {
            return entityManager.TryGetComponent(airport, out AirportCode code) && code.m_Packed != 0 ? code.ToString() : null;
        }

        /// <summary>The airport's code: the entered one, or else its automatic one.</summary>
        public static string Get(EntityManager entityManager, Game.UI.NameSystem nameSystem, Entity airport)
        {
            return GetEntered(entityManager, airport) ?? GetAutomatic(entityManager, nameSystem, airport);
        }

        /// <summary>The automatic code the airport has (or would have) without an entered one.</summary>
        public static string GetAutomatic(EntityManager entityManager, Game.UI.NameSystem nameSystem, Entity airport)
        {
            EntityQuery stops = Airports.CreateAirplaneStopQuery(entityManager);
            NativeList<Entity> airports = new NativeList<Entity>(4, Allocator.Temp);
            try
            {
                Airports.Collect(entityManager, stops, airports);
                airports.Sort();
                System.Collections.Generic.HashSet<string> used = new System.Collections.Generic.HashSet<string>();
                for (int i = 0; i < airports.Length; i++)
                {
                    string entered = GetEntered(entityManager, airports[i]);
                    if (entered != null)
                    {
                        used.Add(entered);
                    }
                }
                string code = null;
                for (int i = 0; i < airports.Length; i++)
                {
                    if (GetEntered(entityManager, airports[i]) != null && airports[i] != airport)
                    {
                        continue;
                    }
                    code = Unique(FromName(nameSystem.GetRenderedLabelName(airports[i])), used);
                    used.Add(code);
                    if (airports[i] == airport)
                    {
                        return code;
                    }
                }
                // Not an airport (yet): a code from its name that no airport uses.
                return Unique(FromName(nameSystem.GetRenderedLabelName(airport)), used);
            }
            finally
            {
                airports.Dispose();
                stops.Dispose();
            }
        }

        private static char[] FromName(string name)
        {
            char[] code = new char[AirportCode.kMaxLength];
            int length = 0;
            for (int i = 0; name != null && i < name.Length && length < code.Length; i++)
            {
                char c = char.ToUpperInvariant(name[i]);
                if (c >= 'A' && c <= 'Z')
                {
                    code[length++] = c;
                }
            }
            if (length < code.Length)
            {
                code[0] = 'Z';
                code[1] = 'Z';
                code[2] = 'A';
                code[3] = 'A';
            }
            return code;
        }

        private static string Unique(char[] code, System.Collections.Generic.HashSet<string> used)
        {
            for (int n = 0; n < 26 * 26; n++)
            {
                string candidate = new string(code);
                if (!used.Contains(candidate))
                {
                    return candidate;
                }
                // Taken: step the last two letters (..AA, ..AB, ...).
                code[3] = (char)(code[3] == 'Z' ? 'A' : code[3] + 1);
                if (code[3] == 'A')
                {
                    code[2] = (char)(code[2] == 'Z' ? 'A' : code[2] + 1);
                }
            }
            return new string(code);
        }
    }

    /// <summary>An airport is a top-level building that owns airplane stops (gates), outside connections excluded.</summary>
    internal static class Airports
    {
        public static EntityQuery CreateAirplaneStopQuery(EntityManager entityManager)
        {
            return entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<AirplaneStop>(), ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        public static Entity GetRoot(EntityManager entityManager, Entity entity)
        {
            while (entityManager.TryGetComponent(entity, out Owner owner) && owner.m_Owner != Entity.Null)
            {
                entity = owner.m_Owner;
            }
            return entity;
        }

        /// <summary>
        /// The airport the entity belongs to (the entity itself or its top-level owner), or Entity.Null. Only for the airport's
        /// main building, roads and runways, stops (gates). Not its upgrades (user: the airport panel on a cargo terminal would
        /// be odd), nor anything else it owns: a company vehicle's owner is the building that runs it, so the airport's own
        /// delivery trucks showed the airport panel.
        /// </summary>
        public static Entity GetAirport(EntityManager entityManager, EntityQuery airplaneStops, Entity entity)
        {
            if (entity == Entity.Null || !entityManager.Exists(entity) || !IsStructure(entityManager, entity))
            {
                return Entity.Null;
            }
            Entity root = GetRoot(entityManager, entity);
            if (entityManager.HasComponent<Game.Objects.OutsideConnection>(root))
            {
                return Entity.Null;
            }
            NativeArray<Entity> stops = airplaneStops.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    if (GetRoot(entityManager, stops[i]) == root)
                    {
                        return root;
                    }
                }
            }
            finally
            {
                stops.Dispose();
            }
            return Entity.Null;
        }

        private static bool IsStructure(EntityManager entityManager, Entity entity)
        {
            // A building only as the top of its owner chain: an upgrade building is owned by the main one.
            return (entityManager.HasComponent<Game.Buildings.Building>(entity)
                    && !(entityManager.TryGetComponent(entity, out Owner owner) && owner.m_Owner != Entity.Null))
                || entityManager.HasComponent<Game.Net.Edge>(entity) || entityManager.HasComponent<Game.Net.Node>(entity)
                || entityManager.HasComponent<TransportStop>(entity);
        }

        /// <summary>Every airport in the city, once each.</summary>
        public static void Collect(EntityManager entityManager, EntityQuery airplaneStops, NativeList<Entity> airports)
        {
            airports.Clear();
            NativeArray<Entity> stops = airplaneStops.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    Entity root = GetRoot(entityManager, stops[i]);
                    if (!entityManager.HasComponent<Game.Objects.OutsideConnection>(root) && !airports.Contains(root))
                    {
                        airports.Add(root);
                    }
                }
            }
            finally
            {
                stops.Dispose();
            }
        }
    }
}
