using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Tools;
using Game.UI.Localization;
using Game.UI.Widgets;
using Unity.Collections;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// Testing aid (temporary): starts a weather disaster (storm, hail, tornado) centered on an airport, so that the airport
    /// closure can be tested without waiting for one. The options page picks the event and presses the button; the event
    /// is created here on the next simulation update, aimed at the selected airport or else the first airport with runways,
    /// the same way the game's developer panel starts an event at the selected object.
    /// </summary>
    public partial class DisasterTestSystem : GameSystemBase
    {
        private static DisasterTestSystem s_Instance;

        private PrefabSystem m_PrefabSystem;
        private ToolSystem m_ToolSystem;
        private AtcSystem m_AtcSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private EntityQuery m_WeatherEventPrefabQuery;
        private EntityQuery m_AirplaneStopQuery;
        private string m_Requested;

        protected override void OnCreate()
        {
            base.OnCreate();
            s_Instance = this;
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_WeatherEventPrefabQuery = GetEntityQuery(ComponentType.ReadOnly<EventData>(), ComponentType.ReadOnly<WeatherPhenomenonData>(), ComponentType.ReadOnly<PrefabData>());
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            Enabled = false;
        }

        protected override void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
            base.OnDestroy();
        }

        /// <summary>
        /// Lets the dropdown re-read its items once the prefabs exist: the options page first asks while the game is still
        /// loading, and only asks again when this value changes.
        /// </summary>
        internal static int GetEventItemsVersion() => s_Instance != null ? s_Instance.m_WeatherEventPrefabQuery.CalculateEntityCount() : -1;

        /// <summary>Weather event prefabs for the options dropdown, shown with the game's own names.</summary>
        internal static DropdownItem<string>[] GetEventItems()
        {
            List<DropdownItem<string>> items = new List<DropdownItem<string>>();
            if (s_Instance != null)
            {
                NativeArray<Entity> prefabs = s_Instance.m_WeatherEventPrefabQuery.ToEntityArray(Allocator.Temp);
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string name = s_Instance.m_PrefabSystem.GetPrefabName(prefabs[i]);
                    items.Add(new DropdownItem<string> { value = name, displayName = LocalizedString.IdWithFallback($"Assets.NAME[{name}]", name) });
                }
                prefabs.Dispose();
            }
            items.Sort((a, b) => string.CompareOrdinal(a.value, b.value));
            return items.ToArray();
        }

        /// <summary>Asks for the event to be started on the next simulation update.</summary>
        internal static void Request(string prefabName)
        {
            if (s_Instance != null)
            {
                s_Instance.m_Requested = prefabName;
                s_Instance.Enabled = true;
            }
        }

        protected override void OnUpdate()
        {
            string requested = m_Requested;
            m_Requested = null;
            Enabled = false;

            Entity prefab = FindPrefab(requested);
            Entity airport = Airports.GetAirport(EntityManager, m_AirplaneStopQuery, m_ToolSystem.selected);
            if (airport == Entity.Null)
            {
                IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
                airport = runways.Count != 0 ? runways[0].Airport : Entity.Null;
            }
            if (prefab == Entity.Null || airport == Entity.Null)
            {
                Mod.Log.Warn($"Test disaster not started: {(prefab == Entity.Null ? $"no event prefab {requested}" : "no airport")}");
                return;
            }
            // Created at the end of the frame, as the developer panel does: the game initializes a new event (position, radius,
            // duration) at the start of the next frame, and an event created mid-frame would lose its Created tag in the
            // frame's clean-up first and never be initialized.
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            Entity entity = commandBuffer.CreateEntity(EntityManager.GetComponentData<EventData>(prefab).m_Archetype);
            commandBuffer.SetComponent(entity, new PrefabRef(prefab));
            commandBuffer.SetBuffer<TargetElement>(entity).Add(new TargetElement(airport));
            Mod.Log.Info($"Test disaster {m_PrefabSystem.GetPrefabName(prefab)} started at airport {airport}");
        }

        /// <summary>The named weather event prefab, or the first one when none is chosen yet.</summary>
        private Entity FindPrefab(string name)
        {
            NativeArray<Entity> prefabs = m_WeatherEventPrefabQuery.ToEntityArray(Allocator.Temp);
            Entity found = Entity.Null;
            for (int i = 0; i < prefabs.Length && found == Entity.Null; i++)
            {
                if (string.IsNullOrEmpty(name) || m_PrefabSystem.GetPrefabName(prefabs[i]) == name)
                {
                    found = prefabs[i];
                }
            }
            prefabs.Dispose();
            return found;
        }
    }
}
