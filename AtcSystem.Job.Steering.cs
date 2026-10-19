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
            // ------------------------------------------------------------ steering

            private void Apply(in FlightScan scan, NativeList<PushbackArea> pushbackAreas)
            {
                Entity entity = scan.m_Entity;
                if (!scan.m_Flying && (scan.m_Occupied != Entity.Null || IsReleased(entity)))
                {
                    PassAirborneBlocker(entity);
                }
                if (!scan.m_Flying && m_Settings.m_Pushback)
                {
                    UpdatePushback(entity);
                    if (pushbackAreas.Length != 0 && scan.m_Occupied == Entity.Null && !m_Pushbacks.ContainsKey(entity))
                    {
                        GiveWayToPushbacks(entity, pushbackAreas);
                    }
                }
                // Ground control only at a real airport: an airplane passing through on the ground lanes of an outside connection
                // (the routeless "TRAFFIC" ones) is not taxiing anywhere and must not be cleared.
                if (!scan.m_Flying && scan.m_Occupied == Entity.Null && !scan.m_PathUnknown && !m_Pushbacks.ContainsKey(entity) && !m_Flights.ContainsKey(entity)
                    && m_Airspaces.ContainsKey(GetAirportOf(entity)))
                {
                    ClearToTaxi(entity);
                    if (m_LastMessages.TryGetValue(entity, out AtcMessage last) && last.m_Kind == AtcMessageKind.TaxiToRunway
                        && m_Frame - last.m_Frame < kResponseFrames)
                    {
                        StandStill(entity);
                    }
                    HoldForGate(entity);
                }
                if (!scan.m_Flying && scan.m_Occupied == Entity.Null && m_Settings.m_TaxiSpacing > 0f)
                {
                    KeepTaxiSpacing(entity);
                }
                if (!m_Flights.TryGetValue(entity, out Flight flight))
                {
                    return;
                }
                if (flight.m_Clearance != Clearance.Waiting && !flight.m_TouchedDown
                    && m_Runways.TryGetValue(flight.m_Runway, out RunwayState runwayState) && runwayState.IsClosed(m_Tick)
                    && CancelForClosure(entity, scan, ref flight))
                {
                    return;
                }
                if (!scan.m_Flying && flight.m_Clearance != Clearance.Waiting && m_Frame - flight.m_ClearedFrame < kResponseFrames
                    && math.lengthsq(m_MovingData[entity].m_Velocity) < kPushbackStartSpeed * kPushbackStartSpeed)
                {
                    // A standing airplane reads the clearance back before it moves (it is not shown starting its roll first).
                    StandStill(entity);
                    return;
                }
                if (flight.m_Told && flight.m_Clearance == Clearance.Cleared && !flight.m_Arrival && !scan.m_Flying && scan.m_Occupied == flight.m_Runway)
                {
                    // Its takeoff clearance was cancelled for a closure that is now over: cleared again (moving a second later).
                    flight.m_Told = false;
                    flight.m_ClearedFrame = m_Frame;
                    m_Flights[entity] = flight;
                    Say(entity, AtcMessageKind.ClearedForTakeoff, flight);
                    StandStill(entity);
                    return;
                }
                if (flight.m_Clearance == Clearance.LineUp && !scan.m_Flying)
                {
                    HoldAtLineUpPoint(entity, scan);
                    return;
                }
                if (flight.m_InCircuit)
                {
                    FlyCircuit(entity, ref flight);
                    m_HoldingFlights.Add(entity);
                    return;
                }
                if (scan.m_Occupied == flight.m_Runway && !flight.m_Holding)
                {
                    if (scan.m_Flying && !scan.m_TakingOff && flight.m_Clearance == Clearance.Cleared && !flight.m_TouchedDown
                        && IsBelowDecisionHeight(entity, flight))
                    {
                        if (!IsRunwayClearToLand(flight.m_Runway, out string reason))
                        {
                            GoAround(entity, ref flight, reason, false);
                            FlyCircuit(entity, ref flight);
                            m_HoldingFlights.Add(entity);
                        }
                        else if (!flight.m_WeatherChecked)
                        {
                            // The pilot's own decision at minimums: in rain or snow, now and then the runway isn't in sight.
                            flight.m_WeatherChecked = true;
                            m_Flights[entity] = flight;
                            if (m_Settings.m_WeatherGoAroundChance > 0f && RollWeatherGoAround(entity))
                            {
                                GoAround(entity, ref flight, m_Settings.m_Weather == WeatherKind.Clear ? "pilot decision" : "weather: " + m_Settings.m_Weather.ToString().ToLowerInvariant(), true);
                                FlyCircuit(entity, ref flight);
                                m_HoldingFlights.Add(entity);
                            }
                        }
                    }
                    return;
                }
                if (flight.m_Holding)
                {
                    if (flight.m_Clearance == Clearance.Cleared && m_Frame - flight.m_ClearedFrame >= kResponseFrames)
                    {
                        ReturnToPath(entity, ref flight);
                    }
                    else
                    {
                        Hold(entity, ref flight);
                    }
                    if (flight.m_Holding)
                    {
                        m_HoldingFlights.Add(entity);
                    }
                }
                else if (flight.m_Clearance == Clearance.Waiting)
                {
                    if (scan.m_Flying)
                    {
                        StartHolding(entity, ref flight);
                        Hold(entity, ref flight);
                        m_HoldingFlights.Add(entity);
                    }
                    else if (!scan.m_PathUnknown)
                    {
                        HoldShort(entity, scan);
                        if (!flight.m_Told)
                        {
                            flight.m_Told = true;
                            m_Flights[entity] = flight;
                            Say(entity, AtcMessageKind.HoldShort, flight);
                        }
                    }
                }
            }

            /// <summary>
            /// The airport is closed: an arrival on final goes around (and then holds, as nothing is cleared), one further
            /// out holds, and a ground airplane not yet on the runway holds short. Airplanes already on the runway carry on.
            /// Returns true when the airplane has been steered for this tick.
            /// </summary>
            private bool CancelForClosure(Entity entity, in FlightScan scan, ref Flight flight)
            {
                if (!scan.m_Flying)
                {
                    if (!flight.m_SeenOnRunway)
                    {
                        RevokeGroundClearance(entity, ref flight, AtcMessageKind.HoldShortClosed);
                        return false;
                    }
                    // On the runway with a takeoff clearance but not on its takeoff roll yet (still entering, or standing lined up):
                    // the tower cancels it and the airplane holds in position until the airport reopens. Only one on its takeoff
                    // roll (the game's TakingOff state, wheels rolling) carries on; speed alone can't tell entering from rolling.
                    bool rolling = scan.m_TakingOff && math.lengthsq(m_MovingData[entity].m_Velocity) >= kRollingSpeed * kRollingSpeed;
                    if (flight.m_Clearance == Clearance.Cleared && !flight.m_Arrival && scan.m_Occupied == flight.m_Runway && !rolling)
                    {
                        // Back to "line up and wait": it goes on to the line-up point and waits there lined up (rather than stopping
                        // half on the runway), and the usual release clears it for takeoff when the airport reopens.
                        if (m_Runways.TryGetValue(flight.m_Runway, out RunwayState lineUpState) && (lineUpState.m_LineUp == Entity.Null || lineUpState.m_LineUp == entity))
                        {
                            ReleaseRunway(entity, flight.m_Runway);
                            lineUpState = m_Runways[flight.m_Runway];
                            lineUpState.m_LineUp = entity;
                            m_Runways[flight.m_Runway] = lineUpState;
                            flight.m_Clearance = Clearance.LineUp;
                            m_Flights[entity] = flight;
                            LogEvent(entity, "takeoff clearance cancelled (airport closed), lining up and waiting on runway", flight.m_Runway);
                            Say(entity, AtcMessageKind.CancelTakeoff, flight, 1, ClosedBy(flight.m_Runway));
                            HoldAtLineUpPoint(entity, scan);
                            return true;
                        }
                        // Someone else is lined up already (rare): it holds where it is until the airport reopens.
                        StandStill(entity);
                        if (!flight.m_Told)
                        {
                            flight.m_Told = true;
                            m_Flights[entity] = flight;
                            LogEvent(entity, "takeoff clearance cancelled (airport closed), holding in position on runway", flight.m_Runway);
                            Say(entity, AtcMessageKind.CancelTakeoff, flight, 0, ClosedBy(flight.m_Runway));
                        }
                        return true;
                    }
                    return false;
                }
                if (scan.m_TakingOff)
                {
                    return false;
                }
                if (scan.m_Occupied == flight.m_Runway && !flight.m_Holding && !flight.m_InCircuit)
                {
                    GoAround(entity, ref flight, "airport closed", false);
                    FlyCircuit(entity, ref flight);
                    m_HoldingFlights.Add(entity);
                    return true;
                }
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                LogEvent(entity, "approach clearance cancelled (airport closed), holding for runway", flight.m_Runway);
                Say(entity, flight.m_ToldLanding ? AtcMessageKind.LandingClearanceCancelled : AtcMessageKind.ApproachClearanceCancelled, flight, 0,
                    ClosedBy(flight.m_Runway));
                return false;
            }

            /// <summary>
            /// The landing check at the decision height. A ground airplane cleared onto the runway (or to line up) that hasn't
            /// entered it yet is told to hold short instead, as a tower would.
            /// </summary>
            private bool IsRunwayClearToLand(Entity runway, out string reason)
            {
                reason = null;
                if (!m_Runways.TryGetValue(runway, out RunwayState state))
                {
                    return true;
                }
                if (state.IsClosed(m_Tick))
                {
                    reason = "airport closed";
                    return false;
                }
                // Only airplanes still on the ground: a departure that has lifted off is no longer in the way.
                if (state.GroundOccupants(m_Tick) != 0)
                {
                    reason = "runway not clear";
                    return false;
                }
                RevokeIfNotOnRunway(state.m_Holder);
                RevokeIfNotOnRunway(state.m_LineUp);
                return true;
            }

            private void RevokeIfNotOnRunway(Entity entity)
            {
                if (entity != Entity.Null && m_Flights.TryGetValue(entity, out Flight flight) && !flight.m_LastFlying && !flight.m_SeenOnRunway)
                {
                    RevokeGroundClearance(entity, ref flight, AtcMessageKind.HoldShortForArrival);
                }
            }

            /// <summary>
            /// Takes a ground airplane's runway or line-up clearance back, so it holds short on the taxiway it is still on.
            /// Not when it can no longer stop at its hold-short point: then it is committed and carries on.
            /// </summary>
            private void RevokeGroundClearance(Entity entity, ref Flight flight, AtcMessageKind message)
            {
                if (!CanHoldShort(entity, flight))
                {
                    return;
                }
                ReleaseRunway(entity, flight.m_Runway);
                flight.m_Clearance = Clearance.Waiting;
                flight.m_Told = true;
                m_Flights[entity] = flight;
                bool closed = message == AtcMessageKind.HoldShortClosed;
                LogEvent(entity, $"clearance cancelled ({(closed ? "airport closed" : "arrival close")}), holding short of runway", flight.m_Runway);
                Say(entity, message, flight, 0, closed ? ClosedBy(flight.m_Runway) : Entity.Null);
            }

        }
    }
}
