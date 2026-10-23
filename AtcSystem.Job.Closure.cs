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
        private partial struct AtcJob
        {
            // ------------------------------------------------------------ airport closure

            /// <summary>
            /// Closes every runway that an active disaster of a kind chosen in the options reaches (its radius plus a margin),
            /// and every runway of an airport whose building is in danger from one. With a closure time limit, an event that
            /// has kept a runway closed that long is waived and no longer closes it. Logs each closing and reopening.
            /// </summary>
            private void UpdateClosures()
            {
                // Last tick's closed airports, to announce closings and reopenings once per airport.
                NativeParallelHashMap<Entity, Entity> before = new NativeParallelHashMap<Entity, Entity>(math.max(4, m_AirportClosures.Count()), Allocator.Temp);
                foreach (var pair in m_AirportClosures)
                {
                    before.TryAdd(pair.Key, pair.Value);
                }
                m_AirportClosures.Clear();
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    Entity runway = runways[i];
                    RunwayState state = m_Runways[runway];
                    Entity previous = state.m_ClosedTick == m_Tick - 1 ? state.m_ClosedBy : Entity.Null;
                    AtcSettings airportSettings = SettingsAt(state.m_Airport);
                    Entity closedBy = FindHazard(runway, state.m_Airport, state.m_WaivedBy, airportSettings.m_ClosureHazards);
                    if (closedBy != Entity.Null && closedBy == previous && airportSettings.m_MaxClosureFrames != 0
                        && m_Frame - state.m_ClosedFrame >= airportSettings.m_MaxClosureFrames)
                    {
                        LogEvent(closedBy, "reached the closure time limit; no longer closes runway", runway);
                        state.m_WaivedBy = closedBy;
                        closedBy = FindHazard(runway, state.m_Airport, closedBy, airportSettings.m_ClosureHazards);
                    }
                    // Closed by an add-on (the runway itself, or its whole airport): no time limit, until the add-on opens it.
                    if (closedBy == Entity.Null
                        && (m_ApiClosures.TryGetValue(runway, out Entity byAddOn) || m_ApiClosures.TryGetValue(state.m_Airport, out byAddOn)))
                    {
                        closedBy = byAddOn;
                    }
                    if (closedBy != Entity.Null && closedBy != previous)
                    {
                        state.m_ClosedFrame = m_Frame;
                    }
                    state.m_ClosedBy = closedBy;
                    state.m_ClosedTick = m_Tick;
                    m_Runways[runway] = state;
                    if (closedBy != Entity.Null && state.m_Airport != Entity.Null)
                    {
                        // The airport's reason is a disaster before an add-on's closure: one add-on closing a runway must not
                        // hide a disaster closing another (an add-on's cause is the closed runway or airport itself).
                        if (!m_AirportClosures.TryGetValue(state.m_Airport, out Entity shown)
                            || (m_ApiClosures.ContainsKey(shown) && !m_ApiClosures.ContainsKey(closedBy)))
                        {
                            m_AirportClosures[state.m_Airport] = closedBy;
                        }
                    }
                    if (closedBy != previous)
                    {
                        if (closedBy != Entity.Null)
                        {
                            LogEvent(closedBy, "closes runway", runway);
                        }
                        else
                        {
                            LogEvent(previous, "no longer closes runway; reopened", runway);
                        }
                    }
                }
                runways.Dispose();
                foreach (var pair in m_AirportClosures)
                {
                    // Newly closed, or still closed for another reason (the first one is over): announced with the reason now.
                    if (!before.TryGetValue(pair.Key, out Entity was) || was != pair.Value)
                    {
                        Broadcast(pair.Key, AtcMessageKind.AirportClosed, pair.Value);
                    }
                }
                foreach (var pair in before)
                {
                    if (!m_AirportClosures.ContainsKey(pair.Key))
                    {
                        Broadcast(pair.Key, AtcMessageKind.AirportOpen, Entity.Null);
                    }
                }
                before.Dispose();
            }

            /// <summary>
            /// The first disaster (other than <paramref name="waived"/>) that closes this runway, or Entity.Null. A weather event
            /// counts from the moment it is announced: the game may delay its start by the disaster warning time (early warning
            /// facilities), and an airport closes on the warning, not when the storm hits.
            /// </summary>
            private Entity FindHazard(Entity runway, Entity airport, Entity waived, HazardKinds hazards)
            {
                if (hazards == HazardKinds.None)
                {
                    return Entity.Null;
                }
                if (airport != Entity.Null && m_InDangerData.TryGetComponent(airport, out Game.Events.InDanger danger) && danger.m_EndFrame > m_Frame)
                {
                    Entity hazard = danger.m_Event != Entity.Null ? danger.m_Event : airport;
                    if (hazard != waived && ClosesAirport(hazard, hazards))
                    {
                        return hazard;
                    }
                }
                if (m_WeatherEvents.Length == 0 || !m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry geometry))
                {
                    return Entity.Null;
                }
                for (int i = 0; i < m_WeatherEvents.Length; i++)
                {
                    Entity hazard = m_WeatherEvents[i];
                    if (hazard == waived
                        || !m_DurationData.TryGetComponent(hazard, out Game.Events.Duration duration)
                        || m_Frame >= duration.m_EndFrame
                        || !m_WeatherPhenomenonData.TryGetComponent(hazard, out Game.Events.WeatherPhenomenon phenomenon)
                        || !ClosesAirport(hazard, hazards))
                    {
                        continue;
                    }
                    float2 center = phenomenon.m_PhenomenonPosition.xz;
                    float along = math.clamp(math.dot(center - geometry.m_Origin, geometry.m_Direction), geometry.m_Min, geometry.m_Max);
                    float reach = phenomenon.m_PhenomenonRadius + kClosureMargin;
                    if (math.distancesq(geometry.m_Origin + geometry.m_Direction * along, center) <= reach * reach)
                    {
                        return hazard;
                    }
                }
                return Entity.Null;
            }

            /// <summary>The event (or an airport in danger without one, counted as "other") is a kind chosen to close airports.</summary>
            private bool ClosesAirport(Entity hazard, HazardKinds hazards)
            {
                HazardKinds kind = m_PrefabRefData.TryGetComponent(hazard, out PrefabRef prefabRef)
                    && m_HazardKinds.TryGetValue(prefabRef.m_Prefab, out HazardKinds known) ? known : HazardKinds.Other;
                return (hazards & kind) != 0;
            }

            // ------------------------------------------------------------ ground flow control (phase 4-5)

            /// <summary>
            /// Counts an airplane that wants to move on the ground (it has a route: taxiing, waiting for the runway or a gate,
            /// pushing back, rolling out) towards its airport. One boarding at a gate has no route yet and doesn't count.
            /// </summary>
            private void CountGroundTraffic(in FlightScan scan)
            {
                if (scan.m_Flying || m_NavigationLanes[scan.m_Entity].Length == 0)
                {
                    return;
                }
                Entity airport = GetAirportOf(scan.m_Entity);
                m_GroundCounts.TryGetValue(airport, out int count);
                m_GroundCounts[airport] = count + 1;
            }

            /// <summary>
            /// Starts a ground delay at an airport with the threshold reached, and ends it once the count is two below it (so it
            /// doesn't flicker at the limit). Both are announced to all aircraft.
            /// </summary>
            private void UpdateGroundDelays()
            {
                foreach (var pair in m_GroundCounts)
                {
                    int limit = GetGroundDelayLimit(pair.Key);
                    if (limit > 0 && pair.Value >= limit && !m_GroundDelays.ContainsKey(pair.Key))
                    {
                        m_GroundDelays.Add(pair.Key, pair.Value);
                        Mod.Log.Info($"Ground delay at airport {pair.Key}: {pair.Value} airplanes moving on the ground (limit {limit})");
                        Broadcast(pair.Key, AtcMessageKind.GroundDelay, Entity.Null);
                    }
                }
                if (m_GroundDelays.IsEmpty)
                {
                    return;
                }
                NativeArray<Entity> delayed = m_GroundDelays.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < delayed.Length; i++)
                {
                    m_GroundCounts.TryGetValue(delayed[i], out int count);
                    int limit = GetGroundDelayLimit(delayed[i]);
                    if (limit <= 0 || count <= limit - 2)
                    {
                        m_GroundDelays.Remove(delayed[i]);
                        Mod.Log.Info($"Ground delay at airport {delayed[i]} ended: {count} airplanes moving on the ground");
                        Broadcast(delayed[i], AtcMessageKind.GroundDelayEnded, Entity.Null);
                    }
                }
                delayed.Dispose();
            }

            /// <summary>The airport's ground delay limit as set in its info panel; 0 when it is off there (the default).</summary>
            private int GetGroundDelayLimit(Entity airport)
            {
                if (m_GroundDelayData.TryGetComponent(airport, out GroundDelaySetting setting))
                {
                    return setting.ActiveLimit;
                }
                // Off until the player turns it on for this airport.
                return 0;
            }

            /// <summary>The airplane stands at a closed airport (its lane belongs to the airport's buildings or nets).</summary>
            private bool IsAtClosedAirport(Entity entity)
            {
                return !m_AirportClosures.IsEmpty && m_AirportClosures.ContainsKey(GetAirportOf(entity));
            }

            private float GetNoseLength(Entity prefab)
            {
                return m_PrefabGeometryData.TryGetComponent(prefab, out ObjectGeometryData geometry) ? math.max(0f, geometry.m_Bounds.max.z) : 0f;
            }

        }
    }
}
