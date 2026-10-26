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
            // ------------------------------------------------------------ go-around and traffic pattern

            /// <summary>
            /// Breaks off the approach: the runway is released and the airplane asks again, keeping its place in time.
            /// It climbs straight ahead and flies the traffic pattern built from its own runway: upwind past the runway
            /// end, crosswind, downwind abeam the runway to the base turn. Left-hand by default; on the other side when a
            /// parallel runway lies on the pattern side.
            /// </summary>
            private void GoAround(Entity entity, ref Flight flight, string reason, bool pilotInitiated = false, int spokenReason = 0)
            {
                // Close to the threshold the game has already moved the approach on from "approaching" to "landing", and in
                // that state it treats being below the steering target as touchdown and drops the flying state, so the
                // climbing airplane would turn into a ground vehicle in the air. Put the approach state it had back.
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if (!LeaveRunwayLane(entity, ref currentLane)
                    && (currentLane.m_LaneFlags & AircraftLaneFlags.Landing) != 0 && (currentLane.m_LaneFlags & AircraftLaneFlags.Approaching) == 0)
                {
                    currentLane.m_LaneFlags |= AircraftLaneFlags.Approaching;
                    m_CurrentLaneData[entity] = currentLane;
                }

                ReleaseRunway(entity, flight.m_Runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_SeenOnRunway = false;
                flight.m_TouchedDown = false;
                flight.m_WentAround = true;
                flight.m_ToldLanding = false;
                flight.m_WeatherChecked = false;

                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                AtcSettings settings = SettingsForRunway(flight.m_Runway);
                float radius = GetTurnRadius(airplaneData);
                float3 threshold = flight.m_EntryPosition;
                float2 landing = flight.m_EntryDirection;
                float2 side = IsRightHandPattern(entity, flight.m_Runway, landing) ? MathUtils.Right(landing) : MathUtils.Left(landing);
                float width = math.max(settings.m_PatternWidth, radius * 2.2f);

                // Upwind ends past the runway end (and past the airplane), leaving room for the crosswind turn.
                float upwind = math.dot(m_TransformData[entity].m_Position.xz - threshold.xz, landing);
                if (m_RunwayGeometry.TryGetValue(flight.m_Runway, out RunwayGeometry geometry))
                {
                    float2 a = geometry.m_Origin + geometry.m_Direction * geometry.m_Min;
                    float2 b = geometry.m_Origin + geometry.m_Direction * geometry.m_Max;
                    upwind = math.max(upwind, math.max(math.dot(a - threshold.xz, landing), math.dot(b - threshold.xz, landing)));
                }
                upwind += math.max(kUpwindExtension, radius);
                float final = settings.m_GlidePath ? GetFinalFixDistance(radius, settings.m_GlideTan) : math.max(settings.m_FinalLength, radius * 2f);
                float altitude = threshold.y + kPatternHeight;

                flight.m_UpwindEnd = new float3(threshold.x + landing.x * upwind, altitude, threshold.z + landing.y * upwind);
                flight.m_CrosswindEnd = flight.m_UpwindEnd + new float3(side.x, 0f, side.y) * width;
                flight.m_DownwindEnd = new float3(threshold.x - landing.x * final + side.x * width, altitude, threshold.z - landing.y * final + side.y * width);
                flight.m_CircuitLeg = CircuitLeg.Upwind;
                flight.m_InCircuit = true;
                m_Flights[entity] = flight;
                LogEvent(entity, $"went around ({reason}), {(math.dot(side, MathUtils.Right(landing)) > 0f ? "right" : "left")}-hand pattern for runway", flight.m_Runway);
                // Value: bit 0 right-hand traffic; for the pilot's own go-around, the reason it gives (GoAroundReason + 1) above that.
                int value = math.dot(side, MathUtils.Right(landing)) > 0f ? 1 : 0;
                Say(entity, pilotInitiated ? AtcMessageKind.GoingAround : AtcMessageKind.GoAround, flight,
                    value | (spokenReason << 1), ClosedBy(flight.m_Runway));
            }

            /// <summary>Flies the pattern legs; at the end of downwind the base turn and final are flown by the hold/return logic.</summary>
            private void FlyCircuit(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                float radius = GetTurnRadius(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab]);
                // Turn onto the next leg a little before each corner, as a fly-by waypoint.
                float lead = radius * kCircuitLeadFactor;
                bool cleared = flight.m_Clearance == Clearance.Cleared && m_Frame - flight.m_ClearedFrame >= kResponseFrames;
                if (flight.m_CircuitLeg == CircuitLeg.ExtendedDownwind && cleared)
                {
                    // Cleared on the extension: turn base from here.
                    EndCircuit(entity, ref flight);
                    return;
                }
                float3 waypoint = flight.m_CircuitLeg == CircuitLeg.Upwind ? flight.m_UpwindEnd
                    : flight.m_CircuitLeg == CircuitLeg.Crosswind ? flight.m_CrosswindEnd
                    : flight.m_CircuitLeg == CircuitLeg.Downwind ? flight.m_DownwindEnd
                    : GetDownwindExtensionEnd(flight);
                if (math.distancesq(position.xz, waypoint.xz) < lead * lead)
                {
                    if (flight.m_CircuitLeg < CircuitLeg.Downwind)
                    {
                        flight.m_CircuitLeg++;
                        m_Flights[entity] = flight;
                        FlyCircuit(entity, ref flight);
                        return;
                    }
                    if (flight.m_CircuitLeg == CircuitLeg.Downwind && !cleared)
                    {
                        // Not cleared yet: keep flying downwind instead of turning base.
                        flight.m_CircuitLeg = CircuitLeg.ExtendedDownwind;
                        m_Flights[entity] = flight;
                        Say(entity, AtcMessageKind.ExtendDownwind, flight);
                        FlyCircuit(entity, ref flight);
                        return;
                    }
                    // Cleared at the end of downwind, or the extension is used up (then the tower holds it).
                    EndCircuit(entity, ref flight);
                    return;
                }
                Steer(entity, waypoint, default);
                m_Flights[entity] = flight;
            }

            /// <summary>Where the extended downwind ends: kDownwindExtension beyond the normal end, at pattern altitude.</summary>
            private static float3 GetDownwindExtensionEnd(in Flight flight)
            {
                float3 direction = flight.m_DownwindEnd - flight.m_CrosswindEnd;
                direction.y = 0f;
                return flight.m_DownwindEnd + math.normalizesafe(direction) * kDownwindExtension;
            }

            /// <summary>
            /// End of downwind (or of its extension): the final approach starts the final length out on the extended centerline,
            /// at the glide path height there, lined up with the runway. A cleared airplane turns base and joins it at once; one
            /// that still isn't at the end of the extension is told to hold, and holds right there until it is (the same
            /// hold-and-return used for arrivals).
            /// </summary>
            private void EndCircuit(Entity entity, ref Flight flight)
            {
                AtcSettings settings = SettingsForRunway(flight.m_Runway);
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float3 threshold = flight.m_EntryPosition;
                float2 landing = flight.m_EntryDirection;
                // With the glide path option the final starts at the final approach fix, as for every other arrival.
                float final = settings.m_GlidePath ? GetFinalFixDistance(GetTurnRadius(airplaneData), settings.m_GlideTan)
                    : math.max(settings.m_FinalLength, GetTurnRadius(airplaneData) * 2f);
                float3 finalStart = new float3(threshold.x - landing.x * final, 0f, threshold.z - landing.y * final);
                finalStart.y = math.min(flight.m_DownwindEnd.y,
                    threshold.y + final * (settings.m_GlidePath ? settings.m_GlideTan : math.tan(airplaneData.m_ClimbAngle)));

                flight.m_InCircuit = false;
                float2 downwind = math.normalizesafe(flight.m_DownwindEnd.xz - flight.m_CrosswindEnd.xz, -landing);
                StartHolding(entity, ref flight, finalStart, new float3(landing.x, 0f, landing.y), downwind, false);
                if (flight.m_Clearance == Clearance.Cleared)
                {
                    ReturnToPath(entity, ref flight);
                }
                else
                {
                    Say(entity, AtcMessageKind.HoldInPattern, flight);
                    Hold(entity, ref flight);
                }
            }

            /// <summary>
            /// The runway end's pattern direction, set in the airport info panel and stored on the runway's edges: read from
            /// the edge the airplane is over (or the runway's key edge) for the end it lands on.
            /// </summary>
            private bool IsRightHandPattern(Entity entity, Entity runway, float2 landing)
            {
                Entity edge = m_OwnerData.TryGetComponent(m_CurrentLaneData[entity].m_Lane, out Owner owner) ? owner.m_Owner : Entity.Null;
                if (!m_RunwayPatternData.HasComponent(edge))
                {
                    edge = runway;
                }
                if (m_RunwayPatternData.TryGetComponent(edge, out RunwayPattern pattern) && m_CurveData.TryGetComponent(edge, out Curve curve)
                    && pattern.TryGetRightHand(curve.m_Bezier.d.xz - curve.m_Bezier.a.xz, landing, out bool rightHand))
                {
                    return rightHand;
                }
                // Not set in the panel: right-hand when a parallel runway lies to the left, so the pattern stays outside; with no
                // parallel runway, away from a neighbouring airport on the left (phase 4-3).
                if (HasParallelRunwayOnSide(runway, landing, MathUtils.Left(landing)))
                {
                    return true;
                }
                if (HasParallelRunwayOnSide(runway, landing, MathUtils.Right(landing)))
                {
                    return false;
                }
                return m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry geometry) && m_Runways.TryGetValue(runway, out RunwayState state)
                    && AirspaceRules.IsNeighbourOnLeft(m_Airspaces, state.m_Airport, geometry.Middle, landing);
            }

            /// <summary>Whether another runway runs parallel within the pattern width on the given side of this one.</summary>
            private bool HasParallelRunwayOnSide(Entity runway, float2 landing, float2 side)
            {
                if (!m_RunwayGeometry.TryGetValue(runway, out RunwayGeometry ours))
                {
                    return false;
                }
                foreach (var pair in m_RunwayGeometry)
                {
                    if (pair.Key != runway && RunwayGeometry.IsParallelOnSide(ours, pair.Value, landing, side, SettingsForRunway(runway).m_PatternWidth))
                    {
                        return true;
                    }
                }
                return false;
            }

            /// <summary>Flies the holding pattern instead of the path the game planned.</summary>
            private void Hold(Entity entity, ref Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                Steer(entity, flight.m_Pattern.GetSteeringTarget(position, flight.m_Pattern.m_TurnRadius + GetSpeed(entity) * 2f), default);
            }

            /// <summary>
            /// After clearance, flies back to the point where the game's path was frozen and lines up with it, then hands the
            /// airplane back with the game's own target and direction. From there the game's approach logic (descent and
            /// slowing down) runs exactly as for an airplane that never held.
            /// </summary>
            private void ReturnToPath(Entity entity, ref Flight flight)
            {
                // With the instrument approach, an arrival leaving an airway hold doesn't fly back to where it began holding: it
                // flies its approach procedure from where it is (FlyApproachProcedure, which also skips the rest of its airway);
                // from a STAR hold, on from the hold's fix.
                if (flight.m_Arrival && flight.m_StackLevel != kNoStackLevel && SettingsForRunway(flight.m_Runway).m_GlidePath
                    && (m_CurrentLaneData[entity].m_LaneFlags & AircraftLaneFlags.Airway) != 0)
                {
                    FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                    flight.m_Holding = false;
                    flight.m_ProcStage = !flight.m_StarHold ? kStageNone : flight.m_ProcSide == 0 ? kStageToIf : kStageToIaf;
                    flight.m_StarHold = false;
                    m_Flights[entity] = flight;
                    return;
                }
                float3 position = m_TransformData[entity].m_Position;
                float3 resume = flight.m_ResumeTarget;
                float radius = flight.m_Pattern.m_TurnRadius;
                float2 toResume = resume.xz - position.xz;
                float distance = math.length(toResume);
                float2 heading = math.normalizesafe(m_MovingData[entity].m_Velocity.xz);

                // Hand back once flying towards the point from far enough out that it can't end up inside the turn circle.
                if (distance > radius * kHandBackRadiusFactor && math.dot(heading, toResume / math.max(distance, 1f)) > kHandBackAlignment)
                {
                    FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                    flight.m_Holding = false;
                    m_Flights[entity] = flight;
                    Steer(entity, resume, flight.m_ResumeDirection);
                    return;
                }

                // Otherwise come round to a gate behind the point, on the path's own course, so the airplane arrives lined up.
                float2 course = math.lengthsq(flight.m_ResumeDirection.xz) > 0.5f
                    ? math.normalize(flight.m_ResumeDirection.xz)
                    : flight.m_Pattern.m_Inbound;
                float2 gate = resume.xz - course * (radius * kGateRadiusFactor);
                float3 target = math.distance(position.xz, gate) > radius ? new float3(gate.x, resume.y, gate.y) : resume;
                Steer(entity, target, default);
            }

            /// <summary>Sets the steering target and a speed the airplane can actually reach this tick.</summary>
            private void Steer(Entity entity, float3 target, float3 direction)
            {
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float3 position = m_TransformData[entity].m_Position;
                // Climb and descend gently: a target high above and close by (a high holding level near the airplane) would
                // otherwise make it shoot up almost vertically. Even the airplane's own climb angle (20 degrees) looked like that
                // in a hold, and the airplane overshoots the angle a little while turning.
                float reach = math.distance(target.xz, position.xz) * kSteeringSlope;
                target.y = math.clamp(target.y, position.y - reach, position.y + reach);
                // The move system sets the velocity to exactly m_MaxSpeed, so it must stay inside the reachable range.
                float maxSpeed = MathUtils.Clamp(GetHoldingSpeed(airplaneData), VehicleUtils.CalculateSpeedRange(airplaneData, GetSpeed(entity), kTimeStep));
                AircraftNavigation navigation = m_NavigationData[entity];
                navigation.m_TargetPosition = target;
                navigation.m_TargetDirection = direction;
                navigation.m_MaxSpeed = maxSpeed;
                // Same climb rule the game uses for airplanes that aren't landing.
                navigation.m_MinClimbAngle = math.atan((target.y - position.y) * 0.02f) * airplaneData.m_ClimbAngle / (math.PI * 0.5f);
                m_NavigationData[entity] = navigation;
            }

            private float GetSpeed(Entity entity)
            {
                return math.length(m_MovingData[entity].m_Velocity);
            }

            /// <summary>
            /// Speed for holding and the traffic pattern. The move system keeps the nose at least at
            /// lerp(-climb angle, slow pitch angle, (1 - f)^2), where f is how far the speed is between the minimum and maximum
            /// flying speed: near the minimum the airplane flies nose-up like in the landing flare. So fly at least fast
            /// enough for that minimum pitch to be level or below.
            /// </summary>
            internal static float GetHoldingSpeed(AirplaneData airplaneData)
            {
                float2 range = airplaneData.m_FlyingSpeed;
                float climb = math.max(airplaneData.m_ClimbAngle, 0f);
                float slow = math.max(airplaneData.m_SlowPitchAngle, 0f);
                float level = climb + slow > 0.0001f ? 1f - math.sqrt(climb / (climb + slow)) : 0f;
                float levelSpeed = math.lerp(range.x, range.y, math.saturate(level + kLevelAttitudeMargin));
                return math.min(math.max(range.x * kHoldingSpeedFactor, levelSpeed), range.y);
            }

            /// <summary>One roll per approach: the same airplane on the same frame always rolls the same, so the result is reproducible.</summary>
            private bool RollWeatherGoAround(Entity entity, float chance)
            {
                Unity.Mathematics.Random random = new Unity.Mathematics.Random(math.hash(new uint2((uint)entity.Index, m_Frame)) | 1u);
                return random.NextFloat() < chance;
            }

            /// <summary>
            /// The reason the pilot gives, picked by the weights for the current weather (GoAroundReasons, city-wide), as
            /// <see cref="GoAroundReason"/> + 1; 0 when every weight is 0 (it just says "going around").
            /// </summary>
            private int PickPilotGoAroundReason(Entity entity)
            {
                FixedList64Bytes<float> weights = m_Settings.m_GoAroundReasonWeights;
                float total = 0f;
                int last = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    total += weights[i];
                    if (weights[i] > 0f)
                    {
                        last = i + 1;
                    }
                }
                if (total <= 0f)
                {
                    return 0;
                }
                Unity.Mathematics.Random random = new Unity.Mathematics.Random(math.hash(new uint2((uint)entity.Index, m_Frame + 1u)) | 1u);
                float pick = random.NextFloat() * total;
                for (int i = 0; i < weights.Length; i++)
                {
                    pick -= weights[i];
                    if (pick < 0f && weights[i] > 0f)
                    {
                        return i + 1;
                    }
                }
                return last;
            }

            /// <summary>At the decision height (200 ft above the runway by default, add-on tunable): still in the air, on short final.</summary>
            private bool IsBelowDecisionHeight(Entity entity, in Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                return position.y - flight.m_EntryPosition.y < SettingsForRunway(flight.m_Runway).m_DecisionHeight
                    && math.distancesq(position.xz, flight.m_EntryPosition.xz) < kDecisionDistance * kDecisionDistance;
            }

            private byte TakeStackLevel(Entity runway)
            {
                RunwayState state = m_Runways[runway];
                // On top of the stack, one level above the highest airplane holding (not into a free level under the ones
                // already waiting, which leave first): StepDownHolds closes any gap under it.
                int level = state.m_StackMask == 0 ? 0 : math.min(64 - math.lzcnt(state.m_StackMask), kMaxStackLevel);
                state.m_StackMask |= 1ul << level;
                m_Runways[runway] = state;
                return (byte)level;
            }

            /// <summary>
            /// The holding stack moves down: once a level is free (the arrival under it was cleared or left), each holding
            /// arrival above it is told to descend to the lowest free level under it, lowest airplane first, so the free levels
            /// close up in one tick with one instruction each. Arrivals then leave the stack from low down: from the top they
            /// reached the IF 1,000-2,000 m too high and spent a minute or two in the descent hold.
            /// </summary>
            private void StepDownHolds()
            {
                NativeList<int2> candidates = new NativeList<int2>(Allocator.Temp); // (level, index into entities)
                NativeList<Entity> entities = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_Flights)
                {
                    Flight flight = pair.Value;
                    if (flight.m_Holding && flight.m_Arrival && flight.m_Clearance != Clearance.Cleared
                        && flight.m_StackLevel != kNoStackLevel && flight.m_StackLevel > 0)
                    {
                        candidates.Add(new int2(flight.m_StackLevel, entities.Length));
                        entities.Add(pair.Key);
                    }
                }
                if (candidates.Length > 1)
                {
                    candidates.Sort(new StackLevelOrder());
                }
                for (int i = 0; i < candidates.Length; i++)
                {
                    Entity entity = entities[candidates[i].y];
                    Flight flight = m_Flights[entity];
                    if (!m_Runways.TryGetValue(flight.m_Runway, out RunwayState state))
                    {
                        continue;
                    }
                    // Down to the lowest free level under it, above the next airplane down: one instruction.
                    int level = flight.m_StackLevel;
                    while (level > 0 && (state.m_StackMask & (1ul << (level - 1))) == 0)
                    {
                        level--;
                    }
                    if (level == flight.m_StackLevel)
                    {
                        continue;
                    }
                    state.m_StackMask = (state.m_StackMask & ~(1ul << flight.m_StackLevel)) | (1ul << level);
                    m_Runways[flight.m_Runway] = state;
                    flight.m_Pattern.m_Fix.y -= (flight.m_StackLevel - level) * kStackSpacing;
                    flight.m_StackLevel = (byte)level;
                    m_Flights[entity] = flight;
                    Say(entity, AtcMessageKind.DescendInHold, flight,
                        (int)math.round((flight.m_Pattern.m_Fix.y - flight.m_EntryPosition.y) / (100f * 0.3048f)) * 100);
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} descends in the hold to level {flight.m_StackLevel} for runway {flight.m_Runway}");
                    }
                }
                candidates.Dispose();
                entities.Dispose();
            }

            private struct StackLevelOrder : System.Collections.Generic.IComparer<int2>
            {
                public int Compare(int2 a, int2 b) => a.x.CompareTo(b.x);
            }

            /// <summary>
            /// Moves an airplane going around off its runway lane, back to the end of its last airway lane: the runway lane
            /// becomes the next element of its path again, and its queued lanes (the runway exit and taxiways) are dropped.
            /// While the tower flies it round the pattern the game doesn't advance its lanes, so it stayed on the runway lane
            /// the whole circuit, as the vehicle ahead of everything rolling on it: the arrival that had landed couldn't roll
            /// out and the departure lined up couldn't take off ("blocked by" it) for minutes, so the next arrival went around
            /// too, and kept the runway blocked in turn. Returns false (nothing changed) when its path shows no airway lane
            /// just before the runway lane.
            /// </summary>
            private bool LeaveRunwayLane(Entity entity, ref AircraftCurrentLane currentLane)
            {
                PathOwner pathOwner = m_PathOwnerData[entity];
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int runwayIndex = -1;
                for (int i = math.min(pathOwner.m_ElementIndex, path.Length) - 1; i >= 1; i--)
                {
                    if (path[i].m_Target == currentLane.m_Lane)
                    {
                        runwayIndex = i;
                        break;
                    }
                }
                // Back over the runway's lanes (it may be on a later one already) to the airway lane before them.
                while (runwayIndex >= 1 && !IsAirwayLane(path[runwayIndex - 1].m_Target) && runwayIndex > pathOwner.m_ElementIndex - kRunwayLanesBack)
                {
                    runwayIndex--;
                }
                if (runwayIndex < 1 || !IsAirwayLane(path[runwayIndex - 1].m_Target))
                {
                    return false;
                }
                PathElement airway = path[runwayIndex - 1];
                Entity runwayLane = currentLane.m_Lane;
                float start = math.lerp(airway.m_TargetDelta.y, airway.m_TargetDelta.x, kFastForwardLaneShare);
                currentLane.m_Lane = airway.m_Target;
                currentLane.m_CurvePosition = new float3(start, start, airway.m_TargetDelta.y);
                currentLane.m_LaneFlags = AircraftLaneFlags.Airway | AircraftLaneFlags.Flying;
                m_CurrentLaneData[entity] = currentLane;
                m_NavigationLanes[entity].Clear();
                pathOwner.m_ElementIndex = runwayIndex;
                m_PathOwnerData[entity] = pathOwner;
                if (m_Settings.m_DetailedLog)
                {
                    Mod.Log.Info($"{entity} going around: off runway lane {runwayLane}, back on airway lane {airway.m_Target}");
                }
                return true;
            }

            /// <summary>How far back along its path an airplane going around is looked for its last airway lane.</summary>
            private const int kRunwayLanesBack = 8;

            private bool IsAirwayLane(Entity lane)
            {
                return m_ConnectionLaneData.TryGetComponent(lane, out Game.Net.ConnectionLane connection) && (connection.m_Flags & ConnectionLaneFlags.Airway) != 0;
            }

            private void FreeStackLevel(Entity runway, byte level)
            {
                if (level != kNoStackLevel && m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    state.m_StackMask &= ~(1ul << level);
                    m_Runways[runway] = state;
                }
            }
        }
    }
}
