using Colossal.Entities;
using Colossal.UI.Binding;
using Game.Pathfind;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    /// <summary>
    /// "Tower radio" box in an airplane's info panel (phase 4-2): the recent transmissions on the frequency the airplane is
    /// on (the one of its latest message: tower or ground), as every airplane on that frequency hears them, with its own
    /// exchanges marked. Shown only while the airplane has heard from the tower recently.
    /// With the "Remove airplane button" option, every airplane's panel also has a button that takes the airplane away.
    /// Clicking an exchange in any radio box selects its airplane and follows it (for a broadcast, its airport)
    /// (the triggers live here, the airport panel uses them too).
    /// </summary>
    public partial class AirplaneAtcSection : InfoSectionBase
    {
        public const string kRemoveAirplaneTrigger = "RemoveAirplane";
        public const string kSelectAirplaneTrigger = "SelectAirplane";
        public const string kSelectAirportTrigger = "SelectAirport";

        private const int kExchanges = 5;

        private readonly RadioBox m_Radio = new RadioBox();
        private AtcSystem m_AtcSystem;
        private AtcMessage m_Message;
        private uint m_RadioVersion;
        private bool m_ShowRadio;
        private bool m_ShowRemove;

        protected override string group => nameof(AirplaneAtcSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(Mod.kName, kRemoveAirplaneTrigger, RemoveAirplane));
            AddBinding(new TriggerBinding<int, int>(Mod.kName, kSelectAirplaneTrigger, SelectAirplane));
            AddBinding(new TriggerBinding<int, int>(Mod.kName, kSelectAirportTrigger, SelectAirport));
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
            bool airplane = entity != Entity.Null && EntityManager.HasComponent<Airplane>(entity);
            m_ShowRadio = airplane && m_AtcSystem.TryGetLastMessage(entity, out m_Message)
                && m_Message.m_Airport != Entity.Null && !HasLeftFrequency(m_Message);
            m_ShowRemove = airplane && Mod.Setting != null && Mod.Setting.RemoveAirplaneButton;
            visible = m_ShowRadio || m_ShowRemove;
        }

        /// <summary>
        /// Released to a controller the mod doesn't model ("contact center" on leaving the last zone, or "frequency change
        /// approved" after a class D departure): it is on none of the airport's frequencies any more.
        /// </summary>
        private static bool HasLeftFrequency(in AtcMessage message)
        {
            return message.m_Kind == AtcMessageKind.ContactCenter || (message.m_Kind == AtcMessageKind.ContactDeparture && message.m_Value == 0);
        }

        protected override void OnProcess()
        {
            if (m_ShowRadio)
            {
                m_Radio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Message.m_Airport, m_Message.m_Frequency, kExchanges, selectedEntity);
            }
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("showRadio");
            writer.Write(m_ShowRadio);
            writer.PropertyName("radio");
            m_Radio.Write(writer);
            writer.PropertyName("canRemove");
            writer.Write(m_ShowRemove);
        }

        /// <summary>
        /// Selects the airplane of a clicked radio exchange (in an airport's or an airplane's panel) and follows it, with the
        /// game's own selection and follow (the info panel's follow button: SelectedInfoUISystem.Focus; user). One that has
        /// left the city does nothing.
        /// </summary>
        private void SelectAirplane(int index, int version)
        {
            Entity entity = new Entity { Index = index, Version = version };
            if (index > 0 && EntityManager.Exists(entity) && EntityManager.HasComponent<Airplane>(entity)
                && !EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                m_InfoUISystem.SetSelection(entity);
                m_InfoUISystem.Focus(entity);
            }
        }

        /// <summary>
        /// A clicked broadcast ("attention all aircraft"): the airport that spoke, selected and followed as an airplane is
        /// (user: the UI stays consistent).
        /// </summary>
        private void SelectAirport(int index, int version)
        {
            Entity entity = new Entity { Index = index, Version = version };
            if (index > 0 && EntityManager.Exists(entity) && !EntityManager.HasComponent<Game.Common.Deleted>(entity))
            {
                m_InfoUISystem.SetSelection(entity);
                m_InfoUISystem.Focus(entity);
            }
        }

        /// <summary>
        /// Takes the selected airplane away (a stop-gap for one stuck on a runway), the way the game itself does it with a
        /// vehicle that is stuck (StuckMovingObjectSystem): its path is marked stuck, and its AI ends any boarding or loading
        /// and deletes it at its next update. The tower forgets it once it is gone.
        /// </summary>
        private void RemoveAirplane()
        {
            Entity entity = selectedEntity;
            if (Mod.Setting == null || !Mod.Setting.RemoveAirplaneButton || entity == Entity.Null || !EntityManager.HasComponent<Airplane>(entity))
            {
                return;
            }
            // Logged always: a player removing an airplane is what a bug report needs to explain.
            string where = EntityManager.TryGetComponent(entity, out Transform transform) ? $" at {(int3)math.round(transform.m_Position)}" : string.Empty;
            string lane = EntityManager.TryGetComponent(entity, out AircraftCurrentLane currentLane)
                ? $", lane {currentLane.m_Lane} [{currentLane.m_LaneFlags}]" : string.Empty;
            string speed = EntityManager.TryGetComponent(entity, out Game.Objects.Moving moving) ? $", speed {math.length(moving.m_Velocity):F1} m/s" : string.Empty;
            string blocker = EntityManager.TryGetComponent(entity, out Blocker block) && block.m_Blocker != Entity.Null
                ? $", blocked by {block.m_Blocker} ({block.m_Type})" : string.Empty;
            string tower = Api.Flights.TryGet(entity, out FlightInfo flight)
                ? $", tower: {flight.State} for runway {flight.Runway}, waited {flight.WaitedSeconds:F0} s" : ", tower: not handling it";
            Mod.Log.Info($"{entity} removed by the player{where}{lane}{speed}{blocker}{tower}");

            if (EntityManager.TryGetComponent(entity, out PathOwner pathOwner))
            {
                pathOwner.m_State |= PathFlags.Stuck;
                EntityManager.SetComponentData(entity, pathOwner);
            }
            else
            {
                EntityManager.AddComponent<Game.Common.Deleted>(entity);
            }
            m_InfoUISystem.RequestUpdate();
        }
    }
}
