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
            // ------------------------------------------------------------ radio (phase 4-2)

            /// <summary>
            /// Records a tower transmission: it becomes the airplane's latest message and goes into the radio log. Only these
            /// small records are kept here; the words are built on the main thread when an info panel shows them.
            /// </summary>
            private void Say(Entity airplane, AtcMessageKind kind, Entity runway, float2 direction, int value = 0, Entity hazard = default)
            {
                Entity airport = m_Runways.TryGetValue(runway, out RunwayState state) ? state.m_Airport : GetAirportOf(airplane);
                SayAt(airplane, kind, airport, runway, direction, value, hazard);
            }

            /// <summary>A transmission by the given airport's controller (<paramref name="hazard"/>: the disaster, or for a handoff the next airport).</summary>
            private void SayAt(Entity airplane, AtcMessageKind kind, Entity airport, Entity runway, float2 direction, int value, Entity hazard,
                AtcFrequency? frequency = null)
            {
                AtcMessage message = new AtcMessage
                {
                    m_Airplane = airplane,
                    m_Runway = runway,
                    m_Airport = airport,
                    m_Direction = direction,
                    m_Event = hazard,
                    m_Value = value,
                    m_Frame = m_Frame,
                    m_Kind = kind,
                    m_Frequency = frequency ?? FrequencyAt(airport, kind),
                };
                if (airplane != Entity.Null && m_TransformData.TryGetComponent(airplane, out Transform transform))
                {
                    // Height above the airport (as the hold altitudes), for the pilot's check-in.
                    float fieldHeight = m_Airspaces.TryGetValue(airport, out AirportSpace space) ? space.m_Height : 0f;
                    message.m_Altitude = math.max(0, (int)math.round((transform.m_Position.y - fieldHeight) / (100f * kMetersPerFoot)) * 100);
                    float climb = m_MovingData.TryGetComponent(airplane, out Moving moving) ? moving.m_Velocity.y : 0f;
                    message.m_Vertical = (sbyte)(climb > kLevelClimbRate ? 1 : climb < -kLevelClimbRate ? -1 : 0);
                }
                if (IsFirstCall(airplane, kind))
                {
                    AttachAtis(ref message);
                }
                m_LastMessages[airplane] = message;
                AddToRadioLog(message);
            }

            /// <summary>
            /// A pilot's first call to the airport, the one that carries "with information X": the pushback request, the
            /// first taxi request of a departure that needs no pushback, and an arrival's initial contact (approach, or the
            /// tower at class D).
            /// </summary>
            private bool IsFirstCall(Entity airplane, AtcMessageKind kind)
            {
                if (kind == AtcMessageKind.PushbackApproved || kind == AtcMessageKind.InitialContact)
                {
                    return true;
                }
                return kind == AtcMessageKind.TaxiToRunway
                    && !(m_LastMessages.TryGetValue(airplane, out AtcMessage last) && last.m_Kind == AtcMessageKind.PushbackApproved);
            }

            /// <summary>
            /// The ATIS letter the pilot reports and the current one. A pilot listens to the ATIS a few minutes before the call
            /// (2 to 10 game minutes, fixed for each airplane): one that listened before the latest change reports the previous
            /// letter, and the controller gives the current one.
            /// </summary>
            private void AttachAtis(ref AtcMessage message)
            {
                if (!m_AtisStates.TryGetValue(message.m_Airport, out AtisState atis))
                {
                    return;
                }
                float listenedAgo = 2f + math.hash(new int2(message.m_Airplane.Index, message.m_Airplane.Version)) % 9u;
                bool stale = atis.m_PreviousLetter >= 0 && atis.m_MinutesSinceChange < listenedAgo;
                message.m_AtisReported = (byte)((stale ? atis.m_PreviousLetter : atis.m_Letter) + 1);
                message.m_AtisCurrent = (byte)(atis.m_Letter + 1);
                message.m_AtisQnh = (short)atis.m_Qnh;
            }

            private void Say(Entity airplane, AtcMessageKind kind, in Flight flight, int value = 0, Entity hazard = default)
            {
                Say(airplane, kind, flight.m_Runway, flight.m_EntryDirection, value, hazard);
            }

            /// <summary>A transmission to all aircraft of an airport ("Attention all aircraft"): no readback, no airplane.</summary>
            private void Broadcast(Entity airport, AtcMessageKind kind, Entity hazard)
            {
                AtcMessage message = new AtcMessage
                {
                    m_Airport = airport,
                    m_Event = hazard,
                    m_Frame = m_Frame,
                    m_Kind = kind,
                };
                AddToRadioLog(message);
            }

            private void AddToRadioLog(in AtcMessage message)
            {
                // Diagnostics: every transmission, so a missing one can be told from one that was only scrolled away.
                if (m_Settings.m_DetailedLog)
                {
                    Mod.Log.Info($"radio {message.m_Frequency} {message.m_Kind} {(message.m_Airplane == Entity.Null ? "all aircraft" : message.m_Airplane.ToString())} runway {message.m_Runway} airport {message.m_Airport}");
                }
                m_RadioVersion.Value++;
                if (m_RadioLog.Length >= kRadioLogLength)
                {
                    m_RadioLog.RemoveAt(0);
                }
                m_RadioLog.Add(message);
            }

            /// <summary>
            /// Ground control's taxi clearance for a departure, given once when it is ready to leave its stand (route known,
            /// pushback done or not needed), naming the runway its route takes off from. It comes before any runway request
            /// (see <see cref="HasTaxiClearance"/>): at small airports the stand can be within the request distance of the runway.
            /// An airplane that taxied in after landing gets it once it stands again with a route onto a runway.
            /// </summary>
            private void ClearToTaxi(Entity entity)
            {
                if (m_NavigationLanes[entity].Length == 0)
                {
                    return;
                }
                bool heard = m_LastMessages.TryGetValue(entity, out AtcMessage last);
                if (heard && (last.m_Kind == AtcMessageKind.TaxiToRunway || last.m_Kind == AtcMessageKind.HoldForGate))
                {
                    // Already cleared to taxi, or waiting for its gate.
                    return;
                }
                if (heard && last.m_Kind == AtcMessageKind.TaxiToGate)
                {
                    // Taxied in after landing: stopped again with a route that takes off, it has boarded and is cleared out.
                    if (math.lengthsq(m_MovingData[entity].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed
                        || !FindTakeoffRunway(entity, out Entity nextRunway, out float2 nextDirection))
                    {
                        return;
                    }
                    Say(entity, AtcMessageKind.TaxiToRunway, nextRunway, nextDirection);
                    LogEvent(entity, "cleared to taxi to runway", nextRunway);
                    return;
                }
                // No taxi message yet (none at all, the pushback approval, or something left over from the air or the runway):
                // leaving its stand, or (right after loading a city, when nobody has heard anything) already taxiing. One taxiing
                // in to a gate here is told so; anyone else is cleared to the runway, so that it can ask for it (without a runway
                // in sight of the path scan, just "taxi to the runway").
                if (!FindTakeoffRunway(entity, out Entity runway, out float2 direction) && IsTaxiingToGate(entity))
                {
                    Say(entity, AtcMessageKind.TaxiToGate, Entity.Null, default);
                    return;
                }
                Say(entity, AtcMessageKind.TaxiToRunway, runway, direction);
                LogEvent(entity, "cleared to taxi to runway", runway);
            }

            /// <summary>
            /// A ground airplane may ask the tower for a runway only after ground control cleared it to taxi (or to taxi in
            /// after landing, for a crossing on the way to its gate) and its pushback is over. "Taxi to the gate" doesn't
            /// clear a route that takes off: that airplane has boarded and waits for its pushback and "taxi to runway". It calls only
            /// once it has started taxiing.
            /// </summary>
            private bool HasTaxiClearance(Entity entity)
            {
                if (m_Pushbacks.ContainsKey(entity) || !m_LastMessages.TryGetValue(entity, out AtcMessage last)
                    || last.m_Kind == AtcMessageKind.PushbackApproved)
                {
                    return false;
                }
                // Cleared to taxi: it calls the tower once it is taxiing, not from the stand (after the pushback, or while reading
                // the clearance back).
                if (last.m_Kind == AtcMessageKind.TaxiToRunway)
                {
                    return math.lengthsq(m_MovingData[entity].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed;
                }
                // Taxiing in to its gate: only for a crossing on the way. Anything else (a message left over from the air, e.g.)
                // is no taxi clearance: ground clears it first (ClearToTaxi).
                return (last.m_Kind == AtcMessageKind.TaxiToGate || last.m_Kind == AtcMessageKind.HoldForGate)
                    && !FindTakeoffRunway(entity, out _, out _);
            }

            /// <summary>The first runway the route takes off from (a lane of the runway proper, not a crossing), and the roll direction.</summary>
            private bool FindTakeoffRunway(Entity entity, out Entity runway, out float2 direction)
            {
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    if (IsTakeoffLane(navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, out runway, out direction))
                    {
                        return true;
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int start = m_PathOwnerData[entity].m_ElementIndex;
                int end = math.min(path.Length, start + kMaxPathScan);
                for (int i = start; i < end; i++)
                {
                    if (IsTakeoffLane(path[i].m_Target, path[i].m_TargetDelta, out runway, out direction))
                    {
                        return true;
                    }
                }
                runway = Entity.Null;
                direction = default;
                return false;
            }

            private bool IsTakeoffLane(Entity lane, float2 delta, out Entity runway, out float2 direction)
            {
                runway = Entity.Null;
                direction = default;
                if (!m_CarLaneData.TryGetComponent(lane, out Game.Net.CarLane carLane) || (carLane.m_Flags & Game.Net.CarLaneFlags.Runway) == 0
                    || !m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                runway = RunwayOfLane(lane);
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                direction = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                return runway != Entity.Null;
            }

            /// <summary>
            /// Handoffs between airspaces (phase 4-3): an airborne airplane is with the controller of the zone it is in. An
            /// arrival stays with its destination once it has called it; anyone else crossing into another zone is handed over
            /// ("contact Portland Approach ...") and calls the next controller, and leaving the last zone ends radar service.
            /// </summary>
            private void UpdateZone(in FlightScan scan)
            {
                if (!scan.m_Flying || m_Airspaces.IsEmpty)
                {
                    return;
                }
                Entity entity = scan.m_Entity;
                m_Zones.TryGetValue(entity, out ZoneState zone);
                if (zone.m_Climbing)
                {
                    // Just took off: it checks in with departure control once it has climbed, then is handed on as usual.
                    float fieldHeight = m_Airspaces.TryGetValue(zone.m_Airport, out AirportSpace field) ? field.m_Height : 0f;
                    zone.m_Tick = m_Tick;
                    if (m_TransformData[entity].m_Position.y - fieldHeight < kCheckInHeight && m_Frame - zone.m_TakeoffFrame < kCheckInTimeoutFrames)
                    {
                        m_Zones[entity] = zone;
                        return;
                    }
                    zone.m_Climbing = false;
                    m_Zones[entity] = zone;
                    if (zone.m_DepartureControl != 0)
                    {
                        SayAt(entity, AtcMessageKind.DepartureCheckIn, zone.m_Airport, Entity.Null, default, zone.m_DepartureControl, Entity.Null,
                            zone.m_DepartureControl == 2 ? AtcFrequency.Departure : AtcFrequency.Approach);
                    }
                }
                if (m_Flights.TryGetValue(entity, out Flight flight))
                {
                    // An arrival stays with its destination; a departure lifting off is still the tower's until it leaves the
                    // runway ("contact departure").
                    if (flight.m_Arrival)
                    {
                        zone.m_Tick = m_Tick;
                        m_Zones[entity] = zone;
                    }
                    return;
                }
                float2 position = m_TransformData[entity].m_Position.xz;
                Entity owner = ZoneOwner(position);
                // It stays with its controller until clearly out of that zone (its edge plus a margin, so no ping-pong along a
                // boundary), unless it entered a higher class's inner surface area.
                if (owner != zone.m_Airport && zone.m_Airport != Entity.Null && m_Airspaces.TryGetValue(zone.m_Airport, out AirportSpace current)
                    && math.distance(position, current.m_Center) <= current.m_Radius + kZoneExitMargin
                    && !(owner != Entity.Null && m_Airspaces[owner].m_Class > current.m_Class
                        && math.distance(position, m_Airspaces[owner].m_Center) < m_Airspaces[owner].m_Radius * kInnerZoneFactor))
                {
                    owner = zone.m_Airport;
                }
                if (owner == zone.m_Airport)
                {
                    if (owner != Entity.Null)
                    {
                        zone.m_Tick = m_Tick;
                        m_Zones[entity] = zone;
                    }
                    return;
                }
                if (zone.m_Airport != Entity.Null && !zone.m_Silent)
                {
                    if (owner != Entity.Null)
                    {
                        SayAt(entity, AtcMessageKind.HandoffTo, zone.m_Airport, Entity.Null, default, (int)m_Airspaces[owner].m_Class, owner, DepartureFrequency(zone));
                    }
                    else
                    {
                        SayAt(entity, AtcMessageKind.ContactCenter, zone.m_Airport, Entity.Null, default, 0, Entity.Null, DepartureFrequency(zone));
                    }
                }
                if (owner == Entity.Null)
                {
                    m_Zones.Remove(entity);
                    return;
                }
                SayAt(entity, AtcMessageKind.TransitContact, owner, Entity.Null, default, (int)m_Airspaces[owner].m_Class, Entity.Null);
                m_Zones[entity] = new ZoneState { m_Airport = owner, m_Tick = m_Tick };
            }

            /// <summary>Just took off and not yet checked in with departure: it doesn't call the next airport yet.</summary>
            private bool IsClimbingOut(Entity entity)
            {
                return m_Zones.TryGetValue(entity, out ZoneState zone) && zone.m_Climbing;
            }

            /// <summary>A class B departure still with its own airport is on that airport's departure frequency; else the usual one.</summary>
            private static AtcFrequency? DepartureFrequency(in ZoneState zone)
            {
                return zone.m_Departing ? AtcFrequency.Departure : (AtcFrequency?)null;
            }

            /// <summary>
            /// The airport whose airspace a point is in, as the airspace infoview draws it: a higher class's inner surface area
            /// first, else the zone the point is relatively deepest in (distance / radius); Entity.Null outside every zone.
            /// </summary>
            private Entity ZoneOwner(float2 point)
            {
                Entity owner = Entity.Null;
                float best = float.MaxValue;
                foreach (var pair in m_Airspaces)
                {
                    AirportSpace space = pair.Value;
                    float depth = math.distance(point, space.m_Center) / space.m_Radius;
                    if (depth > 1f)
                    {
                        continue;
                    }
                    if (space.m_Class != AirspaceClass.D && depth < kInnerZoneFactor)
                    {
                        depth -= 10f * (int)space.m_Class;
                    }
                    if (depth < best)
                    {
                        best = depth;
                        owner = pair.Key;
                    }
                }
                return owner;
            }

            /// <summary>Forgets airplanes that landed or are gone (not airborne for a while).</summary>
            private void EndZoneTick()
            {
                if (m_Zones.IsEmpty || m_Tick % kDiagnosticInterval != 0)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Zones)
                {
                    if (m_Tick - pair.Value.m_Tick > kDiagnosticInterval)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_Zones.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>The airspace class of a runway's airport (C when unknown).</summary>
            private AirspaceClass GetAirspaceClass(Entity runway)
            {
                return m_Runways.TryGetValue(runway, out RunwayState state) && m_Airspaces.TryGetValue(state.m_Airport, out AirportSpace space)
                    ? space.m_Class
                    : AirspaceClass.C;
            }

            private bool HasApproachControl(Entity runway) => GetAirspaceClass(runway) != AirspaceClass.D;

            /// <summary>The frequency of a message at this airport: a class D airport has no approach control, so its tower says it.</summary>
            private AtcFrequency FrequencyAt(Entity airport, AtcMessageKind kind)
            {
                AtcFrequency frequency = AtcMessage.FrequencyOf(kind);
                return frequency == AtcFrequency.Approach && m_Airspaces.TryGetValue(airport, out AirportSpace space) && space.m_Class == AirspaceClass.D
                    ? AtcFrequency.Tower
                    : frequency;
            }

            /// <summary>The disaster closing this runway this tick, or Entity.Null.</summary>
            private Entity ClosedBy(Entity runway)
            {
                return m_Runways.TryGetValue(runway, out RunwayState state) && state.IsClosed(m_Tick) ? state.m_ClosedBy : Entity.Null;
            }

            /// <summary>Forgets the latest message of airplanes that haven't heard from the tower for a long time (or are gone).</summary>
            private void PruneMessages()
            {
                if (m_LastMessages.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> old = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_LastMessages)
                {
                    if (m_Frame - pair.Value.m_Frame > kMessageLifetimeFrames)
                    {
                        old.Add(pair.Key);
                    }
                }
                for (int i = 0; i < old.Length; i++)
                {
                    m_LastMessages.Remove(old[i]);
                }
                old.Dispose();
            }

            /// <summary>The airport an airplane stands at: the top owner of its lane.</summary>
            private Entity GetAirportOf(Entity airplane)
            {
                Entity root = m_CurrentLaneData[airplane].m_Lane;
                while (m_OwnerData.TryGetComponent(root, out Owner owner))
                {
                    root = owner.m_Owner;
                }
                return root;
            }

            private void LogLongWait(Entity entity, Flight flight)
            {
                if (!m_Settings.m_DetailedLog)
                {
                    return;
                }
                m_Runways.TryGetValue(flight.m_Runway, out RunwayState state);
                int occupants = state.Occupants(m_Tick);
                Mod.Log.Info($"{entity} waiting {(m_Frame - flight.m_RequestFrame) / 60} s for runway {flight.m_Runway} " +
                    $"({Describe(flight)}, #{flight.m_Sequence}); ground: {DescribeCleared(state.m_Holder)}; " +
                    $"next arrival ETA: {(state.HasArrivalInSequence(m_Tick) ? state.m_NextArrivalEta.ToString("F0") + " s" : "none")}; closed: {state.IsClosed(m_Tick)}; lined up: {DescribeCleared(state.m_LineUp)}; airplanes on runway: {occupants}");
            }

            private string DescribeCleared(Entity entity)
            {
                if (entity == Entity.Null || !m_Flights.TryGetValue(entity, out Flight flight))
                {
                    return "none";
                }
                return $"{entity} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, on runway: {flight.m_SeenOnRunway}, " +
                    $"cleared {(m_Frame - flight.m_ClearedFrame) / 60} s ago, holding: {flight.m_Holding}{DescribeBlocker(entity)})";
            }

            /// <summary>
            /// Diagnostics for gate blocks (research, see PLAN): once per landing, the lanes from the airplane to its gate. Each
            /// lane is "index:length/owner" with flags: T = the owner is a taxiway net, R = a runway, A = its top owner is the
            /// airport, S = a stand lane of the gate (the waypoint's RouteLane). Also who is boarding at the gate.
            /// </summary>
            private void LogPathToGate(Entity entity, Entity airport)
            {
                if (!m_Settings.m_DetailedLog)
                {
                    return;
                }
                string gate = "none";
                Entity waypoint = Entity.Null;
                Game.Routes.RouteLane stand = default;
                if (m_TargetData.TryGetComponent(entity, out Game.Common.Target target))
                {
                    waypoint = target.m_Target;
                    m_RouteLaneData.TryGetComponent(waypoint, out stand);
                    if (m_ConnectedData.TryGetComponent(waypoint, out Game.Routes.Connected connected))
                    {
                        gate = m_BoardingVehicleData.TryGetComponent(connected.m_Connected, out Game.Routes.BoardingVehicle boarding)
                            ? $"{connected.m_Connected} (boarding: {boarding.m_Vehicle})"
                            : connected.m_Connected.ToString();
                    }
                }
                System.Text.StringBuilder lanes = new System.Text.StringBuilder();
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AppendLane(lanes, navigationLanes[i].m_Lane, airport, stand);
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, m_PathOwnerData[entity].m_ElementIndex + kMaxPathScan);
                for (int i = m_PathOwnerData[entity].m_ElementIndex; i < end; i++)
                {
                    AppendLane(lanes, path[i].m_Target, airport, stand);
                }
                Mod.Log.Info($"{entity} path to gate {gate} (waypoint {waypoint}, airport {airport}):{lanes}");
            }

            private void AppendLane(System.Text.StringBuilder lanes, Entity lane, Entity airport, in Game.Routes.RouteLane stand)
            {
                Entity owner = m_OwnerData.TryGetComponent(lane, out Owner laneOwner) ? laneOwner.m_Owner : Entity.Null;
                Entity top = owner;
                while (m_OwnerData.TryGetComponent(top, out Owner next))
                {
                    top = next.m_Owner;
                }
                float length = m_CurveData.TryGetComponent(lane, out Curve curve) ? curve.m_Length : 0f;
                lanes.Append(' ').Append(lane.Index).Append(':').Append(length.ToString("F0")).Append('/').Append(owner.Index);
                if (m_TaxiwayData.HasComponent(owner))
                {
                    lanes.Append('T');
                }
                if (m_RunwayOf.ContainsKey(owner))
                {
                    lanes.Append('R');
                }
                if (top == airport && airport != Entity.Null)
                {
                    lanes.Append('A');
                }
                if (lane == stand.m_StartLane || lane == stand.m_EndLane)
                {
                    lanes.Append('S');
                }
            }

            /// <summary>What the game itself is waiting for, if anything (diagnostics for an airplane that stopped).</summary>
            private string DescribeBlocker(Entity entity)
            {
                string held = m_HeldBehind.TryGetValue(entity, out Entity ahead) ? $", held by the mod behind {ahead}" : string.Empty;
                return m_BlockerData.TryGetComponent(entity, out Blocker blocker) && blocker.m_Blocker != Entity.Null
                    ? $", blocked by {blocker.m_Blocker} ({blocker.m_Type}){held}"
                    : held;
            }

            private uint NextSequence()
            {
                uint sequence = m_Sequence.Value + 1;
                m_Sequence.Value = sequence;
                return sequence;
            }

            private void DropFlight(Entity entity, Flight flight)
            {
                ReleaseRunway(entity, flight.m_Runway);
                if (flight.m_Holding)
                {
                    FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                }
                m_Flights.Remove(entity);
            }

            private void ReleaseRunway(Entity entity, Entity runway)
            {
                if (m_Runways.TryGetValue(runway, out RunwayState state)
                    && (state.m_Holder == entity || state.m_LineUp == entity))
                {
                    if (state.m_LineUp == entity)
                    {
                        state.m_LineUp = Entity.Null;
                    }
                    if (state.m_Holder == entity)
                    {
                        state.m_Holder = Entity.Null;
                    }
                    m_Runways[runway] = state;
                }
            }

            /// <summary>Drops requests of airplanes that no longer exist or were parked.</summary>
            private void RemoveStaleFlights()
            {
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    DropFlight(stale[i], m_Flights[stale[i]]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// Highest Response Ratio Next: ratio = (time waited + expected runway time) / expected runway time.
            /// Quick users of the runway go first, but every waiting airplane's ratio keeps growing, so none waits forever.
            /// </summary>
            private static float ResponseRatio(float waitedSeconds, float serviceSeconds)
            {
                serviceSeconds = math.max(serviceSeconds, kMinServiceSeconds);
                return (waitedSeconds + serviceSeconds) / serviceSeconds;
            }

            private float WaitedSeconds(uint requestFrame)
            {
                return (m_Frame - requestFrame) * kSecondsPerFrame;
            }

            /// <summary>
            /// For every runway with no ground airplane on or cleared for it, picks by response ratio between the best
            /// arrival and the head of the ground queue. The arrival is cleared when it wins, or when it is far enough out
            /// for the ground airplane to go first; the ground airplane goes when it wins or the cleared arrival is still at
            /// least the departure gap away.
            /// </summary>
            private void GrantRunways()
            {
                ReleaseLinedUp();
                UnblockGroundQueues();

                NativeParallelHashMap<Entity, Candidates> next = new NativeParallelHashMap<Entity, Candidates>(math.max(8, m_Runways.Count()), Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    Flight flight = pair.Value;
                    if (flight.m_Clearance != Clearance.Waiting
                        || !m_Runways.TryGetValue(flight.m_Runway, out RunwayState state)
                        || state.IsClosed(m_Tick))
                    {
                        continue;
                    }
                    // Held by an add-on: it goes on waiting (holding in the air, short of the runway on the ground).
                    if (m_ApiHeld.ContainsKey(pair.Key))
                    {
                        continue;
                    }
                    float gap = SettingsAt(state.m_Airport).m_DepartureGapSeconds;
                    bool priority = m_ApiPriority.ContainsKey(pair.Key);
                    next.TryGetValue(flight.m_Runway, out Candidates candidates);
                    if (flight.m_LastFlying)
                    {
                        // Ground delay: no new arrivals until the ground traffic has thinned out (the departures go first).
                        if (m_GroundDelays.ContainsKey(state.m_Airport))
                        {
                            continue;
                        }
                        // Given priority by an add-on (an emergency, say): ahead of every other arrival.
                        float ratio = ResponseRatio(WaitedSeconds(flight.m_RequestFrame), flight.m_LastEta + kLandingRunwaySeconds)
                            + (priority ? kPriorityRatio : 0f);
                        if (candidates.m_Air == Entity.Null || ratio > candidates.m_AirRatio)
                        {
                            candidates.m_Air = pair.Key;
                            candidates.m_AirRatio = ratio;
                            // It joins only behind the last arrival cleared, with the spacing (and wake) kept. If it can't
                            // yet, it keeps its turn anyway: nobody else joins, so the sequence drains until it fits (a
                            // go-around airplane close to the runway would otherwise never get in).
                            candidates.m_AirReady = flight.m_LastEta >= EarliestArrivalEta(state, flight.m_Size, gap);
                        }
                    }
                    else
                    {
                        // Physically stuck behind another airplane waiting for this runway: that one is the real head of the line
                        // (path distances can't tell, e.g. when it heads for a farther entry point along the same taxiway).
                        if (IsQueuedBehindWaiting(pair.Key, flight.m_Runway))
                        {
                            continue;
                        }
                        // Cleared only once it is coming up to its hold-short point, not right after its pushback: the tower
                        // gives the takeoff (or crossing) clearance to an airplane at the runway.
                        if (flight.m_LastDistance > ClearanceDistance(pair.Key))
                        {
                            continue;
                        }
                        if (!state.IsFree(m_Tick) && !CanLineUp(state))
                        {
                            continue;
                        }
                        if (candidates.m_Ground == Entity.Null || (int)(flight.m_RequestFrame - candidates.m_OldestGroundRequestFrame) < 0)
                        {
                            candidates.m_OldestGroundRequestFrame = flight.m_RequestFrame;
                        }
                        // An add-on's priority airplane first; otherwise the nearest goes first, and at the same spot (e.g. both
                        // at the hold-short line) the one that asked first.
                        if (candidates.m_Ground == Entity.Null || (priority && !candidates.m_GroundPriority)
                            || (priority == candidates.m_GroundPriority
                                && (flight.m_LastDistance < candidates.m_GroundDistance - kSameSpotDistance
                                    || (flight.m_LastDistance <= candidates.m_GroundDistance + kSameSpotDistance
                                        && (int)(flight.m_RequestFrame - candidates.m_GroundRequestFrame) < 0))))
                        {
                            candidates.m_Ground = pair.Key;
                            candidates.m_GroundDistance = flight.m_LastDistance;
                            candidates.m_GroundRequestFrame = flight.m_RequestFrame;
                            candidates.m_GroundPriority = priority;
                        }
                    }
                    next[flight.m_Runway] = candidates;
                }

                foreach (var pair in next)
                {
                    Entity runway = pair.Key;
                    Candidates candidates = pair.Value;
                    RunwayState state = m_Runways[runway];
                    float gap = SettingsAt(state.m_Airport).m_DepartureGapSeconds;
                    float groundRatio = candidates.m_Ground == Entity.Null ? 0f : ResponseRatio(
                        WaitedSeconds(candidates.m_OldestGroundRequestFrame),
                        candidates.m_GroundDistance / kTaxiSpeedEstimate + kTakeoffRunwaySeconds)
                        + (candidates.m_GroundPriority ? kPriorityRatio : 0f);
                    bool groundFirst = candidates.m_Ground != Entity.Null
                        && (candidates.m_Air == Entity.Null || groundRatio > candidates.m_AirRatio);

                    // The next arrival joins the sequence, unless the ground airplane has waited longer (relatively): then
                    // no new arrival joins (the sequence drains to make a hole), except a first one far enough out to go after
                    // it, and only while the runway is free, so that the ground airplane is cleared ahead of it right now.
                    // (Clearing it earlier let the hole close again before the runway was free: departures waited 10 minutes.)
                    if (candidates.m_AirReady)
                    {
                        Flight arrival = m_Flights[candidates.m_Air];
                        bool inSequence = state.HasArrivalInSequence(m_Tick);
                        if (!groundFirst || (!inSequence && state.IsFree(m_Tick) && arrival.m_LastEta > gap))
                        {
                            state.BeginArrivals(m_Tick);
                            state.AddArrival(arrival.m_LastEta, arrival.m_Size);
                            m_Runways[runway] = state;
                            Clear(candidates.m_Air, runway, inSequence ? "to land in sequence" : "to land", candidates.m_AirRatio);
                        }
                    }
                    if (candidates.m_Ground == Entity.Null)
                    {
                        continue;
                    }
                    if (state.IsFree(m_Tick))
                    {
                        if (state.NextArrivalEta(m_Tick) > gap)
                        {
                            state.m_Holder = candidates.m_Ground;
                            m_Runways[runway] = state;
                            Clear(candidates.m_Ground, runway, state.HasArrivalInSequence(m_Tick) ? "ahead of the arrival onto" : "onto", groundRatio);
                        }
                    }
                    else if (MayLineUpBehindTraffic(state, m_Flights[candidates.m_Ground], gap, out Entity traffic))
                    {
                        // Busy, but this airport uses line up and wait: the head of the ground queue may line up behind
                        // traffic that has already passed its entry point.
                        state.m_LineUp = candidates.m_Ground;
                        m_Runways[runway] = state;
                        Flight flight = m_Flights[candidates.m_Ground];
                        flight.m_Clearance = Clearance.LineUp;
                        flight.m_ClearedFrame = m_Frame;
                        m_Flights[candidates.m_Ground] = flight;
                        Say(candidates.m_Ground, AtcMessageKind.LineUpAndWait, flight);
                        LogEvent(candidates.m_Ground, $"cleared to line up and wait (behind {traffic}, waited {(m_Frame - flight.m_RequestFrame) / 60} s) on runway", runway);
                    }
                }
                next.Dispose();
            }

            /// <summary>Path distance to the runway within which a ground airplane can get its clearance: its hold-short point plus a margin.</summary>
            private float ClearanceDistance(Entity entity)
            {
                return GetNoseLength(m_PrefabRefData[entity].m_Prefab) + m_Settings.m_HoldShortSetback + kGroundClearanceMargin;
            }

            /// <summary>
            /// A ground airplane cleared onto (or to line up on) a runway that the taxi spacing holds behind another airplane
            /// still waiting for the same runway can never get there: they would wait for each other forever. Its clearance goes
            /// back (it keeps its place in time) so that the airplane ahead, the real head of the line, is cleared instead.
            /// </summary>
            private void UnblockGroundQueues()
            {
                if (m_HeldBehind.IsEmpty)
                {
                    return;
                }
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    RunwayState state = m_Runways[runways[i]];
                    TakeBackIfQueued(state.m_Holder, runways[i]);
                    TakeBackIfQueued(state.m_LineUp, runways[i]);
                }
                runways.Dispose();
            }

            private void TakeBackIfQueued(Entity entity, Entity runway)
            {
                if (entity == Entity.Null || !IsQueuedBehindWaiting(entity, runway) || !m_Flights.TryGetValue(entity, out Flight flight)
                    || flight.m_SeenOnRunway)
                {
                    return;
                }
                ReleaseRunway(entity, runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                LogEvent(entity, $"is stuck behind {m_HeldBehind[entity]}, which waits for the same runway; clearance passed on, runway", runway);
            }

            /// <summary>Last tick the taxi spacing held this airplane behind a ground airplane waiting for the same runway.</summary>
            private bool IsQueuedBehindWaiting(Entity entity, Entity runway)
            {
                return m_HeldBehind.TryGetValue(entity, out Entity ahead) && m_Flights.TryGetValue(ahead, out Flight other)
                    && other.m_Runway == runway && other.m_Clearance == Clearance.Waiting && !other.m_LastFlying;
            }

            /// <summary>
            /// The soonest an arrival of this size may reach the runway: the arrival spacing (plus wake separation when a
            /// larger airplane is ahead) behind the last cleared arrival; with none, the departure gap while a ground
            /// airplane is using the runway.
            /// </summary>
            private float EarliestArrivalEta(in RunwayState state, SizeClass size, float gap)
            {
                if (state.HasArrivalInSequence(m_Tick))
                {
                    return state.m_LastArrivalEta + SettingsAt(state.m_Airport).m_ArrivalSpacingSeconds + WakeSeconds(state.m_LastArrivalSize, size);
                }
                return state.IsFree(m_Tick) ? 0f : gap;
            }

            private static float WakeSeconds(SizeClass leader, SizeClass follower)
            {
                int steps = SizeRank(leader) - SizeRank(follower);
                return steps > 0 ? steps * kWakeSecondsPerClass : 0f;
            }

            private static int SizeRank(SizeClass size) => size == SizeClass.Undefined ? (int)SizeClass.Medium : (int)size;

            /// <summary>The runway's airport uses line up and wait, and nobody is lined up on it yet.</summary>
            private bool CanLineUp(in RunwayState state)
            {
                return state.m_LineUp == Entity.Null && state.m_Airport != Entity.Null && UsesLineUp(state.m_Airport);
            }

            /// <summary>Line up and wait at this airport: the player's choice for its present class, else the class default (B, C on).</summary>
            private bool UsesLineUp(Entity airport)
            {
                AirspaceClass airspaceClass = m_Airspaces.TryGetValue(airport, out AirportSpace space) ? space.m_Class : AirspaceClass.D;
                bool hasSetting = m_LineUpData.TryGetComponent(airport, out LineUpSetting setting);
                return LineUpSetting.IsEnabled(hasSetting, setting, airspaceClass);
            }

            /// <summary>
            /// "Behind the landing / departing traffic, line up and wait": no arrival is due within the departure gap (else the
            /// lined-up airplane would still be on the runway when it arrives), the only airplane on the runway is either an
            /// arrival rolling out after touchdown or the departure on its takeoff roll, and it is already well past the
            /// point where the waiting airplane would enter, rolling away.
            /// </summary>
            private bool MayLineUpBehindTraffic(in RunwayState state, in Flight candidate, float gap, out Entity traffic)
            {
                traffic = Entity.Null;
                if (!CanLineUp(state) || state.Occupants(m_Tick) != 1 || state.NextArrivalEta(m_Tick) <= gap
                    || math.lengthsq(candidate.m_EntryDirection) < 0.5f)
                {
                    return false;
                }
                if (state.m_Holder != Entity.Null)
                {
                    if (!m_Flights.TryGetValue(state.m_Holder, out Flight departure)
                        || departure.m_OnRunwayTick != m_Tick)
                    {
                        return false;
                    }
                    traffic = state.m_Holder;
                }
                else
                {
                    traffic = state.LandingTraffic(m_Tick);
                    if (traffic == Entity.Null)
                    {
                        return false;
                    }
                }
                float2 direction = candidate.m_EntryDirection;
                float2 offset = m_TransformData[traffic].m_Position.xz - candidate.m_EntryPosition.xz;
                return math.dot(offset, direction) > kLineUpBehindDistance
                    && math.dot(m_MovingData[traffic].m_Velocity.xz, direction) >= 0f;
            }

            /// <summary>
            /// Gives the takeoff (or crossing) clearance to a lined-up airplane as soon as the runway ahead of it is clear,
            /// without waiting on the arrivals: if it hasn't lifted off when the next arrival reaches the decision height, that
            /// arrival goes around. One that hasn't reached the runway yet when an arrival gets within half the departure gap
            /// holds short instead, if it can still stop; one already entering carries on.
            /// </summary>
            private void ReleaseLinedUp()
            {
                NativeArray<Entity> runways = m_Runways.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < runways.Length; i++)
                {
                    RunwayState state = m_Runways[runways[i]];
                    if (state.m_LineUp == Entity.Null || state.m_Holder != Entity.Null || !m_Flights.TryGetValue(state.m_LineUp, out Flight flight))
                    {
                        continue;
                    }
                    float gap = SettingsAt(state.m_Airport).m_DepartureGapSeconds;
                    float nextArrival = state.NextArrivalEta(m_Tick);
                    bool onRunway = flight.m_OnRunwayTick == m_Tick;
                    if (!onRunway && nextArrival <= gap * 0.5f)
                    {
                        RevokeGroundClearance(state.m_LineUp, ref flight, AtcMessageKind.HoldShortForArrival);
                        continue;
                    }
                    if (state.IsClosed(m_Tick) || state.Occupants(m_Tick) != (onRunway ? 1 : 0))
                    {
                        continue;
                    }
                    state.m_Holder = state.m_LineUp;
                    state.m_LineUp = Entity.Null;
                    m_Runways[runways[i]] = state;
                    Clear(state.m_Holder, runways[i], "for takeoff after lining up on", 0f);
                }
                runways.Dispose();
            }

            private void Clear(Entity entity, Entity runway, string what, float ratio)
            {
                Flight flight = m_Flights[entity];
                flight.m_Clearance = Clearance.Cleared;
                flight.m_ClearedFrame = m_Frame;
                flight.m_Told = false;
                flight.m_ToldLanding = false;
                m_Flights[entity] = flight;
                if (flight.m_LastFlying)
                {
                    // Approach clears it for the approach and hands it to the tower; the tower clears it to land once it is on
                    // final (UpdateFlight). After a go-around it is already with the tower, which only gives the landing clearance.
                    if (!flight.m_WentAround)
                    {
                        m_Runways.TryGetValue(runway, out RunwayState state);
                        Say(entity, AtcMessageKind.ClearedApproach, flight, state.m_ArrivalTick == m_Tick ? math.max(1, state.m_ArrivalCount) : 1);
                        if (HasApproachControl(runway))
                        {
                            // Handed to the tower: the pilot checks in on the tower frequency.
                            Say(entity, AtcMessageKind.TowerCheckIn, flight);
                        }
                    }
                }
                else
                {
                    Say(entity, flight.m_Crossing ? AtcMessageKind.CrossRunway : AtcMessageKind.ClearedForTakeoff, flight);
                }
                LogEvent(entity, $"cleared {what} ({Describe(flight)}, ETA {flight.m_LastEta:F0} s, waited {(m_Frame - flight.m_RequestFrame) / 60} s, ratio {ratio:F2}) runway", runway);
            }

        }
    }
}
