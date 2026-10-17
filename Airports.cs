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
    /// Tag on an airport building whose tower uses "line up and wait". Saved with the city; without the mod the loader
    /// logs "Not serializable type" and drops it.
    /// </summary>
    public struct LineUpAndWaitAirport : IComponentData, IQueryTypeParameter, IEmptySerializable
    {
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
            writer.Write(m_RightHand);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_RightHand);
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
