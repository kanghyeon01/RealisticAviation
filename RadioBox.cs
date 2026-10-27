using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.UI;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// One frequency's recent transmissions as shown in an info panel (phase 4-2): a label such as "TWR 118.300" and
    /// the exchanges oldest first (newest at the bottom), each the controller's words and the pilot's readback (no
    /// readback for a broadcast; on a pilot's own call, such as a weather go-around, the pilot speaks first), and whether
    /// it is the selected airplane's own, and its airplane and airport (clicking an exchange selects its airplane, or for a
    /// broadcast the airport, and moves the camera there). RealisticAviation.mjs draws it.
    /// </summary>
    internal sealed class RadioBox
    {
        private struct Exchange
        {
            public string m_Tower;
            public string m_Pilot;
            public bool m_Own;
            public bool m_PilotFirst;
            public string m_Id;

            /// <summary>The airplane of the exchange (Entity.Null for a broadcast), selected when the exchange is clicked.</summary>
            public Entity m_Airplane;

            /// <summary>The airport that spoke, selected when a broadcast (no airplane) is clicked.</summary>
            public Entity m_Airport;
        }

        private readonly List<AtcMessage> m_Messages = new List<AtcMessage>();
        private readonly List<Exchange> m_Exchanges = new List<Exchange>();
        private string m_Label = string.Empty;

        public void Clear()
        {
            m_Label = string.Empty;
            m_Exchanges.Clear();
        }

        /// <summary>Fills the box from the tower's log; <paramref name="own"/> marks that airplane's exchanges.</summary>
        public void Fill(AtcSystem atcSystem, EntityManager entityManager, NameSystem nameSystem, PrefabSystem prefabSystem,
            Entity airport, AtcFrequency frequency, int max, Entity own)
        {
            Clear();
            m_Label = AtcRadio.FrequencyLabel(airport, frequency);
            atcSystem.GetRadioLog(airport, frequency, m_Messages, max);
            for (int i = 0; i < m_Messages.Count; i++)
            {
                atcSystem.Radio.Describe(entityManager, nameSystem, prefabSystem, atcSystem.RunwayList, m_Messages[i], frequency,
                    out string tower, out string pilot, out bool pilotFirst);
                AtcMessage message = m_Messages[i];
                m_Exchanges.Add(new Exchange
                {
                    m_Tower = tower,
                    m_Pilot = pilot,
                    m_Own = own != Entity.Null && message.m_Airplane == own,
                    m_PilotFirst = pilotFirst,
                    // Stable across panel updates, so the UI can tell a new exchange from one it already shows.
                    m_Id = $"{message.m_Frame}:{message.m_Airplane.Index}:{(int)message.m_Kind}",
                    m_Airplane = message.m_Airplane,
                    m_Airport = airport,
                });
            }
        }

        public void Write(IJsonWriter writer)
        {
            writer.TypeBegin("RealisticAviation.RadioBox");
            writer.PropertyName("label");
            writer.Write(m_Label);
            writer.PropertyName("exchanges");
            writer.ArrayBegin(m_Exchanges.Count);
            // Oldest first, like a radio transcript: the newest exchange is at the bottom.
            for (int i = m_Exchanges.Count - 1; i >= 0; i--)
            {
                Exchange exchange = m_Exchanges[i];
                writer.TypeBegin("RealisticAviation.RadioExchange");
                writer.PropertyName("tower");
                writer.Write(exchange.m_Tower);
                writer.PropertyName("pilot");
                writer.Write(exchange.m_Pilot);
                writer.PropertyName("own");
                writer.Write(exchange.m_Own);
                writer.PropertyName("pilotFirst");
                writer.Write(exchange.m_PilotFirst);
                writer.PropertyName("id");
                writer.Write(exchange.m_Id);
                writer.PropertyName("airplane");
                writer.Write(exchange.m_Airplane.Index);
                writer.PropertyName("airplaneVersion");
                writer.Write(exchange.m_Airplane.Version);
                writer.PropertyName("airport");
                writer.Write(exchange.m_Airport.Index);
                writer.PropertyName("airportVersion");
                writer.Write(exchange.m_Airport.Version);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }
    }
}
