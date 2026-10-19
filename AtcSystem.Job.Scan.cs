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
            // ------------------------------------------------------------ per-airplane scan

            private FlightScan Scan(Entity entity)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                FlightScan scan = new FlightScan
                {
                    m_Entity = entity,
                    m_Flying = (currentLane.m_LaneFlags & AircraftLaneFlags.Flying) != 0,
                    m_TakingOff = (currentLane.m_LaneFlags & AircraftLaneFlags.TakingOff) != 0,
                    m_Occupied = RunwayOfLane(currentLane.m_Lane),
                };
                PathOwner pathOwner = m_PathOwnerData[entity];
                if ((pathOwner.m_State & (PathFlags.Pending | PathFlags.Obsolete)) != 0)
                {
                    scan.m_PathUnknown = true;
                    return scan;
                }

                if (scan.m_Flying)
                {
                    // Far enough along the path to find the runway before the airplane enters the largest zone (B, 5 NM + margin);
                    // whether it calls is decided by its straight distance to its airport (IsWithinContact).
                    scan.m_RequestDistance = kAirScanDistance;
                }
                else
                {
                    Entity prefab = m_PrefabRefData[entity].m_Prefab;
                    AircraftData aircraftData = m_PrefabAircraftData[prefab];
                    scan.m_RequestDistance = math.max(kMinGroundRequestDistance,
                        VehicleUtils.GetBrakingDistance(aircraftData, aircraftData.m_GroundMaxSpeed, kTimeStep)
                        + GetNoseLength(prefab) + m_Settings.m_HoldShortSetback + kGroundRequestMargin);
                }
                float limit = scan.m_RequestDistance * kKeepRequestFactor;

                // The rest of the current lane, then the navigation lanes, then the path elements not yet copied into them.
                Entity skip = scan.m_Occupied;
                float distance = 0f;
                if (m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                {
                    distance = curve.m_Length * math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x);
                }
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = 0; i < navigationLanes.Length; i++)
                {
                    AircraftNavigationLane lane = navigationLanes[i];
                    if (VisitLane(lane.m_Lane, lane.m_CurvePosition, limit, ref skip, ref distance, ref scan))
                    {
                        return AlignEntryWithRunway(entity, scan, i, pathOwner.m_ElementIndex);
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, pathOwner.m_ElementIndex + kMaxPathScan);
                for (int i = pathOwner.m_ElementIndex; i < end; i++)
                {
                    PathElement element = path[i];
                    if (VisitLane(element.m_Target, element.m_TargetDelta, limit, ref skip, ref distance, ref scan))
                    {
                        return AlignEntryWithRunway(entity, scan, navigationLanes.Length, i);
                    }
                }
                return scan;
            }

            /// <summary>
            /// A ground airplane usually enters a runway through a junction lane that turns onto it, so the first runway lane
            /// of its path points along the taxiway. Line up and wait needs the direction of the takeoff roll instead: that
            /// of the first actual runway lane (CarLaneFlags.Runway) that follows. Crossing traffic keeps the junction direction.
            /// </summary>
            private FlightScan AlignEntryWithRunway(Entity entity, FlightScan scan, int navigationIndex, int pathIndex)
            {
                if (scan.m_Flying || scan.m_Upcoming == Entity.Null)
                {
                    return scan;
                }
                DynamicBuffer<AircraftNavigationLane> navigationLanes = m_NavigationLanes[entity];
                for (int i = navigationIndex; i < navigationLanes.Length; i++)
                {
                    if (TakeRunwayDirection(navigationLanes[i].m_Lane, navigationLanes[i].m_CurvePosition, ref scan))
                    {
                        return scan;
                    }
                }
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                int end = math.min(path.Length, pathIndex + kMaxRunwayLaneScan);
                for (int i = pathIndex; i < end; i++)
                {
                    if (TakeRunwayDirection(path[i].m_Target, path[i].m_TargetDelta, ref scan))
                    {
                        return scan;
                    }
                }
                return scan;
            }

            /// <summary>Returns true when done: a runway lane gave the direction, or the path left the runway first.</summary>
            private bool TakeRunwayDirection(Entity lane, float2 delta, ref FlightScan scan)
            {
                if (RunwayOfLane(lane) != scan.m_Upcoming)
                {
                    // The path leaves the runway before any runway lane proper: it only crosses it.
                    scan.m_Crossing = true;
                    return true;
                }
                if (!m_CarLaneData.TryGetComponent(lane, out Game.Net.CarLane carLane) || (carLane.m_Flags & Game.Net.CarLaneFlags.Runway) == 0
                    || !m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    return false;
                }
                float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                scan.m_EntryDirection = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                return true;
            }

            /// <summary>Returns true when the scan is finished: the next runway was found or the distance limit passed.</summary>
            private bool VisitLane(Entity lane, float2 delta, float limit, ref Entity skip, ref float distance, ref FlightScan scan)
            {
                Entity runway = RunwayOfLane(lane);
                bool hasCurve = m_CurveData.TryGetComponent(lane, out Curve curve);
                if (runway == Entity.Null)
                {
                    // Once the path leaves the runway the airplane is on, a later lane of that runway is a new use.
                    skip = Entity.Null;
                }
                else if (runway != skip)
                {
                    scan.m_Upcoming = runway;
                    scan.m_Distance = distance;
                    if (hasCurve)
                    {
                        scan.m_EntryPosition = MathUtils.Position(curve.m_Bezier, delta.x);
                        float2 tangent = MathUtils.Tangent(curve.m_Bezier, delta.x).xz;
                        scan.m_EntryDirection = math.normalizesafe(math.select(tangent, -tangent, delta.y < delta.x));
                    }
                    return true;
                }
                if (hasCurve)
                {
                    distance += curve.m_Length * math.abs(delta.y - delta.x);
                }
                return distance > limit;
            }

        }
    }
}
