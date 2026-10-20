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
            private void GoAround(Entity entity, ref Flight flight, string reason, bool pilotInitiated)
            {
                // Close to the threshold the game has already moved the approach on from "approaching" to "landing", and in
                // that state it treats being below the steering target as touchdown and drops the flying state, so the
                // climbing airplane would turn into a ground vehicle in the air. Put the approach state it had back.
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Landing) != 0 && (currentLane.m_LaneFlags & AircraftLaneFlags.Approaching) == 0)
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
                Say(entity, pilotInitiated ? AtcMessageKind.GoingAround : AtcMessageKind.GoAround, flight,
                    math.dot(side, MathUtils.Right(landing)) > 0f ? 1 : 0, ClosedBy(flight.m_Runway));
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
            private static float GetHoldingSpeed(AirplaneData airplaneData)
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

            /// <summary>At the decision height: still in the air, below 100 ft above the runway, on short final.</summary>
            private bool IsBelowDecisionHeight(Entity entity, in Flight flight)
            {
                float3 position = m_TransformData[entity].m_Position;
                return position.y - flight.m_EntryPosition.y < kDecisionHeight
                    && math.distancesq(position.xz, flight.m_EntryPosition.xz) < kDecisionDistance * kDecisionDistance;
            }

            private byte TakeStackLevel(Entity runway)
            {
                RunwayState state = m_Runways[runway];
                int level = math.min(math.tzcnt(~state.m_StackMask), kMaxStackLevel);
                state.m_StackMask |= 1ul << level;
                m_Runways[runway] = state;
                return (byte)level;
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
