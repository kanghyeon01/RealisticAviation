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
            // ------------------------------------------------------------ clearances

            /// <summary>Updates or creates this airplane's request. Returns true when it has one afterwards.</summary>
            private bool UpdateFlight(ref FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                bool hasFlight = m_Flights.TryGetValue(entity, out Flight flight);

                // Safety net: an airplane flying the pattern or holding that the game no longer considers airborne (landed
                // after all) is handed back at once, so it can never sit on the runway as a pattern flight forever.
                if (hasFlight && !scan.m_Flying && (flight.m_InCircuit || (flight.m_Holding && scan.m_Occupied == flight.m_Runway)))
                {
                    if (flight.m_Holding)
                    {
                        FreeStackLevel(flight.m_Runway, flight.m_StackLevel);
                    }
                    flight.m_InCircuit = false;
                    flight.m_Holding = false;
                    m_Flights[entity] = flight;
                    LogEvent(entity, "lost its flying state in the pattern; handed back to the game on runway", flight.m_Runway);
                }

                // After a go-around the airplane's game state still says "on final on the runway lane" while it flies the
                // pattern or holds; it isn't using the runway, and its path can't tell where it is.
                if (hasFlight && scan.m_Flying && (flight.m_InCircuit || flight.m_Holding) && scan.m_Occupied == flight.m_Runway)
                {
                    flight.m_LastTick = m_Tick;
                    flight.m_LastFlying = true;
                    UpdateStraightEta(entity, ref flight);
                    NoteArrival(entity, flight);
                    if (flight.m_Clearance == Clearance.Waiting && m_Tick % kDiagnosticInterval == 0 && m_Frame - flight.m_RequestFrame > kLongWaitFrames)
                    {
                        LogLongWait(entity, flight);
                    }
                    m_Flights[entity] = flight;
                    return true;
                }

                // Airborne arrivals on final are on the runway lane too, but they don't occupy the runway yet; whether it is
                // clear for them is decided at the decision height. A departure lifting off still does. One that just left
                // the runway's lanes on the ground does until its tail is past the hold-short line.
                if (scan.m_Occupied != Entity.Null && (!scan.m_Flying || scan.m_TakingOff))
                {
                    m_RunwayExits.Remove(entity);
                    CountOccupant(scan.m_Occupied, !scan.m_Flying);
                }
                else if (!scan.m_Flying && IsStillExiting(entity, out Entity exitedRunway))
                {
                    CountOccupant(exitedRunway, true);
                }

                if (hasFlight)
                {
                    flight.m_LastTick = m_Tick;
                    flight.m_LastFlying = scan.m_Flying;
                    if (!scan.m_PathUnknown)
                    {
                        if (scan.m_Occupied == flight.m_Runway)
                        {
                            // An arrival is on the runway lane for its whole final approach; what matters is the touchdown.
                            if (flight.m_Arrival && !flight.m_TouchedDown && !scan.m_Flying && flight.m_SeenOnRunway && flight.m_Clearance == Clearance.Cleared)
                            {
                                flight.m_TouchedDown = true;
                            }
                            flight.m_SeenOnRunway = true;
                            flight.m_OnRunwayTick = m_Tick;
                            if (scan.m_Flying && !scan.m_TakingOff && !flight.m_TouchedDown)
                            {
                                UpdateStraightEta(entity, ref flight);
                            }
                            else
                            {
                                flight.m_LastDistance = 0f;
                                flight.m_LastEta = 0f;
                            }
                            if (scan.m_Flying && !scan.m_TakingOff && flight.m_Arrival && flight.m_Clearance == Clearance.Cleared && !flight.m_ToldLanding)
                            {
                                // On final: the tower clears it to land.
                                flight.m_ToldLanding = true;
                                Say(entity, AtcMessageKind.ClearedToLand, flight);
                            }
                        }
                        else if (flight.m_SeenOnRunway || scan.m_Upcoming != flight.m_Runway)
                        {
                            // Used the runway and left it, or no longer heading for it. It may ask for the next one below.
                            LogEvent(entity, flight.m_SeenOnRunway ? "vacated runway" : "no longer heading for runway", flight.m_Runway);
                            if (flight.m_SeenOnRunway && !scan.m_Flying)
                            {
                                // Landed or crossed: on the runway until its tail is past the hold-short line.
                                m_RunwayExits[entity] = new RunwayExit
                                {
                                    m_Runway = flight.m_Runway,
                                    m_Point = m_TransformData[entity].m_Position.xz,
                                    m_ClearDistance = GetTailLength(m_PrefabRefData[entity].m_Prefab) + HoldShortSetback(entity),
                                    m_LastTick = m_Tick,
                                };
                                CountOccupant(flight.m_Runway, true);
                            }
                            if (flight.m_TouchedDown)
                            {
                                // Off the runway: the tower hands it to ground, which clears it to its gate.
                                Say(entity, AtcMessageKind.ContactGround, flight);
                                Say(entity, AtcMessageKind.TaxiToGate, flight);
                                m_Runways.TryGetValue(flight.m_Runway, out RunwayState landedOn);
                                LogPathToGate(entity, landedOn.m_Airport);
                            }
                            else if (flight.m_SeenOnRunway && scan.m_Flying)
                            {
                                // Airborne after takeoff: on to departure control, or off the frequency at a class D airport.
                                // Value: 2 = its own departure frequency (class B), 1 = approach works departures too, 0 = none (class D).
                                AirspaceClass departedClass = GetAirspaceClass(flight.m_Runway);
                                int departureControl = departedClass == AirspaceClass.B ? 2 : departedClass == AirspaceClass.C ? 1 : 0;
                                Say(entity, AtcMessageKind.ContactDeparture, flight, departureControl);
                                m_Runways.TryGetValue(flight.m_Runway, out RunwayState departedFrom);
                                m_Zones[entity] = new ZoneState
                                {
                                    m_Airport = departedFrom.m_Airport,
                                    m_Silent = departedClass == AirspaceClass.D,
                                    m_Departing = departedClass == AirspaceClass.B,
                                    m_Climbing = true,
                                    m_DepartureControl = (byte)departureControl,
                                    m_TakeoffFrame = m_Frame,
                                    m_Tick = m_Tick,
                                };
                            }
                            DropFlight(entity, flight);
                            return TryRequest(ref scan);
                        }
                        else
                        {
                            flight.m_LastDistance = scan.m_Distance;
                            flight.m_LastEta = scan.m_Flying ? EstimateArrivalSeconds(entity, scan) : 0f;
                            if (scan.m_Flying && TryGetGlideRouteDistance(entity, flight, out float routeDistance))
                            {
                                flight.m_LastEta = routeDistance / EstimateSpeed(entity);
                            }
                            if (flight.m_DescentHold && scan.m_Flying)
                            {
                                // Descending in the hold before final: the path doesn't know, add the flight the descent takes.
                                float excess = math.max(0f, m_TransformData[entity].m_Position.y - flight.m_DescentPattern.m_Fix.y);
                                flight.m_LastEta += excess / kSteeringSlope / EstimateSpeed(entity);
                            }
                            flight.m_EntryPosition = scan.m_EntryPosition;
                            flight.m_EntryDirection = scan.m_EntryDirection;
                            flight.m_Crossing = scan.m_Crossing;
                            if (flight.m_Clearance == Clearance.Cleared
                                && m_Frame - flight.m_ClearedFrame > (scan.m_Flying ? kAirClearanceTimeout : kGroundClearanceTimeout))
                            {
                                LogEvent(entity, "clearance timed out, back of the line for runway", flight.m_Runway);
                                ReleaseRunway(entity, flight.m_Runway);
                                flight.m_Clearance = Clearance.Waiting;
                                flight.m_Sequence = NextSequence();
                                flight.m_RequestFrame = m_Frame;
                            }
                        }
                    }
                    if (flight.m_Clearance == Clearance.Waiting && m_Tick % kDiagnosticInterval == 0 && m_Frame - flight.m_RequestFrame > kLongWaitFrames)
                    {
                        LogLongWait(entity, flight);
                    }
                    NoteArrival(entity, flight);
                    m_Flights[entity] = flight;
                    return true;
                }
                return TryRequest(ref scan);
            }

            /// <summary>
            /// ETA where the path can't say: straight to the threshold on final; in the pattern, along the remaining legs
            /// and then to the threshold; holding after a go-around, via the final-approach start it will resume at.
            /// </summary>
            private void UpdateStraightEta(Entity entity, ref Flight flight)
            {
                float2 position = m_TransformData[entity].m_Position.xz;
                float2 threshold = flight.m_EntryPosition.xz;
                if (flight.m_InCircuit)
                {
                    float2 upwind = flight.m_UpwindEnd.xz;
                    float2 crosswind = flight.m_CrosswindEnd.xz;
                    float2 downwind = flight.m_DownwindEnd.xz;
                    flight.m_LastDistance = flight.m_CircuitLeg == CircuitLeg.Upwind
                        ? math.distance(position, upwind) + math.distance(upwind, crosswind) + math.distance(crosswind, downwind)
                        : flight.m_CircuitLeg == CircuitLeg.Crosswind
                            ? math.distance(position, crosswind) + math.distance(crosswind, downwind)
                            : flight.m_CircuitLeg == CircuitLeg.Downwind
                                ? math.distance(position, downwind)
                                : 0f;
                    // From the end of downwind (or from beyond it, on the extension), the base turn and final to the threshold.
                    flight.m_LastDistance += math.distance(flight.m_CircuitLeg == CircuitLeg.ExtendedDownwind ? position : downwind, threshold);
                }
                else if (flight.m_Holding)
                {
                    float2 resume = flight.m_ResumeTarget.xz;
                    flight.m_LastDistance = math.distance(position, resume) + math.distance(resume, threshold);
                }
                else
                {
                    flight.m_LastDistance = math.distance(position, threshold);
                }
                flight.m_LastEta = flight.m_LastDistance / math.max(math.length(m_MovingData[entity].m_Velocity), 1f);
            }

            /// <summary>Adds a cleared arrival to its runway's arrival picture for this tick.</summary>
            private void NoteArrival(Entity entity, in Flight flight)
            {
                if (flight.m_Clearance != Clearance.Cleared || !flight.m_LastFlying && !flight.m_TouchedDown
                    || !m_Runways.TryGetValue(flight.m_Runway, out RunwayState state))
                {
                    return;
                }
                state.BeginArrivals(m_Tick);
                if (flight.m_TouchedDown)
                {
                    if (flight.m_OnRunwayTick == m_Tick)
                    {
                        state.m_LandingTraffic = entity;
                    }
                }
                else
                {
                    state.AddArrival(flight.m_LastEta, flight.m_Size);
                }
                m_Runways[flight.m_Runway] = state;
            }

            /// <summary>
            /// An arrival calls once it is inside its airport's zone plus a margin (phase 4-3), measured straight to the airport,
            /// whatever other zones overlap there. An airport without airspace data uses the B-to-D middle value.
            /// </summary>
            private bool IsWithinContact(in FlightScan scan)
            {
                float2 position = m_TransformData[scan.m_Entity].m_Position.xz;
                if (m_Runways.TryGetValue(scan.m_Upcoming, out RunwayState state) && m_Airspaces.TryGetValue(state.m_Airport, out AirportSpace space))
                {
                    return math.distancesq(position, space.m_Center) <= space.ContactDistance * space.ContactDistance;
                }
                return scan.m_Distance <= AirportSpace.RadiusOf(AirspaceClass.C) + AirportSpace.kContactMargin;
            }

            /// <summary>
            /// An airplane already landing when the tower starts (a city just loaded, or the mod turned on mid-game) is on its
            /// runway lane, so no runway lies ahead for it to ask for: it is taken as an arrival cleared to land. Untracked, the
            /// runway looked free to departures and it got no decision-height check: it once landed on a departure.
            /// </summary>
            private bool AdoptLandingAirplane(in FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if ((currentLane.m_LaneFlags & (AircraftLaneFlags.Landing | AircraftLaneFlags.Approaching)) == 0
                    || !m_Runways.ContainsKey(scan.m_Occupied) || !m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    return false;
                }
                // The runway lane starts at the threshold, where a landing airplane's place on it stays until touchdown.
                float3 entry = MathUtils.Position(curve.m_Bezier, currentLane.m_CurvePosition.x);
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, currentLane.m_CurvePosition.x).xz;
                float distance = math.distance(m_TransformData[entity].m_Position.xz, entry.xz);
                Flight flight = new Flight
                {
                    m_Runway = scan.m_Occupied,
                    m_Sequence = NextSequence(),
                    m_RequestFrame = m_Frame,
                    m_ClearedFrame = m_Frame,
                    m_LastTick = m_Tick,
                    m_LastDistance = distance,
                    m_LastFlying = true,
                    m_LastEta = distance / EstimateSpeed(entity),
                    m_EntryPosition = entry,
                    m_EntryDirection = math.normalizesafe(currentLane.m_CurvePosition.z >= currentLane.m_CurvePosition.x ? tangent : -tangent),
                    m_Clearance = Clearance.Cleared,
                    m_Size = m_PrefabAircraftData[m_PrefabRefData[entity].m_Prefab].m_SizeClass,
                    m_Arrival = true,
                    m_SeenOnRunway = true,
                    m_ProcStage = kStageFinal,
                    m_FastForwardTried = true,
                };
                m_Flights.Add(entity, flight);
                LogEvent(entity, "was already landing when the tower started; taken as cleared to land on runway", flight.m_Runway);
                return true;
            }

            /// <summary>
            /// An airplane already on a runway on the ground when the tower starts (not landing) has no runway ahead to ask for
            /// either. One past its line-up point is on its takeoff roll: taken as cleared for takeoff, it holds the runway; one short
            /// of it is told to line up and wait, stops there and goes once the runway is free. Two past it both keep rolling
            /// (stopped halfway down the runway and started again would look stranger): a serious incident, and the one behind
            /// gets a pilot deviation call. Untracked, both were left to the game: right after loading, a departure at the
            /// threshold started its takeoff roll while the one ahead was rolling.
            /// </summary>
            private bool AdoptRunwayGroundAirplane(in FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Landing) != 0 || !m_Runways.TryGetValue(scan.m_Occupied, out RunwayState state)
                    || !m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    return false;
                }
                // Past its line-up point it is on its takeoff roll; short of it (or still entering), it stops there. (By speed, one
                // just starting to roll counted as rolling too, found the runway taken and was left to the game.)
                bool rolling = RunwayProgress(entity, out float progress, out float lineUpPoint) && progress > lineUpPoint;
                bool alsoRolling = false, behindHolder = false;
                if (rolling && state.m_Holder != Entity.Null)
                {
                    if (m_Flights.TryGetValue(state.m_Holder, out Flight holder) && !holder.m_Arrival && !holder.m_LastFlying
                        && RunwayProgress(state.m_Holder, out float holderProgress, out _))
                    {
                        // Another departure is rolling on this runway: both go, whichever the tower saw first. The holder keeps
                        // the runway; the ground occupants keep it closed to others until both are off it.
                        alsoRolling = true;
                        behindHolder = holderProgress >= progress;
                        Mod.Log.Info($"Serious incident: {(behindHolder ? entity : state.m_Holder)} started its takeoff roll while " +
                            $"{(behindHolder ? state.m_Holder : entity)} was still rolling on runway {scan.m_Occupied}");
                        if (!behindHolder)
                        {
                            Say(state.m_Holder, AtcMessageKind.PilotDeviation, holder);
                        }
                    }
                    else
                    {
                        rolling = false;
                    }
                }
                if (!rolling && state.m_LineUp != Entity.Null)
                {
                    return false;
                }
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, currentLane.m_CurvePosition.x).xz;
                Flight flight = new Flight
                {
                    m_Runway = scan.m_Occupied,
                    m_Sequence = NextSequence(),
                    m_RequestFrame = m_Frame,
                    m_ClearedFrame = m_Frame,
                    m_LastTick = m_Tick,
                    m_EntryPosition = MathUtils.Position(curve.m_Bezier, currentLane.m_CurvePosition.x),
                    m_EntryDirection = math.normalizesafe(currentLane.m_CurvePosition.z >= currentLane.m_CurvePosition.x ? tangent : -tangent),
                    m_Clearance = rolling ? Clearance.Cleared : Clearance.LineUp,
                    m_Size = m_PrefabAircraftData[m_PrefabRefData[entity].m_Prefab].m_SizeClass,
                    m_SeenOnRunway = true,
                    m_OnRunwayTick = m_Tick,
                    // Not for one rolling: "told" and cleared on the runway reads as a takeoff clearance cancelled for a closure,
                    // which cleared it again and stopped it for the readback, halfway down the runway.
                    m_Told = !rolling,
                };
                m_Flights.Add(entity, flight);
                if (alsoRolling)
                {
                    if (behindHolder)
                    {
                        Say(entity, AtcMessageKind.PilotDeviation, flight);
                    }
                    LogEvent(entity, "was already rolling with another departure when the tower started; taken as cleared for takeoff on runway", flight.m_Runway);
                }
                else if (rolling)
                {
                    state.m_Holder = entity;
                    LogEvent(entity, "was already rolling when the tower started; taken as cleared for takeoff on runway", flight.m_Runway);
                }
                else
                {
                    state.m_LineUp = entity;
                    Say(entity, AtcMessageKind.LineUpAndWait, flight);
                    LogEvent(entity, "was already on the runway when the tower started; told to line up and wait on runway", flight.m_Runway);
                }
                m_Runways[flight.m_Runway] = state;
                return true;
            }

            /// <summary>Counts an airplane as on the runway this tick (on the ground: not lifted off yet).</summary>
            private void CountOccupant(Entity runway, bool ground)
            {
                if (!m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    return;
                }
                if (state.m_OccupiedTick != m_Tick)
                {
                    state.m_OccupiedTick = m_Tick;
                    state.m_Occupants = 0;
                    state.m_GroundOccupants = 0;
                }
                state.m_Occupants++;
                if (ground)
                {
                    state.m_GroundOccupants++;
                }
                m_Runways[runway] = state;
            }

            /// <summary>
            /// A ground airplane off the runway's lanes whose tail isn't past the hold-short line yet (measured in a straight line
            /// from where it left the lanes, so a turning exit keeps it a little longer).
            /// </summary>
            private bool IsStillExiting(Entity entity, out Entity runway)
            {
                runway = Entity.Null;
                if (!m_RunwayExits.TryGetValue(entity, out RunwayExit exit))
                {
                    return false;
                }
                if (math.distancesq(m_TransformData[entity].m_Position.xz, exit.m_Point) >= exit.m_ClearDistance * exit.m_ClearDistance)
                {
                    m_RunwayExits.Remove(entity);
                    LogEvent(entity, "is clear of runway (tail past the hold-short line)", exit.m_Runway);
                    return false;
                }
                exit.m_LastTick = m_Tick;
                m_RunwayExits[entity] = exit;
                runway = exit.m_Runway;
                return true;
            }

            /// <summary>
            /// How far a ground airplane has come along its runway lane from the end it entered at, and where its line-up point
            /// is (as AtcJob.HoldAtLineUpPoint measures). False when it isn't on a runway lane yet (still entering).
            /// </summary>
            private bool RunwayProgress(Entity entity, out float progress, out float lineUpPoint)
            {
                lineUpPoint = GetTailLength(m_PrefabRefData[entity].m_Prefab) + kLineUpInset;
                progress = 0f;
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Runway) == 0 || !m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    return false;
                }
                float start = math.select(1f, 0f, currentLane.m_CurvePosition.z >= currentLane.m_CurvePosition.x);
                progress = math.abs(currentLane.m_CurvePosition.x - start) * curve.m_Length;
                return true;
            }

            private bool TryRequest(ref FlightScan scan)
            {
                if (!scan.m_PathUnknown && scan.m_Occupied != Entity.Null
                    && (scan.m_Flying ? !scan.m_TakingOff && AdoptLandingAirplane(scan) : AdoptRunwayGroundAirplane(scan)))
                {
                    return true;
                }
                if (scan.m_PathUnknown || scan.m_Upcoming == Entity.Null
                    || (scan.m_Flying ? !IsWithinContact(scan) || IsClimbingOut(scan.m_Entity) : scan.m_Distance > scan.m_RequestDistance || !HasTaxiClearance(scan.m_Entity)))
                {
                    return false;
                }
                Flight flight = new Flight
                {
                    m_Runway = scan.m_Upcoming,
                    m_Sequence = NextSequence(),
                    m_RequestFrame = m_Frame,
                    m_LastTick = m_Tick,
                    m_LastDistance = scan.m_Distance,
                    m_LastFlying = scan.m_Flying,
                    m_LastEta = scan.m_Flying ? EstimateArrivalSeconds(scan.m_Entity, scan) : 0f,
                    m_EntryPosition = scan.m_EntryPosition,
                    m_EntryDirection = scan.m_EntryDirection,
                    m_Clearance = Clearance.Waiting,
                    m_Size = m_PrefabAircraftData[m_PrefabRefData[scan.m_Entity].m_Prefab].m_SizeClass,
                    m_Crossing = scan.m_Crossing,
                    m_Arrival = scan.m_Flying,
                };
                m_Flights.Add(scan.m_Entity, flight);
                if (scan.m_Flying)
                {
                    // First call in the airspace: approach (or the tower at a class D airport) identifies it.
                    // Coming from another airport's airspace, that controller hands it over first; from now on its own airport
                    // keeps it (UpdateZone).
                    Entity destination = m_Runways.TryGetValue(flight.m_Runway, out RunwayState destinationState) ? destinationState.m_Airport : Entity.Null;
                    if (m_Zones.TryGetValue(scan.m_Entity, out ZoneState zone) && zone.m_Airport != destination && zone.m_Airport != Entity.Null && !zone.m_Silent)
                    {
                        SayAt(scan.m_Entity, AtcMessageKind.HandoffTo, zone.m_Airport, Entity.Null, default, (int)GetAirspaceClass(flight.m_Runway), destination,
                            DepartureFrequency(zone));
                    }
                    m_Zones[scan.m_Entity] = new ZoneState { m_Airport = destination, m_Tick = m_Tick };
                    Say(scan.m_Entity, AtcMessageKind.InitialContact, flight, (int)GetAirspaceClass(flight.m_Runway));
                }
                else
                {
                    // Ready for the runway: ground hands the departure over to the tower.
                    Say(scan.m_Entity, AtcMessageKind.ContactTower, flight);
                }
                m_PrefabNames.TryGetValue(m_PrefabRefData[scan.m_Entity].m_Prefab, out FixedString128Bytes name);
                LogEvent(scan.m_Entity, $"[{name}] asks (#{flight.m_Sequence}, {Describe(flight)}) for runway", flight.m_Runway);
                return true;
            }

            /// <summary>
            /// Seconds until an airborne airplane reaches the runway. A holding airplane's path stays frozen while it
            /// circles away from it, so the straight-line distance to the runway is used when that is longer.
            /// </summary>
            private float EstimateArrivalSeconds(Entity entity, in FlightScan scan)
            {
                float distance = math.max(scan.m_Distance, math.distance(m_TransformData[entity].m_Position.xz, scan.m_EntryPosition.xz));
                return distance / EstimateSpeed(entity);
            }

            /// <summary>
            /// Speed for an airborne ETA: the present one, but no less than the airplane's slowest flying speed (an airplane that
            /// has just appeared is nearly at rest; dividing by that gave an ETA of 4,866 s, which held every arrival behind it).
            /// </summary>
            private float EstimateSpeed(Entity entity)
            {
                float minSpeed = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab].m_FlyingSpeed.x;
                return math.max(math.length(m_MovingData[entity].m_Velocity), math.max(minSpeed, 1f));
            }

            // ------------------------------------------------------------ diagnostics

            private static string Describe(Flight flight)
            {
                return $"{(flight.m_LastFlying ? "air" : "ground")}, {flight.m_LastDistance:F0} m";
            }

            private void LogEvent(Entity entity, string what, Entity runway)
            {
                if (!m_Settings.m_DetailedLog)
                {
                    return;
                }
                Mod.Log.Info($"{entity} {what} {runway}");
            }

        }
    }
}
