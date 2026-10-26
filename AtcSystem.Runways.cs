using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Edge = Game.Net.Edge;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        // ---------------------------------------------------------------- runways (main thread, only after net edits)

        /// <summary>
        /// Rebuilds the runway map when taxiway nets or airports were added or removed. Runway keys survive unrelated edits.
        /// </summary>
        private void RefreshRunways()
        {
            int version = m_TaxiwayVersionQuery.GetCombinedComponentOrderVersion(false);
            int airportVersion = m_AirportVersionQuery.GetCombinedComponentOrderVersion(false);
            if (version == m_TaxiwayVersion && airportVersion == m_AirportVersion)
            {
                return;
            }
            m_TaxiwayVersion = version;
            m_AirportVersion = airportVersion;

            // The versions also move for temporary entities (tool previews, route changes), so compare the actual set of
            // taxiway edges and airplane stops before paying for a rebuild, which has to wait for the tower job.
            NativeArray<Entity> edges = m_RunwayEdgeQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Entity> stops = m_AirplaneStopQuery.ToEntityArray(Allocator.Temp);
            ulong signature = Signature(edges) * 31ul + Signature(stops);
            stops.Dispose();
            if (signature == m_RunwaySignature)
            {
                edges.Dispose();
                return;
            }
            m_RunwaySignature = signature;
            // New or changed runways: their procedures need a fresh obstacle survey.
            m_SurveyDue = true;
            RefreshGateCounts();

            // Rare (only after airport edits), so plain synchronous EntityManager/query access is fine here
            // and keeps net components out of the tower job's dependencies.
            m_JobHandle.Complete();
            EntityManager entityManager = EntityManager;
            NativeArray<PrefabRef> prefabs = m_RunwayEdgeQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            NativeArray<Edge> edgeEnds = m_RunwayEdgeQuery.ToComponentDataArray<Edge>(Allocator.Temp);
            NativeParallelHashMap<Entity, Entity> parent = new NativeParallelHashMap<Entity, Entity>(math.max(8, edges.Length * 2), Allocator.Temp);
            NativeParallelHashMap<Entity, RunwayState> kept = new NativeParallelHashMap<Entity, RunwayState>(math.max(8, m_Runways.Count()), Allocator.Temp);
            NativeList<Entity> runwayEdges = new NativeList<Entity>(edges.Length, Allocator.Temp);
            try
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity prefab = prefabs[i].m_Prefab;
                    if (!entityManager.HasComponent<TaxiwayData>(prefab)
                        || (entityManager.GetComponentData<TaxiwayData>(prefab).m_Flags & TaxiwayFlags.Runway) == 0)
                    {
                        continue;
                    }
                    Entity edge = edges[i];
                    Edge ends = edgeEnds[i];
                    runwayEdges.Add(edge);
                    Union(parent, edge, ends.m_Start);
                    Union(parent, edge, ends.m_End);
                }

                m_RunwayOf.Clear();
                NativeArray<Entity> members = parent.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < members.Length; i++)
                {
                    Entity key = Find(parent, members[i]);
                    m_RunwayOf.Add(members[i], key);
                    if (!kept.ContainsKey(key))
                    {
                        m_Runways.TryGetValue(key, out RunwayState state);
                        kept.Add(key, state);
                    }
                }
                members.Dispose();

                // Centerline extent of each runway, along the direction of its first edge (traffic pattern size and side).
                m_RunwayGeometry.Clear();
                for (int i = 0; i < runwayEdges.Length; i++)
                {
                    Entity edge = runwayEdges[i];
                    if (!entityManager.HasComponent<Curve>(edge))
                    {
                        continue;
                    }
                    Bezier4x3 bezier = entityManager.GetComponentData<Curve>(edge).m_Bezier;
                    Entity key = m_RunwayOf[edge];
                    if (!m_RunwayGeometry.TryGetValue(key, out RunwayGeometry geometry))
                    {
                        geometry.m_Origin = bezier.a.xz;
                        geometry.m_Direction = math.normalizesafe(bezier.d.xz - bezier.a.xz);
                        geometry.m_Min = float.MaxValue;
                        geometry.m_Max = float.MinValue;
                    }
                    geometry.Include(bezier.a.xz);
                    geometry.Include(bezier.d.xz);
                    geometry.m_Landings |= LandingDirections(entityManager, edge, geometry.m_Direction);
                    m_RunwayGeometry[key] = geometry;
                }

                // Flights pointing at a runway that no longer exists drop themselves on the next tick.
                m_Runways.Clear();
                NativeList<Entity> airports = new NativeList<Entity>(4, Allocator.Temp);
                Airports.Collect(entityManager, m_AirplaneStopQuery, airports);
                m_RunwayList.Clear();
                Dictionary<Entity, RunwayInfo> infos = new Dictionary<Entity, RunwayInfo>();
                foreach (var pair in kept)
                {
                    RunwayState state = pair.Value;
                    state.m_Airport = FindAirport(entityManager, pair.Key, airports);
                    m_Runways.Add(pair.Key, state);
                    if (m_RunwayGeometry.TryGetValue(pair.Key, out RunwayGeometry geometry))
                    {
                        RunwayInfo info = new RunwayInfo(pair.Key, state.m_Airport, geometry);
                        infos.Add(pair.Key, info);
                        m_RunwayList.Add(info);
                    }
                }
                for (int i = 0; i < runwayEdges.Length; i++)
                {
                    if (infos.TryGetValue(m_RunwayOf[runwayEdges[i]], out RunwayInfo info))
                    {
                        info.Edges.Add(runwayEdges[i]);
                    }
                }
                airports.Dispose();
                Mod.Log.Info($"Found {m_Runways.Count()} runways.");
                // After the runway list is rebuilt: the airspaces are made from it.
                RefreshAirspaces();
                Api.Events.RaiseRunwaysChanged();
            }
            finally
            {
                runwayEdges.Dispose();
                kept.Dispose();
                parent.Dispose();
                edgeEnds.Dispose();
                prefabs.Dispose();
                edges.Dispose();
            }
        }

        /// <summary>
        /// Which way the runway lanes of an edge run relative to the runway axis. Game runways are usually one-way, so this
        /// tells which end airplanes actually land on and take off from.
        /// </summary>
        private static byte LandingDirections(EntityManager entityManager, Entity edge, float2 axis)
        {
            if (!entityManager.HasBuffer<Game.Net.SubLane>(edge))
            {
                return 0;
            }
            byte directions = 0;
            DynamicBuffer<Game.Net.SubLane> subLanes = entityManager.GetBuffer<Game.Net.SubLane>(edge, true);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity lane = subLanes[i].m_SubLane;
                if (!entityManager.HasComponent<Game.Net.CarLane>(lane) || !entityManager.HasComponent<Curve>(lane)
                    || (entityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags & Game.Net.CarLaneFlags.Runway) == 0)
                {
                    continue;
                }
                Bezier4x3 bezier = entityManager.GetComponentData<Curve>(lane).m_Bezier;
                directions |= math.dot(bezier.d.xz - bezier.a.xz, axis) >= 0f ? RunwayGeometry.kAlong : RunwayGeometry.kAgainst;
            }
            return directions;
        }

        /// <summary>Order-independent fingerprint of a set of entities.</summary>
        private static ulong Signature(NativeArray<Entity> entities)
        {
            ulong signature = (ulong)entities.Length;
            for (int i = 0; i < entities.Length; i++)
            {
                ulong value = ((ulong)(uint)entities[i].Index << 32) | (uint)entities[i].Version;
                signature += value * 0x9E3779B97F4A7C15ul ^ (value >> 29);
            }
            return signature;
        }

        /// <summary>
        /// The airport a runway belongs to: the airport that owns it when it is part of an airport building, otherwise
        /// (a separately built runway) the nearest airport.
        /// </summary>
        private static Entity FindAirport(EntityManager entityManager, Entity runwayKey, NativeList<Entity> airports)
        {
            if (airports.Length == 0)
            {
                return Entity.Null;
            }
            Entity root = Airports.GetRoot(entityManager, runwayKey);
            if (airports.Contains(root))
            {
                return root;
            }
            float3 position;
            if (entityManager.HasComponent<Curve>(runwayKey))
            {
                position = MathUtils.Position(entityManager.GetComponentData<Curve>(runwayKey).m_Bezier, 0.5f);
            }
            else if (entityManager.HasComponent<Game.Net.Node>(runwayKey))
            {
                position = entityManager.GetComponentData<Game.Net.Node>(runwayKey).m_Position;
            }
            else
            {
                return Entity.Null;
            }
            Entity nearest = Entity.Null;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < airports.Length; i++)
            {
                if (!entityManager.HasComponent<Transform>(airports[i]))
                {
                    continue;
                }
                float distance = math.distancesq(entityManager.GetComponentData<Transform>(airports[i]).m_Position.xz, position.xz);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = airports[i];
                }
            }
            return nearest;
        }

        // Union-find with the lower entity index as root, so a runway keeps its key when unrelated nets change.
        private static Entity Find(NativeParallelHashMap<Entity, Entity> parent, Entity entity)
        {
            Entity root = entity;
            while (true)
            {
                if (!parent.TryGetValue(root, out Entity up))
                {
                    parent.Add(root, root);
                    return root;
                }
                if (up == root)
                {
                    break;
                }
                root = up;
            }
            // Path compression.
            while (entity != root)
            {
                Entity up = parent[entity];
                parent[entity] = root;
                entity = up;
            }
            return root;
        }

        private static void Union(NativeParallelHashMap<Entity, Entity> parent, Entity a, Entity b)
        {
            Entity rootA = Find(parent, a);
            Entity rootB = Find(parent, b);
            if (rootA == rootB)
            {
                return;
            }
            if (rootB.Index < rootA.Index)
            {
                parent[rootA] = rootB;
            }
            else
            {
                parent[rootB] = rootA;
            }
        }
    }
}
