using Colossal.UI.Binding;
using Game.UI.InGame;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// "Line up and wait" checkbox in an airport's info panel. RealisticAviation.mjs draws it; the click comes back
    /// through the trigger binding and toggles <see cref="LineUpAndWaitAirport"/> on the airport building.
    /// </summary>
    public partial class LineUpSection : InfoSectionBase
    {
        public const string kToggleTrigger = "ToggleLineUpAndWait";

        private EntityQuery m_AirplaneStopQuery;
        private Entity m_Airport;
        private bool m_Enabled;

        protected override string group => nameof(LineUpSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(Mod.kName, kToggleTrigger, Toggle));
        }

        protected override void OnUpdate()
        {
            m_Airport = Airports.GetAirport(EntityManager, m_AirplaneStopQuery, selectedEntity);
            visible = m_Airport != Entity.Null;
        }

        protected override void Reset()
        {
            m_Enabled = false;
        }

        protected override void OnProcess()
        {
            m_Enabled = EntityManager.HasComponent<LineUpAndWaitAirport>(m_Airport);
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("enabled");
            writer.Write(m_Enabled);
        }

        private void Toggle()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            bool enable = !EntityManager.HasComponent<LineUpAndWaitAirport>(m_Airport);
            if (enable)
            {
                EntityManager.AddComponent<LineUpAndWaitAirport>(m_Airport);
            }
            else
            {
                EntityManager.RemoveComponent<LineUpAndWaitAirport>(m_Airport);
            }
            Mod.Log.Info($"Line up and wait {(enable ? "enabled" : "disabled")} at airport {m_Airport}");
            // Re-sends every section to the UI; only marking this one dirty would leave the checkbox unchanged on screen.
            m_InfoUISystem.RequestUpdate();
        }
    }
}
