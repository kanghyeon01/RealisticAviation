using Colossal.Entities;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Routes;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace RealisticAviation
{
    /// <summary>
    /// Tag on an airport building whose tower uses "line up and wait". Saved with the city; without the mod the loader
    /// logs "Not serializable type" and drops it.
    /// </summary>
    public struct LineUpAndWaitAirport : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
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

        /// <summary>The airport the entity belongs to (the entity itself or its top-level owner), or Entity.Null.</summary>
        public static Entity GetAirport(EntityManager entityManager, EntityQuery airplaneStops, Entity entity)
        {
            if (entity == Entity.Null || !entityManager.Exists(entity))
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
