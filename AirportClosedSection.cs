using Colossal.UI.Binding;
using Game.Prefabs;
using Game.UI.InGame;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// Status banners in the top area of an airport's info panel (next to the notifications): "Airport closed" while the
    /// tower has closed the airport for a disaster (naming the event's prefab, which the UI translates with the game's own
    /// asset names), and "Ground delay" while too many airplanes move on its ground (phase 4-5). Shown only when one of
    /// them applies. RealisticAviation.mjs draws them.
    /// </summary>
    public partial class AirportClosedSection : InfoSectionBase
    {
        private AtcSystem m_AtcSystem;
        private EntityQuery m_AirplaneStopQuery;
        private bool m_Closed;
        private string m_ClosedBy;
        private bool m_GroundDelay;
        private int m_GroundCount;

        protected override string group => nameof(AirportClosedSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            m_InfoUISystem.AddTopSection(this);
        }

        protected override void Reset()
        {
            m_Closed = false;
            m_ClosedBy = string.Empty;
            m_GroundDelay = false;
            m_GroundCount = 0;
        }

        protected override void OnUpdate()
        {
            Entity airport = Airports.GetAirport(EntityManager, m_AirplaneStopQuery, selectedEntity);
            if (airport == Entity.Null)
            {
                visible = false;
                return;
            }
            Entity closedBy = m_AtcSystem.GetClosure(airport);
            m_Closed = closedBy != Entity.Null;
            if (m_Closed)
            {
                m_ClosedBy = closedBy != airport && EntityManager.HasComponent<PrefabRef>(closedBy)
                    ? m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(closedBy).m_Prefab)
                    : string.Empty;
            }
            m_GroundDelay = m_AtcSystem.GetGroundDelay(airport, out m_GroundCount);
            visible = m_Closed || m_GroundDelay;
        }

        protected override void OnProcess()
        {
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("closed");
            writer.Write(m_Closed);
            writer.PropertyName("closedBy");
            writer.Write(m_ClosedBy);
            writer.PropertyName("groundDelay");
            writer.Write(m_GroundDelay);
            writer.PropertyName("groundCount");
            writer.Write(m_GroundCount);
        }
    }
}
