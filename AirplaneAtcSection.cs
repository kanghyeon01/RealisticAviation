using Colossal.UI.Binding;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// "Tower radio" box in an airplane's info panel (phase 4-2): the recent transmissions on the frequency the airplane is
    /// on (the one of its latest message: tower or ground), as every airplane on that frequency hears them, with its own
    /// exchanges marked. Shown only while the airplane has heard from the tower recently.
    /// </summary>
    public partial class AirplaneAtcSection : InfoSectionBase
    {
        private const int kExchanges = 5;

        private readonly RadioBox m_Radio = new RadioBox();
        private AtcSystem m_AtcSystem;
        private AtcMessage m_Message;
        private uint m_RadioVersion;

        protected override string group => nameof(AirplaneAtcSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_InfoUISystem.AddMiddleSection(this);
        }

        protected override void Reset()
        {
            m_Radio.Clear();
        }

        /// <summary>Refreshes the panel as soon as there is a new transmission, not only when the panel next updates by itself.</summary>
        protected override void OnPreUpdate()
        {
            // Any selected airplane, also one not shown yet: its first transmission should appear at once too.
            Entity entity = selectedEntity;
            if (entity != Entity.Null && EntityManager.HasComponent<Airplane>(entity)
                && m_AtcSystem.TryGetRadioVersion(out uint version) && version != m_RadioVersion)
            {
                m_RadioVersion = version;
                m_InfoUISystem.RequestUpdate();
            }
        }

        protected override void OnUpdate()
        {
            Entity entity = selectedEntity;
            visible = entity != Entity.Null && EntityManager.HasComponent<Airplane>(entity) && m_AtcSystem.TryGetLastMessage(entity, out m_Message)
                && m_Message.m_Airport != Entity.Null;
        }

        protected override void OnProcess()
        {
            m_Radio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Message.m_Airport, m_Message.m_Frequency, kExchanges, selectedEntity);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("radio");
            m_Radio.Write(writer);
        }
    }
}
