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
            // ------------------------------------------------------------ pushback

            /// <summary>
            /// The game turns an airplane leaving a nose-in gate around on the spot. Instead, an airplane that starts moving
            /// with its nose against its path is pushed back: it rolls backwards along the path with the nose locked, stops
            /// where the path turns onto the taxiway, and is swung around until it faces along the path. Only the speed
            /// and the facing direction the game computed are replaced; its path and lane state are untouched.
            /// </summary>
            private void UpdatePushback(Entity entity)
            {
                bool active = m_Pushbacks.TryGetValue(entity, out Pushback pushback);
                if (!active)
                {
                    // Only an airplane standing still at its gate can begin a pushback. Airplanes at a gate keep their
                    // Moving component while boarding, so "just started moving" can't be used to find a departure. Once it
                    // is done there its next stop is somewhere else, so it is remembered as at the gate while it stands.
                    // An arrival that stops on a taxiway and gets a path that turns back (the game sent it on to its next
                    // stop) is no pushback: approved as one, it was pushed along the taxiway and blocked the gate traffic.
                    if (math.lengthsq(m_MovingData[entity].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed)
                    {
                        return;
                    }
                    if (IsHeldAtStop(entity))
                    {
                        // The game still holds it at the stop (loading, or stopped), though it may already have its next
                        // path: asking now got a pushback approval it couldn't act on for a minute or more.
                        if (IsAtOwnGate(entity) || m_AtGate.ContainsKey(entity))
                        {
                            m_AtGate[entity] = m_Tick;
                        }
                        return;
                    }
                    if (m_AtGate.ContainsKey(entity) || IsAtOwnGate(entity))
                    {
                        m_AtGate[entity] = m_Tick;
                    }
                    else
                    {
                        return;
                    }
                    if (m_NavigationLanes[entity].Length == 0)
                    {
                        // Standing still with somewhere to go: an airplane still boarding has no route lanes yet.
                        return;
                    }
                }
                Transform transform = m_TransformData[entity];
                AircraftNavigation navigation = m_NavigationData[entity];
                float2 pathDirection = GetPathDirection(entity, transform.m_Position, navigation);
                if (math.lengthsq(pathDirection) < 0.5f)
                {
                    if (!active)
                    {
                        return;
                    }
                    // The path is being searched again (common right after leaving a gate). Stand still with the nose
                    // locked instead of letting the game spin the airplane around; give up only after a while.
                    if (++pushback.m_UnknownPathTicks > kPushbackUnknownPathTicks)
                    {
                        m_Pushbacks.Remove(entity);
                        if (m_Settings.m_DetailedLog)
                        {
                            Mod.Log.Info($"{entity} pushback cancelled: no path");
                        }
                        return;
                    }
                    pushback.m_LastTick = m_Tick;
                    navigation.m_MaxSpeed = 0f;
                    navigation.m_TargetDirection = new float3(pushback.m_Heading.x, 0f, pushback.m_Heading.y);
                    m_NavigationData[entity] = navigation;
                    m_Pushbacks[entity] = pushback;
                    return;
                }
                if (!active)
                {
                    float2 forward = math.normalizesafe(math.forward(transform.m_Rotation).xz);
                    if (math.dot(forward, pathDirection) > kPushbackStartDot)
                    {
                        return;
                    }
                    m_AtGate.Remove(entity);
                    float2 size = m_PrefabGeometryData.TryGetComponent(m_PrefabRefData[entity].m_Prefab, out ObjectGeometryData geometry)
                        ? geometry.m_Size.xz : new float2(30f, 30f);
                    pushback = new Pushback
                    {
                        m_Start = transform.m_Position,
                        m_Axis = pathDirection,
                        m_Heading = forward,
                        m_Phase = PushbackPhase.AwaitingApproval,
                        m_PhaseFrame = m_Frame,
                        // A short push, about half the airplane's length, then it turns towards the taxiway.
                        m_MaxDistance = math.clamp(size.y * kPushbackLengthFactor, kMinPushbackDistance, kMaxPushbackDistance),
                        m_Radius = 0.5f * math.cmax(size) + kPushbackClearance,
                    };
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} ready for pushback (nose vs path cos {math.dot(forward, pathDirection):F2})");
                    }
                }
                pushback.m_LastTick = m_Tick;
                pushback.m_UnknownPathTicks = 0;

                switch (pushback.m_Phase)
                {
                    case PushbackPhase.AwaitingApproval:
                        // Nothing has moved yet: keep the push laid out from where the airplane stands.
                        pushback.m_Start = transform.m_Position;
                        pushback.m_Axis = pathDirection;
                        if (IsAtClosedAirport(entity))
                        {
                            // Ground stop: no pushbacks while the airport is closed; the approval wait starts over when it reopens.
                            pushback.m_PhaseFrame = m_Frame;
                            navigation.m_MaxSpeed = 0f;
                        }
                        else if (IsPushbackClear(entity, pushback) || (m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame > kPushbackApprovalTimeout)
                        {
                            pushback.m_Phase = PushbackPhase.Pushing;
                            if (m_Settings.m_DetailedLog)
                            {
                                Mod.Log.Info($"{entity} pushback approved after {(m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame:F0} s");
                            }
                            Say(entity, AtcMessageKind.PushbackApproved, Entity.Null, default);
                            pushback.m_PhaseFrame = m_Frame;
                        }
                        else
                        {
                            navigation.m_MaxSpeed = 0f;
                        }
                        break;

                    case PushbackPhase.Pushing:
                        if (m_Frame - pushback.m_PhaseFrame < kResponseFrames)
                        {
                            // The readback first: the push starts a moment after the approval.
                            pushback.m_Start = transform.m_Position;
                            navigation.m_MaxSpeed = 0f;
                            break;
                        }
                        float pushed = math.distance(transform.m_Position.xz, pushback.m_Start.xz);
                        if (pushed < kPushbackSettleDistance)
                        {
                            // The first lanes out of a stand (gate connections) can point a little off the stand's axis;
                            // follow the path for a few metres before watching for the turn.
                            pushback.m_Axis = pathDirection;
                        }
                        else if (pushed > pushback.m_MaxDistance || math.dot(pathDirection, pushback.m_Axis) < kPushbackTurnDot)
                        {
                            pushback.m_Phase = PushbackPhase.Turning;
                            if (m_Settings.m_DetailedLog)
                            {
                                Mod.Log.Info($"{entity} pushback turning after {pushed:F0} m");
                            }
                        }
                        navigation.m_MaxSpeed = pushback.m_Phase == PushbackPhase.Pushing ? math.min(navigation.m_MaxSpeed, kPushbackSpeed) : 0f;
                        break;

                    case PushbackPhase.Turning:
                        // Swing towards the path direction at the tug's turn rate, standing still.
                        float2 heading = pushback.m_Heading;
                        float angle = math.atan2(heading.x * pathDirection.y - heading.y * pathDirection.x, math.dot(heading, pathDirection));
                        float step = math.radians(kPushbackTurnRate) * kTimeStep;
                        if (math.abs(angle) <= step)
                        {
                            pushback.m_Heading = pathDirection;
                            pushback.m_Phase = PushbackPhase.EngineStart;
                            pushback.m_PhaseFrame = m_Frame;
                        }
                        else
                        {
                            math.sincos(math.sign(angle) * step, out float sin, out float cos);
                            pushback.m_Heading = math.normalize(new float2(heading.x * cos - heading.y * sin, heading.x * sin + heading.y * cos));
                        }
                        navigation.m_MaxSpeed = 0f;
                        break;

                    case PushbackPhase.EngineStart:
                        if ((m_Frame - pushback.m_PhaseFrame) * kSecondsPerFrame >= kEngineStartSeconds)
                        {
                            // Engines running, tug gone: hand the airplane back to the game, facing along its path.
                            m_Pushbacks.Remove(entity);
                            if (m_Settings.m_DetailedLog)
                            {
                                Mod.Log.Info($"{entity} pushback done after {math.distance(transform.m_Position.xz, pushback.m_Start.xz):F0} m");
                            }
                            navigation.m_TargetDirection = new float3(pathDirection.x, 0f, pathDirection.y);
                            m_NavigationData[entity] = navigation;
                            return;
                        }
                        navigation.m_MaxSpeed = 0f;
                        break;
                }
                // On the ground the move system faces the target direction when one is set.
                navigation.m_TargetDirection = new float3(pushback.m_Heading.x, 0f, pushback.m_Heading.y);
                m_NavigationData[entity] = navigation;
                m_Pushbacks[entity] = pushback;
            }

            /// <summary>
            /// Ground control's pushback approval: no airplane moving within kPushbackApprovalMargin of the area the push
            /// would cover, and none standing in it.
            /// </summary>
            private bool IsPushbackClear(Entity entity, in Pushback pushback)
            {
                float2 from = pushback.m_Start.xz;
                float2 to = from + pushback.m_Axis * pushback.m_MaxDistance;
                float movingLimit = pushback.m_Radius + kPushbackApprovalMargin;
                for (int i = 0; i < m_Airplanes.Length; i++)
                {
                    Entity other = m_Airplanes[i];
                    if (other == entity || (m_CurrentLaneData[other].m_LaneFlags & AircraftLaneFlags.Flying) != 0)
                    {
                        continue;
                    }
                    float limit = math.lengthsq(m_MovingData[other].m_Velocity) > kPushbackStartSpeed * kPushbackStartSpeed
                        ? movingLimit
                        : pushback.m_Radius;
                    if (DistanceToSegmentSq(m_TransformData[other].m_Position.xz, from, to) < limit * limit)
                    {
                        return false;
                    }
                }
                return true;
            }

            /// <summary>Direction of travel along the path at the airplane's position (the current lane's direction).</summary>
            private float2 GetPathDirection(Entity entity, float3 position, in AircraftNavigation navigation)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float span = currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x;
                if (math.abs(span) > 0.001f && m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    float2 tangent = MathUtils.Tangent(curve.m_Bezier, currentLane.m_CurvePosition.x).xz;
                    return math.normalizesafe(math.select(tangent, -tangent, span < 0f));
                }
                return math.normalizesafe(navigation.m_TargetPosition.xz - position.xz);
            }

            /// <summary>
            /// The ground each pushing airplane covers: from where it is now along the rest of its push (just its own spot
            /// once it is turning). Built once per tick from last tick's pushback state.
            /// </summary>
            private NativeList<PushbackArea> CollectPushbackAreas()
            {
                NativeList<PushbackArea> areas = new NativeList<PushbackArea>(m_Pushbacks.Count(), Allocator.Temp);
                if (m_Pushbacks.IsEmpty)
                {
                    return areas;
                }
                foreach (var pair in m_Pushbacks)
                {
                    if (pair.Value.m_Phase == PushbackPhase.AwaitingApproval)
                    {
                        // Not approved yet: it isn't moving, and traffic must be free to pass so that it can be approved.
                        continue;
                    }
                    if (!m_TransformData.TryGetComponent(pair.Key, out Transform transform))
                    {
                        continue;
                    }
                    Pushback pushback = pair.Value;
                    float2 from = transform.m_Position.xz;
                    float remaining = pushback.m_Phase == PushbackPhase.Pushing
                        ? math.max(0f, pushback.m_MaxDistance - math.distance(from, pushback.m_Start.xz))
                        : 0f;
                    areas.Add(new PushbackArea
                    {
                        m_Entity = pair.Key,
                        m_From = from,
                        m_To = from + pushback.m_Axis * remaining,
                        m_Radius = pushback.m_Radius,
                    });
                }
                return areas;
            }

            /// <summary>
            /// A taxiing airplane stops before its path enters the area of an airplane being pushed back, the way ground
            /// control holds traffic behind a pushback. The pushing airplane has priority, so the two can't block each other.
            /// </summary>
            private void GiveWayToPushbacks(Entity entity, NativeList<PushbackArea> areas)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                AircraftData aircraftData = m_PrefabAircraftData[prefab];
                AircraftNavigation navigation = m_NavigationData[entity];
                float nose = GetNoseLength(prefab);
                float limit = nose + VehicleUtils.GetBrakingDistance(aircraftData, navigation.m_MaxSpeed, kTimeStep) + kTaxiScanMargin;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance = 0f;
                float hit = float.MaxValue;
                Entity pusher = Entity.Null;
                if (!SamplePathForPushbacks(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, limit, areas, ref distance, ref hit, ref pusher))
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length; i++)
                    {
                        if (SamplePathForPushbacks(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, limit, areas, ref distance, ref hit, ref pusher))
                        {
                            break;
                        }
                    }
                }
                if (hit == float.MaxValue)
                {
                    return;
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(aircraftData, math.max(0f, hit - nose), kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed && MayHoldBehind(entity, pusher))
                {
                    m_HeldBehind[entity] = pusher;
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Walks the traversed part of one lane in steps and records the path distance of the first point inside a
            /// pushback area. Returns true when done: an area was hit or the look-ahead limit was passed.
            /// </summary>
            private bool SamplePathForPushbacks(Entity entity, Entity lane, float2 delta, float limit, NativeList<PushbackArea> areas, ref float distance,
                ref float hit, ref Entity pusher)
            {
                if (!m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                float length = curve.m_Length * math.abs(delta.y - delta.x);
                int steps = math.max(1, (int)math.ceil(length / kPushbackSampleStep));
                for (int step = 0; step <= steps; step++)
                {
                    float along = length * step / steps;
                    if (distance + along > limit)
                    {
                        return true;
                    }
                    float2 point = MathUtils.Position(curve.m_Bezier, math.lerp(delta.x, delta.y, (float)step / steps)).xz;
                    for (int i = 0; i < areas.Length; i++)
                    {
                        PushbackArea area = areas[i];
                        if (area.m_Entity != entity && DistanceToSegmentSq(point, area.m_From, area.m_To) < area.m_Radius * area.m_Radius)
                        {
                            hit = distance + along;
                            pusher = area.m_Entity;
                            return true;
                        }
                    }
                }
                distance += length;
                return distance > limit;
            }

            private static float DistanceToSegmentSq(float2 point, float2 a, float2 b)
            {
                float2 ab = b - a;
                float t = math.saturate(math.dot(point - a, ab) / math.max(math.lengthsq(ab), 1e-4f));
                return math.distancesq(point, a + ab * t);
            }

            /// <summary>
            /// The airplane stands at the stop it is bound for (still boarding there): on one of the stand's lanes. The gate's
            /// boarding vehicle can't tell: an arrival is named there while still taxiing in.
            /// </summary>
            private bool IsAtOwnGate(Entity entity)
            {
                if (!m_TargetData.TryGetComponent(entity, out Game.Common.Target target)
                    || !m_RouteLaneData.TryGetComponent(target.m_Target, out Game.Routes.RouteLane stand))
                {
                    return false;
                }
                Entity lane = m_CurrentLaneData[entity].m_Lane;
                return lane == stand.m_StartLane || lane == stand.m_EndLane;
            }

            /// <summary>
            /// The game still holds the airplane at its stop: loading or boarding (the Boarding state of its cargo or passenger
            /// transport), or stopped outright.
            /// </summary>
            private bool IsHeldAtStop(Entity entity)
            {
                return m_StoppedData.HasComponent(entity)
                    || (m_CargoTransportData.TryGetComponent(entity, out Game.Vehicles.CargoTransport cargo) && (cargo.m_State & CargoTransportFlags.Boarding) != 0)
                    || (m_PublicTransportData.TryGetComponent(entity, out Game.Vehicles.PublicTransport passenger) && (passenger.m_State & PublicTransportFlags.Boarding) != 0);
            }

            /// <summary>
            /// Forgets pushbacks of airplanes that stopped being tracked (parked, deleted or flying), and airplanes no
            /// longer standing at their gate.
            /// </summary>
            private void EndPushbackTick()
            {
                RemoveStale(m_AtGate);
                if (m_Pushbacks.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Pushbacks)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_Pushbacks.Remove(stale[i]);
                }
                stale.Dispose();
            }

            private void RemoveStale(NativeParallelHashMap<Entity, uint> lastTicks)
            {
                if (lastTicks.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in lastTicks)
                {
                    if (pair.Value != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    lastTicks.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// Gate hold: an airplane taxiing to a gate that another airplane still uses (boarding there, or standing on the
            /// stand lanes, e.g. pushing back) stops kGateHoldDistance before the stand, clear of the pushback area, so the
            /// one leaving can always get out. Ground control tells it to hold, and to taxi on once the gate is free.
            /// One that is already closer goes on to the stand and waits there, as in the game.
            /// </summary>
            private void HoldForGate(Entity entity)
            {
                bool told = m_LastMessages.TryGetValue(entity, out AtcMessage last) && last.m_Kind == AtcMessageKind.HoldForGate;
                if (!IsGateOccupied(entity, out Game.Routes.RouteLane stand, out Entity occupant))
                {
                    if (told)
                    {
                        Say(entity, AtcMessageKind.TaxiToGate, Entity.Null, default);
                    }
                    return;
                }
                // Only a gate at this airport, on the taxi in (a departure's next stop is somewhere else).
                float distance = DistanceToLane(entity, stand.m_StartLane);
                if (distance < 0f || distance > kGateLookAhead)
                {
                    return;
                }
                // It waits for the airplane on its gate (deadlock detection follows these).
                m_GateHolds[entity] = occupant;
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float stopDistance = distance - GetNoseLength(prefab) - kGateHoldDistance;
                if (stopDistance <= 0f || IsReleasedFrom(entity, occupant))
                {
                    return;
                }
                AircraftNavigation navigation = m_NavigationData[entity];
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], stopDistance, kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
                if (!told)
                {
                    Say(entity, AtcMessageKind.HoldForGate, Entity.Null, default);
                }
            }

            /// <summary>The airplane's next stop is a gate whose stand its route reaches within the look-ahead (taxiing in here).</summary>
            private bool IsTaxiingToGate(Entity entity)
            {
                if (!m_TargetData.TryGetComponent(entity, out Game.Common.Target target)
                    || !m_RouteLaneData.TryGetComponent(target.m_Target, out Game.Routes.RouteLane stand))
                {
                    return false;
                }
                float distance = DistanceToLane(entity, stand.m_StartLane);
                return distance >= 0f && distance <= kGateLookAhead;
            }

            /// <summary>The airplane's next stop is a gate that another airplane is boarding at or still standing on.</summary>
            private bool IsGateOccupied(Entity entity, out Game.Routes.RouteLane stand, out Entity occupant)
            {
                stand = default;
                occupant = Entity.Null;
                if (!m_TargetData.TryGetComponent(entity, out Game.Common.Target target)
                    || !m_RouteLaneData.TryGetComponent(target.m_Target, out stand)
                    || !m_ConnectedData.TryGetComponent(target.m_Target, out Game.Routes.Connected connected)
                    || !m_BoardingVehicleData.TryGetComponent(connected.m_Connected, out Game.Routes.BoardingVehicle boarding))
                {
                    return false;
                }
                if (boarding.m_Vehicle != Entity.Null && boarding.m_Vehicle != entity)
                {
                    occupant = boarding.m_Vehicle;
                    return true;
                }
                return HasOtherObject(stand.m_StartLane, entity, out occupant)
                    || (stand.m_EndLane != stand.m_StartLane && HasOtherObject(stand.m_EndLane, entity, out occupant));
            }

            private bool HasOtherObject(Entity lane, Entity entity, out Entity other)
            {
                other = Entity.Null;
                if (!m_LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> laneObjects))
                {
                    return false;
                }
                for (int i = 0; i < laneObjects.Length; i++)
                {
                    if (laneObjects[i].m_LaneObject != entity)
                    {
                        other = laneObjects[i].m_LaneObject;
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Path distance from the airplane to the start of <paramref name="target"/>, or -1 if its route doesn't reach it.</summary>
            private float DistanceToLane(Entity entity, Entity target)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if (currentLane.m_Lane == target)
                {
                    return 0f;
                }
                float distance = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve current)
                    ? current.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x)
                    : 0f;
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AircraftNavigationLane lane = navigationLanes[i];
                    if (lane.m_Lane == target)
                    {
                        return distance;
                    }
                    if (m_CurveData.TryGetComponent(lane.m_Lane, out Curve curve))
                    {
                        distance += curve.m_Length * math.abs(lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int start = m_PathOwnerData[entity].m_ElementIndex;
                int end = math.min(path.Length, start + kMaxPathScan);
                for (int i = start; i < end; i++)
                {
                    PathElement element = path[i];
                    if (element.m_Target == target)
                    {
                        return distance;
                    }
                    if (m_CurveData.TryGetComponent(element.m_Target, out Curve curve))
                    {
                        distance += curve.m_Length * math.abs(element.m_TargetDelta.y - element.m_TargetDelta.x);
                    }
                }
                return -1f;
            }

            /// <summary>
            /// An airplane that went around is still on the runway lane in the game's state while it flies the pattern or holds,
            /// and the game then stops a departure (or a landed airplane) behind it as if it stood on the runway. An airborne
            /// airplane never blocks a ground one: the game's own IgnoreBlocker flag lets the next navigation update skip it,
            /// and as that flag only lasts one update, a blocked update keeps the speed trend of the last free one.
            /// </summary>
            private void PassAirborneBlocker(Entity entity)
            {
                AircraftNavigation navigation = m_NavigationData[entity];
                float speed = math.length(m_MovingData[entity].m_Velocity);
                // Airborne blockers are always passed; any blocker when the airplane was released from a deadlock.
                if (!m_BlockerData.TryGetComponent(entity, out Blocker blocker) || blocker.m_Blocker == Entity.Null
                    || !m_CurrentLaneData.TryGetComponent(blocker.m_Blocker, out AircraftCurrentLane blockerLane)
                    || ((blockerLane.m_LaneFlags & AircraftLaneFlags.Flying) == 0 && !IsReleasedFrom(entity, blocker.m_Blocker)))
                {
                    m_FreeSpeeds[entity] = new FreeSpeed { m_MaxSpeed = navigation.m_MaxSpeed, m_Speed = speed, m_Tick = m_Tick };
                    return;
                }
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                currentLane.m_LaneFlags |= AircraftLaneFlags.IgnoreBlocker;
                m_CurrentLaneData[entity] = currentLane;
                if (m_FreeSpeeds.TryGetValue(entity, out FreeSpeed free) && m_Tick - free.m_Tick <= 2)
                {
                    Bounds1 range = VehicleUtils.CalculateSpeedRange(m_PrefabAircraftData[m_PrefabRefData[entity].m_Prefab], speed, kTimeStep);
                    float trend = math.clamp(free.m_MaxSpeed + (free.m_MaxSpeed - free.m_Speed), range.min, range.max);
                    if (trend > navigation.m_MaxSpeed)
                    {
                        navigation.m_MaxSpeed = trend;
                        m_NavigationData[entity] = navigation;
                    }
                }
            }

            /// <summary>
            /// Deadlock detection: each ground airplane waits for at most one other: the airplane the mod's taxi hold keeps it
            /// behind, the airplane on its gate, or else the game's own blocker. Following those links back to the start is a
            /// cycle that won't clear by itself, so everyone on it is released at once for kDeadlockReleaseFrames and drives
            /// through the others on that cycle (only them), overlapping if it must. Runway hold-short is never released.
            /// </summary>
            private void DetectDeadlocks(NativeArray<FlightScan> scans)
            {
                NativeParallelHashMap<Entity, Entity> waitsFor = new NativeParallelHashMap<Entity, Entity>(math.max(8, scans.Length), Allocator.Temp);
                for (int i = 0; i < scans.Length; i++)
                {
                    if (scans[i].m_Flying)
                    {
                        continue;
                    }
                    Entity entity = scans[i].m_Entity;
                    Entity next = m_HeldBehind.TryGetValue(entity, out Entity ahead) ? ahead
                        : m_GateHolds.TryGetValue(entity, out Entity occupant) ? occupant
                        : m_BlockerData.TryGetComponent(entity, out Blocker blocker) && m_CurrentLaneData.HasComponent(blocker.m_Blocker) ? blocker.m_Blocker
                        : Entity.Null;
                    if (next != Entity.Null)
                    {
                        waitsFor.TryAdd(entity, next);
                    }
                }
                foreach (var pair in waitsFor)
                {
                    Entity start = pair.Key;
                    if (IsReleased(start))
                    {
                        continue;
                    }
                    Entity current = pair.Value;
                    for (int step = 0; step < kMaxDeadlockChain && current != start && waitsFor.TryGetValue(current, out Entity next); step++)
                    {
                        current = next;
                    }
                    if (current != start)
                    {
                        continue;
                    }
                    // Everyone on this cycle passes the others on it (only them: an airplane ahead in a queue still holds it).
                    // The cycle is named by its first airplane, which is not released yet, so no other active cycle shares it.
                    uint cycle = (uint)start.Index;
                    System.Text.StringBuilder chain = new System.Text.StringBuilder();
                    Entity node = start;
                    do
                    {
                        m_Released[node] = new DeadlockRelease { m_Until = m_Frame + kDeadlockReleaseFrames, m_Cycle = cycle };
                        chain.Append(node).Append(" -> ");
                        node = waitsFor[node];
                    }
                    while (node != start);
                    Mod.Log.Info($"deadlock: {chain}{start}; passing through");
                }
                waitsFor.Dispose();
            }

            /// <summary>Released from a deadlock: no taxi holds and no blockers until the release runs out.</summary>
            private bool IsReleased(Entity entity)
            {
                return !m_Released.IsEmpty && m_Released.TryGetValue(entity, out DeadlockRelease release) && (int)(release.m_Until - m_Frame) > 0;
            }

            /// <summary>Released from a deadlock together with <paramref name="other"/>: it may drive through that airplane.</summary>
            private bool IsReleasedFrom(Entity entity, Entity other)
            {
                return !m_Released.IsEmpty && m_Released.TryGetValue(entity, out DeadlockRelease release) && (int)(release.m_Until - m_Frame) > 0
                    && m_Released.TryGetValue(other, out DeadlockRelease otherRelease) && otherRelease.m_Cycle == release.m_Cycle;
            }

            private void EndReleaseTick()
            {
                if (m_Released.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> expired = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Released)
                {
                    if ((int)(pair.Value.m_Until - m_Frame) <= 0)
                    {
                        expired.Add(pair.Key);
                    }
                }
                for (int i = 0; i < expired.Length; i++)
                {
                    m_Released.Remove(expired[i]);
                }
                expired.Dispose();
            }

            private void EndFreeSpeedTick()
            {
                if (m_FreeSpeeds.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_FreeSpeeds)
                {
                    if (m_Tick - pair.Value.m_Tick > 2)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_FreeSpeeds.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// The mod's own taxi holds (spacing, giving way to a pushback) assume the airplane ahead moves on. One that has
            /// held an airplane behind the same other airplane for kTaxiHoldTimeout is dropped, and the game's own traffic
            /// logic (which lets stuck airplanes pass each other) takes over, unless the one ahead is waiting in line for a
            /// reason that will pass (<see cref="IsWaitingInLine"/>).
            /// </summary>
            private bool MayHoldBehind(Entity entity, Entity ahead)
            {
                if (IsReleasedFrom(entity, ahead))
                {
                    return false;
                }
                if (m_TaxiHolds.TryGetValue(entity, out TaxiHold hold) && hold.m_Ahead == ahead)
                {
                    hold.m_LastTick = m_Tick;
                    if (!hold.m_Released && m_Frame - hold.m_Since > kTaxiHoldTimeout && !IsWaitingInLine(ahead))
                    {
                        hold.m_Released = true;
                        Mod.Log.Info($"{entity} held behind {ahead} for {(m_Frame - hold.m_Since) / 60} s; taxi hold released (the game takes over)");
                    }
                    m_TaxiHolds[entity] = hold;
                    return !hold.m_Released;
                }
                m_TaxiHolds[entity] = new TaxiHold { m_Ahead = ahead, m_Since = m_Frame, m_LastTick = m_Tick };
                return true;
            }

            /// <summary>
            /// The airplane ahead stands for a reason that will pass: it waits for its runway (a ground request), is itself held
            /// in the queue behind another one, or is being pushed back. Holding behind it is never given up, however long the
            /// queue (giving up let airplanes in a long departure queue roll into each other); a wait-for cycle is still found
            /// and released by DetectDeadlocks.
            /// </summary>
            private bool IsWaitingInLine(Entity ahead)
            {
                if (m_Flights.TryGetValue(ahead, out Flight flight) && !flight.m_LastFlying)
                {
                    return true;
                }
                return m_Pushbacks.ContainsKey(ahead) || (m_TaxiHolds.TryGetValue(ahead, out TaxiHold aheadHold) && !aheadHold.m_Released);
            }

            /// <summary>Forgets taxi holds that didn't apply this tick (the airplane moved on, or the one ahead did).</summary>
            private void EndTaxiHoldTick()
            {
                if (m_TaxiHolds.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_TaxiHolds)
                {
                    if (pair.Value.m_LastTick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_TaxiHolds.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>
            /// On taxiways the game lets an airplane roll up almost to the tail of the one ahead. This keeps the configured
            /// gap between its nose and the nearest tail ahead on its path, the way real taxiing aircraft keep their distance.
            /// </summary>
            private void KeepTaxiSpacing(Entity entity)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                AircraftData aircraftData = m_PrefabAircraftData[prefab];
                AircraftNavigation navigation = m_NavigationData[entity];
                float nose = GetNoseLength(prefab);
                float spacing = m_Settings.m_TaxiSpacing;
                // Nothing beyond the point where the airplane could still stop with the gap to spare matters this tick.
                float limit = nose + spacing + VehicleUtils.GetBrakingDistance(aircraftData, navigation.m_MaxSpeed, kTimeStep) + kTaxiScanMargin;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance = 0f;
                float nearestTail = float.MaxValue;
                Entity ahead = Entity.Null;
                FindTailAhead(entity, currentLane.m_Lane, currentLane.m_CurvePosition.xz, ref distance, ref nearestTail, ref ahead);
                if (nearestTail == float.MaxValue && distance < limit)
                {
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length && distance < limit && nearestTail == float.MaxValue; i++)
                    {
                        FindTailAhead(entity, navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, ref distance, ref nearestTail, ref ahead);
                    }
                }
                if (nearestTail == float.MaxValue)
                {
                    return;
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(aircraftData, math.max(0f, nearestTail - nose - spacing), kTimeStep);
                if (maxSpeed < navigation.m_MaxSpeed && MayHoldBehind(entity, ahead))
                {
                    m_HeldBehind[entity] = ahead;
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Path distance to the tail of the nearest other object on the traversed part <paramref name="delta"/> of a lane,
            /// measured from the airplane's own position; then adds the lane part's length to <paramref name="distance"/>.
            /// </summary>
            private void FindTailAhead(Entity entity, Entity lane, float2 delta, ref float distance, ref float nearestTail, ref Entity ahead)
            {
                if (!m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return;
                }
                float span = delta.y - delta.x;
                if (m_LaneObjects.TryGetBuffer(lane, out DynamicBuffer<LaneObject> laneObjects))
                {
                    float direction = math.select(-1f, 1f, span >= 0f);
                    for (int i = 0; i < laneObjects.Length; i++)
                    {
                        LaneObject laneObject = laneObjects[i];
                        float along = (laneObject.m_CurvePosition.x - delta.x) * direction;
                        if (laneObject.m_LaneObject == entity || along <= 0f || along > math.abs(span))
                        {
                            continue;
                        }
                        // An airplane in the air (an arrival on approach is listed on the runway's lanes) is the tower's
                        // business, not taxi spacing: holding behind it kept a cleared departure from ever moving.
                        if (m_CurrentLaneData.TryGetComponent(laneObject.m_LaneObject, out AircraftCurrentLane otherLane)
                            && (otherLane.m_LaneFlags & AircraftLaneFlags.Flying) != 0)
                        {
                            continue;
                        }
                        float tail = 0f;
                        if (m_PrefabRefData.TryGetComponent(laneObject.m_LaneObject, out PrefabRef other)
                            && m_PrefabGeometryData.TryGetComponent(other.m_Prefab, out ObjectGeometryData geometry))
                        {
                            tail = math.max(0f, -geometry.m_Bounds.min.z);
                        }
                        float candidate = distance + along * curve.m_Length - tail;
                        if (candidate < nearestTail)
                        {
                            nearestTail = candidate;
                            ahead = laneObject.m_LaneObject;
                        }
                    }
                }
                FindCrossingAhead(entity, lane, curve, delta, distance, ref nearestTail, ref ahead);
                distance += curve.m_Length * math.abs(span);
            }

            /// <summary>
            /// Taxiways that cross or merge into this lane part (the game's lane overlaps): an airplane standing in or moving
            /// through such a junction ahead counts as an airplane ahead at the junction, so the airplane stops the taxi gap
            /// before it. Without this only airplanes on the airplane's own lanes were seen, and one coming to a junction that
            /// another blocked (e.g. a departure holding short with its tail across it) rolled up to a few metres, the game's
            /// own crossing gap. Runway lanes are left to the tower.
            /// </summary>
            private void FindCrossingAhead(Entity entity, Entity lane, in Curve curve, float2 delta, float distance, ref float nearestTail, ref Entity ahead)
            {
                if (!m_LaneOverlaps.TryGetBuffer(lane, out DynamicBuffer<LaneOverlap> overlaps))
                {
                    return;
                }
                bool forward = delta.y >= delta.x;
                for (int i = 0; i < overlaps.Length; i++)
                {
                    LaneOverlap overlap = overlaps[i];
                    float start = overlap.m_ThisStart * (1f / 255f);
                    float end = overlap.m_ThisEnd * (1f / 255f);
                    // Only where this lane part still lies ahead overlaps the other lane.
                    if (forward ? end < delta.x || start > delta.y : start > delta.x || end < delta.y)
                    {
                        continue;
                    }
                    float entry = forward ? math.max(start, delta.x) : math.min(end, delta.x);
                    float candidate = distance + math.abs(entry - delta.x) * curve.m_Length;
                    if (candidate >= nearestTail || RunwayOfLane(overlap.m_Other) != Entity.Null
                        || !m_LaneObjects.TryGetBuffer(overlap.m_Other, out DynamicBuffer<LaneObject> others))
                    {
                        continue;
                    }
                    float2 junction = MathUtils.Position(curve.m_Bezier, entry).xz;
                    for (int j = 0; j < others.Length; j++)
                    {
                        Entity other = others[j].m_LaneObject;
                        if (other == entity || !m_TransformData.TryGetComponent(other, out Transform transform)
                            || (m_CurrentLaneData.TryGetComponent(other, out AircraftCurrentLane otherLane) && (otherLane.m_LaneFlags & AircraftLaneFlags.Flying) != 0)
                            || !m_PrefabRefData.TryGetComponent(other, out PrefabRef prefabRef)
                            || !m_PrefabGeometryData.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry))
                        {
                            continue;
                        }
                        float2 forwardOther = math.normalizesafe(math.forward(transform.m_Rotation).xz);
                        float2 nose = transform.m_Position.xz + forwardOther * geometry.m_Bounds.max.z;
                        float2 tail = transform.m_Position.xz + forwardOther * geometry.m_Bounds.min.z;
                        float otherDistance = math.sqrt(DistanceToSegmentSq(junction, nose, tail));
                        // Only the one further from the junction gives way (equal: the higher index), so that two airplanes
                        // reaching it together don't both wait: that was a wait-for cycle, released as a deadlock, and the two
                        // drove through each other. One standing across the junction (distance 0) always goes first.
                        if (otherDistance < kCrossingClearance && GivesWay(entity, junction, other, otherDistance))
                        {
                            nearestTail = candidate;
                            ahead = other;
                            break;
                        }
                    }
                }
            }

            /// <summary>The airplane is further from the junction than the other one (by body), so it waits; a tie goes by index.</summary>
            private bool GivesWay(Entity entity, float2 junction, Entity other, float otherDistance)
            {
                Transform transform = m_TransformData[entity];
                ObjectGeometryData geometry = m_PrefabGeometryData.TryGetComponent(m_PrefabRefData[entity].m_Prefab, out ObjectGeometryData g) ? g : default;
                float2 forward = math.normalizesafe(math.forward(transform.m_Rotation).xz);
                float ownDistance = math.sqrt(DistanceToSegmentSq(junction,
                    transform.m_Position.xz + forward * geometry.m_Bounds.max.z, transform.m_Position.xz + forward * geometry.m_Bounds.min.z));
                if (math.abs(ownDistance - otherDistance) < kCrossingTie)
                {
                    return entity.Index > other.Index;
                }
                return ownDistance > otherDistance;
            }

            /// <summary>
            /// Line up and wait: lets the airplane onto the runway and stops it lined up on the first runway lane of its path,
            /// with its tail just inside it. A path that only crosses the runway has no such lane, so the airplane keeps
            /// holding short until its full clearance.
            /// </summary>
            private void HoldAtLineUpPoint(Entity entity, in FlightScan scan)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float tail = m_PrefabGeometryData.TryGetComponent(prefab, out ObjectGeometryData geometry) ? math.max(0f, -geometry.m_Bounds.min.z) : 0f;
                float target = tail + kLineUpInset;

                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float distance;
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Runway) != 0)
                {
                    // Already on the runway lane, which it entered at one of its ends.
                    float length = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve) ? curve.m_Length : 0f;
                    float start = math.select(1f, 0f, currentLane.m_CurvePosition.z >= currentLane.m_CurvePosition.x);
                    distance = target - math.abs(currentLane.m_CurvePosition.x - start) * length;
                }
                else
                {
                    distance = m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve)
                        ? curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x)
                        : 0f;
                    bool found = false;
                    DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                    for (int i = 0; i < navigationLanes.Length; i++)
                    {
                        AircraftNavigationLane lane = navigationLanes[i];
                        if ((lane.m_Flags & AircraftLaneFlags.Runway) != 0)
                        {
                            distance += target;
                            found = true;
                            break;
                        }
                        if (m_CurveData.TryGetComponent(lane.m_Lane, out Curve laneCurve))
                        {
                            distance += laneCurve.m_Length * math.abs(lane.m_CurvePosition.y - lane.m_CurvePosition.x);
                        }
                    }
                    if (!found)
                    {
                        if (!scan.m_PathUnknown && scan.m_Occupied == Entity.Null)
                        {
                            HoldShort(entity, scan);
                        }
                        return;
                    }
                }
                float maxSpeed = VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], math.max(0f, distance), kTimeStep);
                AircraftNavigation navigation = m_NavigationData[entity];
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>Highest speed from which the airplane still stops at its hold-short point, given its distance to the runway.</summary>
            private float GetHoldShortSpeed(Entity entity, float distance)
            {
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float stopDistance = math.max(0f, distance - GetNoseLength(prefab) - m_Settings.m_HoldShortSetback);
                return VehicleUtils.GetMaxBrakingSpeed(m_PrefabAircraftData[prefab], stopDistance, kTimeStep);
            }

            /// <summary>A taxiing airplane can still stop at its hold-short point (with a little slack for rounding).</summary>
            private bool CanHoldShort(Entity entity, in Flight flight)
            {
                return math.length(m_MovingData[entity].m_Velocity) <= GetHoldShortSpeed(entity, flight.m_LastDistance) + kHoldShortSpeedSlack;
            }

            private void StandStill(Entity entity)
            {
                AircraftNavigation navigation = m_NavigationData[entity];
                navigation.m_MaxSpeed = 0f;
                m_NavigationData[entity] = navigation;
            }

            /// <summary>Lowers the speed so the airplane stops with its nose the setback distance before the runway.</summary>
            private void HoldShort(Entity entity, in FlightScan scan)
            {
                float maxSpeed = GetHoldShortSpeed(entity, scan.m_Distance);
                AircraftNavigation navigation = m_NavigationData[entity];
                if (maxSpeed < navigation.m_MaxSpeed)
                {
                    navigation.m_MaxSpeed = maxSpeed;
                    m_NavigationData[entity] = navigation;
                }
            }

            /// <summary>
            /// Holds at present position: the fix is where the airplane is now, on its own path, and the inbound course
            /// follows that path. The game's own steering target and direction are kept, so the airplane can be handed
            /// back in exactly the state the game left it in. The game's lane state is never touched.
            /// </summary>
            private void StartHolding(Entity entity, ref Flight flight)
            {
                Transform transform = m_TransformData[entity];
                AircraftNavigation navigation = m_NavigationData[entity];
                float2 inbound = math.normalizesafe(navigation.m_TargetPosition.xz - transform.m_Position.xz,
                    math.normalizesafe(m_MovingData[entity].m_Velocity.xz, new float2(0f, 1f)));
                AtcSettings settings = SettingsForRunway(flight.m_Runway);
                if (m_Settings.m_StandardArrival && settings.m_GlidePath && flight.m_Arrival && math.lengthsq(flight.m_EntryDirection) > 0.5f
                    && (m_CurrentLaneData[entity].m_LaneFlags & AircraftLaneFlags.Airway) != 0)
                {
                    // STAR (1.2): it holds at the IAF of its sector (the IF from straight in), the next leg of the approach as the
                    // inbound course. Once cleared it flies the procedure on from there (FlyApproachProcedure).
                    int side = ChooseApproachSide(flight, settings.m_GlideTan, transform.m_Position);
                    GetApproachFixes(flight, settings.m_GlideTan, side == 0 ? 1 : side, out float3 iaf, out float3 intermediate, out _);
                    float2 fix = side == 0 ? intermediate.xz : iaf.xz;
                    float2 starInbound = side == 0 ? flight.m_EntryDirection : math.normalizesafe(intermediate.xz - iaf.xz);
                    StartHolding(entity, ref flight, navigation.m_TargetPosition, navigation.m_TargetDirection, starInbound, true, true, fix);
                    flight.m_StarHold = true;
                    flight.m_ProcSide = (sbyte)side;
                    m_Flights[entity] = flight;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} STAR: holding at the {(side == 0 ? "IF (straight in)" : side > 0 ? "right IAF" : "left IAF")} for runway {flight.m_Runway}, level {flight.m_StackLevel}");
                    }
                    return;
                }
                StartHolding(entity, ref flight, navigation.m_TargetPosition, navigation.m_TargetDirection, inbound, true);
            }

            /// <summary>
            /// Holds at present position, then (once cleared) flies to <paramref name="resumeTarget"/> lined up with
            /// <paramref name="resumeDirection"/> and hands the airplane back there.
            /// Arrival holds are <paramref name="stacked"/> 1,000 ft apart per runway. A hold at the end of the downwind
            /// isn't: it stays at pattern altitude near the airport, apart from the arrival holds (stacking it on top of
            /// them sent the airplane climbing thousands of feet).
            /// </summary>
            private void StartHolding(Entity entity, ref Flight flight, float3 resumeTarget, float3 resumeDirection, float2 inbound, bool stacked,
                bool atFix = false, float2 fixXZ = default)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                flight.m_ResumeTarget = resumeTarget;
                flight.m_ResumeDirection = resumeDirection;
                flight.m_StackLevel = stacked ? TakeStackLevel(flight.m_Runway) : kNoStackLevel;
                float3 fix = m_TransformData[entity].m_Position;
                if (atFix)
                {
                    // A published holding fix (the STAR's IAF) instead of the present position.
                    fix.xz = fixXZ;
                }
                if (stacked)
                {
                    // Holds of different airports are separated by altitude: each class has its own band above its runway, B highest
                    // (phase 4-3); without airspace data, the stack starts where the airplane is.
                    fix.y = m_Runways.TryGetValue(flight.m_Runway, out RunwayState runwayState) && m_Airspaces.TryGetValue(runwayState.m_Airport, out AirportSpace space)
                        ? flight.m_EntryPosition.y + space.HoldBase + flight.m_StackLevel * kStackSpacing
                        : fix.y + flight.m_StackLevel * kStackSpacing;
                    if (!flight.m_Told)
                    {
                        // Not when the reason was just given (closure): that transmission already said to hold.
                        flight.m_Told = true;
                        Say(entity, AtcMessageKind.HoldAtPresentPosition, flight, (int)math.round((fix.y - flight.m_EntryPosition.y) / (100f * 0.3048f)) * 100);
                    }
                }
                flight.m_Pattern = new HoldingPattern(fix, inbound, m_Settings.m_HoldingLegLength, GetTurnRadius(airplaneData));
                flight.m_Holding = true;
                flight.m_FastForwardTried = false;
                flight.m_ProcStage = 0;
                flight.m_StarHold = false;
                m_Flights[entity] = flight;
            }

            /// <summary>Turn radius at holding speed: the move system turns with radius speed / m_FlyingTurning, plus a margin.</summary>
            private static float GetTurnRadius(AirplaneData airplaneData)
            {
                return GetHoldingSpeed(airplaneData) / math.max(airplaneData.m_FlyingTurning, 0.001f) * 1.1f;
            }

        }
    }
}
