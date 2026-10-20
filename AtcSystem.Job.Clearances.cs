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
                // clear for them is decided at the decision height. A departure lifting off still does.
                if (scan.m_Occupied != Entity.Null && (!scan.m_Flying || scan.m_TakingOff)
                    && m_Runways.TryGetValue(scan.m_Occupied, out RunwayState occupied))
                {
                    if (occupied.m_OccupiedTick != m_Tick)
                    {
                        occupied.m_OccupiedTick = m_Tick;
                        occupied.m_Occupants = 0;
                        occupied.m_GroundOccupants = 0;
                    }
                    occupied.m_Occupants++;
                    if (!scan.m_Flying)
                    {
                        occupied.m_GroundOccupants++;
                    }
                    m_Runways[scan.m_Occupied] = occupied;
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

            private bool TryRequest(ref FlightScan scan)
            {
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
