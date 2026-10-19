using Colossal.Entities;
using Game;
using Game.Buildings;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// Colours the airport buildings in the VFR chart infoview as their airspace: blue for class B and D, magenta for class C.
    /// The game can only colour buildings by kind, and airports share theirs with every station, so this runs right after
    /// the game's own colouring (<see cref="Game.Rendering.ObjectColorSystem"/>, every frame while an infoview is open) and
    /// recolours each airport and everything it owns (terminals, hangars, upgrades and their props).
    /// </summary>
    public partial class AirportHighlightSystem : GameSystemBase
    {
        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private AtcSystem m_AtcSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
        }

        protected override void OnUpdate()
        {
            if (!AirspaceOverlaySystem.IsActive(m_ToolSystem))
            {
                return;
            }
            NativeParallelHashMap<Entity, AirportSpace> airspaces = m_AtcSystem.Airspaces;
            if (!airspaces.IsCreated || airspaces.IsEmpty)
            {
                return;
            }
            int classBD = AirspaceOverlaySystem.AirportColorIndex(EntityManager, m_PrefabSystem, AirspaceClass.B);
            int classC = AirspaceOverlaySystem.AirportColorIndex(EntityManager, m_PrefabSystem, AirspaceClass.C);
            if (classBD < 0 && classC < 0)
            {
                return;
            }
            NativeKeyValueArrays<Entity, AirportSpace> all = airspaces.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < all.Length; i++)
            {
                int index = all.Values[i].m_Class == AirspaceClass.C ? classC : classBD;
                if (index >= 0 && EntityManager.Exists(all.Keys[i]))
                {
                    Paint(all.Keys[i], new Game.Objects.Color((byte)index, 0));
                }
            }
            all.Dispose();
        }

        /// <summary>The object, its sub-objects and its upgrades, as the game colours a building together with what it owns.</summary>
        private void Paint(Entity entity, Game.Objects.Color color)
        {
            // Trees and plants keep their own colour, as the game leaves them.
            if (EntityManager.HasComponent<Game.Objects.Color>(entity) && !EntityManager.HasComponent<Tree>(entity))
            {
                EntityManager.SetComponentData(entity, color);
            }
            if (EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<Game.Objects.SubObject> subObjects))
            {
                for (int i = 0; i < subObjects.Length; i++)
                {
                    Paint(subObjects[i].m_SubObject, color);
                }
            }
            if (EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<InstalledUpgrade> upgrades))
            {
                for (int i = 0; i < upgrades.Length; i++)
                {
                    Paint(upgrades[i].m_Upgrade, color);
                }
            }
        }
    }
}
