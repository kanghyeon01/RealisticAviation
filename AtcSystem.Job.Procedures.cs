using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        private partial struct AtcJob
        {
            // ------------------------------------------------------------ airway fast-forward
            // The game sets Approaching only when its steering target reaches the end of the airway, and only then moves the
            // airplane onto its runway lane. While the tower steers an airplane the game doesn't advance its airway, so an
            // arrival taken over mid-airway (approach procedures, arrival routes) would never get there. Fast-forwarding
            // moves the airplane's path on to its last airway lane, near its end, the way the game itself advances: the
            // current lane becomes that lane, the queued lanes are dropped and the path index moves past it; the game then
            // queues the runway lane itself and reaches Approaching when the airplane gets close.

            /// <summary>The new current lane starts this far (as a share of the lane) before its end.</summary>
            private const float kFastForwardLaneShare = 0.05f;

            /// <summary>
            /// Moves the airplane's path on to the last airway lane before its runway. Returns false (nothing changed) when
            /// the path holds no such lane, isn't ready, or the airplane is already on it.
            /// </summary>
            private bool TryFastForwardAirway(Entity entity)
            {
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                PathOwner pathOwner = m_PathOwnerData[entity];
                DynamicBuffer<AircraftNavigationLane> queued = m_NavigationLanes[entity];
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                if ((currentLane.m_LaneFlags & AircraftLaneFlags.Airway) == 0
                    || (pathOwner.m_State & (PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Updated)) != 0)
                {
                    LogNoFastForward(entity, "not on an airway, or its path is being updated");
                    return false;
                }

                // The last airway lane before the runway: the current lane, one already queued, or one further along the path.
                // The game queues the next airway lane ahead of time, and an arrival holding near its airport is often on its
                // last airway lane already.
                int source = 0; // 0 current lane, 1 queued lane, 2 path element
                int index = -1;
                Entity lane = currentLane.m_Lane;
                float2 delta = currentLane.m_CurvePosition.xz;
                bool done = false;
                for (int i = 0; i < queued.Length && !done; i++)
                {
                    if ((queued[i].m_Flags & AircraftLaneFlags.Airway) != 0 && (queued[i].m_Flags & AircraftLaneFlags.TransformTarget) == 0)
                    {
                        source = 1;
                        index = i;
                        lane = queued[i].m_Lane;
                        delta = queued[i].m_CurvePosition;
                    }
                    else
                    {
                        done = true;
                    }
                }
                for (int i = pathOwner.m_ElementIndex; i < path.Length && !done; i++)
                {
                    if (m_ConnectionLaneData.TryGetComponent(path[i].m_Target, out ConnectionLane connection)
                        && (connection.m_Flags & ConnectionLaneFlags.Airway) != 0)
                    {
                        source = 2;
                        index = i;
                        lane = path[i].m_Target;
                        delta = path[i].m_TargetDelta;
                    }
                    else
                    {
                        done = true;
                    }
                }
                if (!m_CurveData.TryGetComponent(lane, out Curve curve))
                {
                    LogNoFastForward(entity, "no curve on the last airway lane");
                    return false;
                }
                float start = math.lerp(delta.y, delta.x, kFastForwardLaneShare);
                if (source == 0 && math.abs(delta.y - currentLane.m_CurvePosition.x) <= math.abs(delta.y - start))
                {
                    LogNoFastForward(entity, "already near the end of its last airway lane");
                    return false;
                }
                AircraftLaneFlags before = currentLane.m_LaneFlags;
                Entity fromLane = currentLane.m_Lane;
                currentLane.m_Lane = lane;
                currentLane.m_CurvePosition = new float3(start, start, delta.y);
                currentLane.m_LaneFlags = AircraftLaneFlags.Airway | (before & AircraftLaneFlags.Flying);
                m_CurrentLaneData[entity] = currentLane;
                if (source == 1)
                {
                    queued.RemoveRange(0, index + 1);
                }
                else if (source == 2)
                {
                    queued.Clear();
                    pathOwner.m_ElementIndex = index + 1;
                    m_PathOwnerData[entity] = pathOwner;
                }
                int last = index;

                AircraftNavigation navigation = m_NavigationData[entity];
                float3 point = MathUtils.Position(curve.m_Bezier, start);
                navigation.m_TargetPosition = new float3(point.x, math.max(navigation.m_TargetPosition.y, point.y), point.z);
                navigation.m_TargetDirection = default;
                m_NavigationData[entity] = navigation;
                if (m_Settings.m_DetailedLog)
                {
                    float3 position = m_TransformData[entity].m_Position;
                    Mod.Log.Info($"{entity} fast-forward: lane {fromLane} [{before}] -> {lane} ({(source == 0 ? "same lane" : source == 1 ? "queued lane " + last : "path element " + last + " of " + path.Length)}), "
                        + $"airway end {math.distance(position.xz, MathUtils.Position(curve.m_Bezier, delta.y).xz):F0} m away");
                }
                return true;
            }

            // ------------------------------------------------------------ instrument approach procedure (RNAV "Basic T")
            // Per runway end: the FAF on the extended centerline (as 1.1), the IF kSegmentRadii turn radii further out, and an
            // IAF on each side of the IF, perpendicular to the course, as many radii away (AIM 5-4: "T" design). An arrival is
            // sent to the IAF of its side, or straight to the IF from behind it (the TAA areas), and flies IAF -> IF -> FAF;
            // from the IF on, the 1.1 final logic (join outside the FAF, glide path, runway lane 2.5 km out) takes over.

            /// <summary>
            /// Reference turn radius of the procedures: a standard rate turn at the approach speed. 3°/s at the shown speed is
            /// 6°/s in game seconds (airplanes move at twice the shown speed): internal 150 m/s / 0.1047 rad/s.
            /// </summary>
            internal const float kProcedureRadius = 1430f;

            /// <summary>Initial (IAF -> IF) and intermediate (IF -> FAF) segment lengths, in procedure turn radii.</summary>
            internal const float kSegmentRadii = 3f;

            /// <summary>Height gained per metre outward: intermediate segment 150 ft/NM (2.5 %), initial 250 ft/NM (4 %), TERPS optimums.</summary>
            private const float kIntermediateGradient = 0.025f;
            private const float kInitialGradient = 0.04f;

            /// <summary>
            /// The steering target runs this far ahead along the procedure: under the 1,500 m within which the game moves a
            /// fast-forwarded airplane to Approaching, over the ~350 m within which it would switch to the runway lane.
            /// </summary>
            private const float kProcedureLookAhead = 1000f;

            private const byte kStageNone = 0;
            private const byte kStageToIaf = 1;
            private const byte kStageToIf = 2;
            private const byte kStageFinal = 3;

            /// <summary>The fixes of the arrival's runway end: an IAF on <paramref name="side"/> (+1 / -1 along Right(course)), the IF and the FAF.</summary>
            private void GetApproachFixes(in Flight flight, float glideTan, int side, out float3 iaf, out float3 intermediate, out float3 faf)
            {
                GetApproachFixes(flight.m_EntryPosition, flight.m_EntryDirection, glideTan, GetProcedureFloor(flight), side, out iaf, out intermediate, out faf);
            }

            /// <summary>The fixes of a runway end from its threshold and course (also for the IFR chart, on the main thread).</summary>
            internal static void GetApproachFixes(float3 threshold, float2 course, float glideTan, in ProcedureFloor floor, int side,
                out float3 iaf, out float3 intermediate, out float3 faf)
            {
                float2 outward = -course;
                float segment = kProcedureRadius * kSegmentRadii;
                float finalDistance = GetFinalFixDistance(kProcedureRadius, glideTan);
                float fafHeight = threshold.y + math.min(kFinalFixHeight, finalDistance * glideTan);
                float2 fafXZ = threshold.xz + outward * finalDistance;
                float2 ifXZ = fafXZ + outward * segment;
                float2 iafXZ = ifXZ + MathUtils.Right(course) * (side * segment);
                faf = new float3(fafXZ.x, fafHeight, fafXZ.y);
                // Raised over obstacles as a designer would (the obstacle survey): the IF, one altitude for every arrival,
                // clears the segments it joins, the IAF its own initial segment. The FAF stays on the glide path.
                float initialFloor = side > 0 ? floor.m_InitialRight : floor.m_InitialLeft;
                intermediate = new float3(ifXZ.x, math.max(fafHeight + segment * kIntermediateGradient,
                    threshold.y + math.max(floor.m_Intermediate, math.max(floor.m_InitialLeft, floor.m_InitialRight))), ifXZ.y);
                iaf = new float3(iafXZ.x, math.max(intermediate.y + segment * kInitialGradient, threshold.y + initialFloor), iafXZ.y);
            }

            /// <summary>The obstacle floors of the arrival's runway end (AtcSystem.Survey), or none yet.</summary>
            private ProcedureFloor GetProcedureFloor(in Flight flight)
            {
                if (!m_ProcedureFloors.TryGetValue(flight.m_Runway, out ProcedureFloors floors)
                    || !m_RunwayGeometry.TryGetValue(flight.m_Runway, out RunwayGeometry geometry))
                {
                    return default;
                }
                return math.dot(flight.m_EntryDirection, geometry.m_Direction) >= 0f ? floors.m_Along : floors.m_Against;
            }

            /// <summary>
            /// The arrival's sector, as the TAA areas: behind the line through the IF square to the course it goes straight to
            /// the IF (0); in front of it, to the IAF of its side (+1 or -1).
            /// </summary>
            private int ChooseApproachSide(in Flight flight, float glideTan, float3 position)
            {
                GetApproachFixes(flight, glideTan, 1, out _, out float3 intermediate, out _);
                float2 fromIf = position.xz - intermediate.xz;
                if (math.dot(fromIf, -flight.m_EntryDirection) > 0f)
                {
                    return 0;
                }
                return math.dot(fromIf, MathUtils.Right(flight.m_EntryDirection)) >= 0f ? 1 : -1;
            }

            /// <summary>
            /// Flies a cleared arrival along its approach procedure up to the IF. Returns true while it does; false once it is
            /// past the IF (the final logic flies it from there) or the procedure doesn't apply.
            /// </summary>
            private bool FlyApproachProcedure(Entity entity, ref Flight flight, float glideTan)
            {
                if (flight.m_ProcStage == kStageFinal || math.lengthsq(flight.m_EntryDirection) < 0.5f)
                {
                    return false;
                }
                float3 position = m_TransformData[entity].m_Position;
                if (flight.m_ProcStage == kStageNone)
                {
                    int side = ChooseApproachSide(flight, glideTan, position);
                    flight.m_ProcSide = (sbyte)side;
                    flight.m_ProcStage = side == 0 ? kStageToIf : kStageToIaf;
                    m_Flights[entity] = flight;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} approach procedure: {(side == 0 ? "straight in to the IF" : side > 0 ? "right IAF" : "left IAF")} for runway {flight.m_Runway}");
                    }
                }
                if (!flight.m_FastForwardTried)
                {
                    // The game reaches Approaching (and so the runway lane at the end) only at the end of its airway. Also after a
                    // STAR hold, which starts the procedure at its own stage (ReturnToPath).
                    flight.m_FastForwardTried = true;
                    m_Flights[entity] = flight;
                    TryFastForwardAirway(entity);
                }
                GetApproachFixes(flight, glideTan, flight.m_ProcSide == 0 ? 1 : flight.m_ProcSide, out float3 iaf, out float3 intermediate, out float3 faf);
                bool toIaf = flight.m_ProcStage == kStageToIaf;
                float3 fix = toIaf ? iaf : intermediate;
                float3 next = toIaf ? intermediate : faf;
                float2 heading = math.normalizesafe(m_MovingData[entity].m_Velocity.xz);
                float2 toFix = fix.xz - position.xz;
                float distance = math.length(toFix);

                // Fly-by: the turn onto the next leg starts its lead (radius × tan(half the turn)) before the fix; a fix left
                // behind close by (overshot in the turn) counts as passed too.
                float2 legIn = math.normalizesafe(toFix, heading);
                float2 legOut = math.normalizesafe(next.xz - fix.xz);
                float turn = math.acos(math.clamp(math.dot(legIn, legOut), -1f, 1f));
                float lead = math.max(kProcedureRadius * math.tan(math.min(turn, math.radians(120f)) * 0.5f), 200f);
                if (distance < lead || (distance < 2f * kProcedureRadius && math.dot(heading, toFix) < 0f))
                {
                    flight.m_ProcStage = toIaf ? kStageToIf : kStageFinal;
                    m_Flights[entity] = flight;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} approach procedure: {(toIaf ? "IAF passed" : "IF passed, onto final")} at {position.y - flight.m_EntryPosition.y:F0} m above the runway");
                    }
                    if (!toIaf)
                    {
                        return false;
                    }
                    fix = intermediate;
                    next = faf;
                    toFix = fix.xz - position.xz;
                    distance = math.length(toFix);
                }

                // Target: kProcedureLookAhead along the route, direct to the fix and then along the next leg.
                float2 point = distance > kProcedureLookAhead
                    ? position.xz + toFix / math.max(distance, 1f) * kProcedureLookAhead
                    : fix.xz + math.normalizesafe(next.xz - fix.xz) * (kProcedureLookAhead - distance);
                // Height: down to the fix's height (never climbing for it), at most the game's steering slope (6°). The TERPS
                // maximums (8 % and 5.2 %) are too shallow here: the airway runs some 1,500 m above the runway and the scaled
                // segments are short, so arrivals reached the IF far too high and all went into the descent hold.
                float reach = kProcedureLookAhead * kSteeringSlope;
                float height = math.clamp(fix.y, position.y - reach, math.max(position.y, fix.y));
                height = math.min(height, position.y + reach);
                // Not frozen as a hold is (AtcPreNavigationSystem): after the fast-forward the game must move it on to Approaching.
                Steer(entity, new float3(point.x, height, point.y), default);
                return true;
            }

            /// <summary>
            /// Flight distance left along the approach procedure (to the threshold), or -1 when it isn't flying one. An arrival
            /// still on its airway (waiting, or holding) counts the procedure it will fly once cleared: estimated by the airway
            /// it is beside the runway, so close that the next arrival could join the sequence only once the one ahead landed.
            /// </summary>
            private float ProcedureRouteDistance(Entity entity, in Flight flight, float glideTan)
            {
                float3 position = m_TransformData[entity].m_Position;
                int side = flight.m_ProcSide;
                byte stage = flight.m_ProcStage;
                if (stage == kStageNone)
                {
                    if (flight.m_InCircuit || (m_CurrentLaneData[entity].m_LaneFlags & AircraftLaneFlags.Airway) == 0)
                    {
                        return -1f;
                    }
                    if (!flight.m_StarHold)
                    {
                        side = ChooseApproachSide(flight, glideTan, position);
                    }
                    stage = side == 0 ? kStageToIf : kStageToIaf;
                }
                else if (stage != kStageToIaf && stage != kStageToIf)
                {
                    return -1f;
                }
                GetApproachFixes(flight, glideTan, side == 0 ? 1 : side, out float3 iaf, out float3 intermediate, out float3 faf);
                float toThreshold = math.distance(intermediate.xz, flight.m_EntryPosition.xz);
                float route = stage == kStageToIaf
                    ? math.distance(position.xz, iaf.xz) + math.distance(iaf.xz, intermediate.xz) + toThreshold
                    : math.distance(position.xz, intermediate.xz) + toThreshold;
                // Height it can't lose on the way to the FAF at the steering slope is lost in the descent hold: that much
                // more flight. Without it, arrivals from the top of the stack were sequenced as if they could dive, reached
                // the runway together with the ones behind them, and went around.
                float toFaf = route - math.distance(faf.xz, flight.m_EntryPosition.xz);
                return route + math.max(0f, (position.y - faf.y) / kSteeringSlope - toFaf);
            }

            /// <summary>Why an airway couldn't be fast-forwarded (detailed log).</summary>
            private void LogNoFastForward(Entity entity, string reason)
            {
                if (m_Settings.m_DetailedLog)
                {
                    Mod.Log.Info($"{entity} no fast-forward: {reason}");
                }
            }
        }
    }
}
