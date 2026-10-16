using Game;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    /// <summary>
    /// Keeps <see cref="AircraftNavigationSystem"/> from moving a holding airplane along its path.
    /// The game only advances an airborne airplane to its next lane when the steering target is close
    /// (within a few kilometres), so a far-away target in the same direction makes it skip that step.
    /// <see cref="AtcSystem"/> replaces the target with the real holding target right afterwards.
    /// </summary>
    public partial class AtcPreNavigationSystem : GameSystemBase
    {
        private AtcSystem m_AtcSystem;
        private ComponentLookup<Transform> m_TransformData;
        private ComponentLookup<AircraftNavigation> m_NavigationData;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public override int GetUpdateOffset(SystemUpdatePhase phase) => 10;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_TransformData = GetComponentLookup<Transform>(true);
            m_NavigationData = GetComponentLookup<AircraftNavigation>(false);
        }

        protected override void OnUpdate()
        {
            NativeList<Entity> holding = m_AtcSystem.GetHoldingFlights(out JobHandle holdingHandle);
            m_TransformData.Update(this);
            m_NavigationData.Update(this);
            JobHandle handle = new SkipPathJob
            {
                m_HoldingFlights = holding,
                m_TransformData = m_TransformData,
                m_NavigationData = m_NavigationData,
            }.Schedule(JobHandle.CombineDependencies(Dependency, holdingHandle));
            m_AtcSystem.AddHoldingFlightsReader(handle);
            Dependency = handle;
        }

        private struct SkipPathJob : IJob
        {
            private const float kFarAway = 1000000f;

            [ReadOnly] public NativeList<Entity> m_HoldingFlights;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            public ComponentLookup<AircraftNavigation> m_NavigationData;

            public void Execute()
            {
                for (int i = 0; i < m_HoldingFlights.Length; i++)
                {
                    Entity entity = m_HoldingFlights[i];
                    if (!m_NavigationData.TryGetComponent(entity, out AircraftNavigation navigation) || !m_TransformData.TryGetComponent(entity, out Transform transform))
                    {
                        continue;
                    }
                    float2 direction = math.normalizesafe(navigation.m_TargetPosition.xz - transform.m_Position.xz, new float2(0f, 1f));
                    navigation.m_TargetPosition.xz = transform.m_Position.xz + direction * kFarAway;
                    m_NavigationData[entity] = navigation;
                }
            }
        }
    }
}
