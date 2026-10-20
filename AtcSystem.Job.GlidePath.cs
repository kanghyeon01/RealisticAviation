using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    public partial class AtcSystem
    {
        private partial struct AtcJob
        {
            // ------------------------------------------------------------ straight-in final and glide path (option)
            // The game keeps arrivals on the airway at 1,000 m up to about 2 km beside the runway, then turns them onto a short
            // final and dives at the climb angle (20 degrees). With the option on, an arrival leaves the airway there for the
            // final approach fix (FAF): on the extended centerline, 1,500 ft above the runway, as far out as the glide path
            // needs. The game moves an approaching airplane onto its runway lane once it is within some 350 m of its steering
            // target, so from the FAF the target runs ahead of it down the centerline on the glide path, until kRunwayLaneDistance
            // out; there the game takes it onto the runway lane and flies the rest itself, its target height only lowered to the
            // glide path.

            /// <summary>Height of the final approach fix (and of the traffic pattern) above the runway.</summary>
            private const float kFinalFixHeight = kPatternHeight;

            /// <summary>The final course is joined this many turn radii outside the FAF, so the airplane is lined up by the fix.</summary>
            private const float kJoinLead = 0.25f;

            /// <summary>Flying straight to the join point needs at least this many turn radii of room behind it.</summary>
            private const float kJoinMinDistance = 0.75f;

            /// <summary>Straight to the join point from at most 45 degrees off the final course (tan 45).</summary>
            private const float kJoinConeTan = 1f;

            /// <summary>Otherwise via a waypoint this many turn radii from the join point, 30 degrees off the course.</summary>
            private const float kJoinWaypointDistance = 1.5f;
            private const float kJoinAngleCos = 0.866f;
            private const float kJoinAngleSin = 0.5f;

            /// <summary>
            /// The arrival is handed to its runway lane this far from the threshold, about where the game itself does it.
            /// On the runway lane an airplane reserves the lanes ahead within its braking distance (the runway exit and the
            /// taxiways to its gate), and ground traffic can't enter lanes crossing or merging into reserved ones. Handed over
            /// at the FAF, each arrival held that reservation for a minute or more, and a departure leaving a stand by the
            /// runway exit couldn't move between arrivals.
            /// </summary>
            private const float kRunwayLaneDistance = 2500f;

            /// <summary>On the final course the steering target runs this far ahead of the airplane, beyond the game's lane switch range.</summary>
            private const float kFinalLead = 1500f;

            /// <summary>Closer than this to its target the game moves an approaching airplane onto the runway lane (with a margin).</summary>
            private const float kLaneSwitchRange = 300f;

            /// <summary>Holds when the height to lose exceeds this share of what kSteeringSlope allows on the way to the join point.</summary>
            private const float kDescentHoldMargin = 0.8f;

            /// <summary>Leaves the descent hold within this height above the FAF height...</summary>
            private const float kDescentHoldExitHeight = 30f;

            /// <summary>...heading within 45 degrees of the final course...</summary>
            private const float kDescentHoldExitAlignment = 0.7f;

            /// <summary>...outside the fix, at most this many turn radii off the course.</summary>
            private const float kDescentHoldExitOffset = 1.5f;

            /// <summary>Inside the hand-over point, "established" means at most this far off the centerline...</summary>
            private const float kEstablishedLateral = 300f;

            /// <summary>...and at most this far above the glide path.</summary>
            private const float kEstablishedHeight = 150f;

            /// <summary>How high the game flies airplanes on airways, above the ground (AircraftNavigationSystem.UpdateTargetHeight).</summary>
            private const float kAirwayHeight = 1000f;

            /// <summary>Distance of the final approach fix from the threshold: where the glide path is 1,500 ft up, or two turns.</summary>
            private static float GetFinalFixDistance(float turnRadius, float glideTan)
            {
                return math.max(kFinalFixHeight / glideTan, turnRadius * 2f);
            }

            /// <summary>Steers arrivals the tower is not flying itself (holding, pattern) to the FAF, and down the glide path.</summary>
            private void ApplyGlidePath(in FlightScan scan)
            {
                if (!scan.m_Flying || scan.m_TakingOff || scan.m_PathUnknown)
                {
                    return;
                }
                Entity entity = scan.m_Entity;
                if (!m_Flights.TryGetValue(entity, out Flight flight) || !flight.m_Arrival || flight.m_Holding || flight.m_InCircuit)
                {
                    return;
                }
                AtcSettings settings = SettingsForRunway(flight.m_Runway);
                if (!settings.m_GlidePath)
                {
                    return;
                }
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                AircraftLaneFlags flags = currentLane.m_LaneFlags;
                if ((flags & AircraftLaneFlags.Landing) != 0)
                {
                    if (scan.m_Occupied == flight.m_Runway && m_CurveData.TryGetComponent(currentLane.m_Lane, out Curve curve))
                    {
                        FollowGlidePath(entity, MathUtils.Position(curve.m_Bezier, currentLane.m_CurvePosition.x), settings.m_GlideTan);
                    }
                }
                else if ((flags & (AircraftLaneFlags.Approaching | AircraftLaneFlags.Airway)) == (AircraftLaneFlags.Approaching | AircraftLaneFlags.Airway))
                {
                    FlyToFinalFix(entity, flight, settings.m_GlideTan);
                }
            }

            /// <summary>
            /// The end of the airway: instead of the game's turn onto a short final, join the final course just outside the FAF
            /// at an angle, as a controller vectors an arrival onto the localizer. From inside a cone behind the join point
            /// (at most 45 degrees off the course) the airplane flies straight to it; from anywhere else it first flies to a
            /// waypoint on its own side, 30 degrees off the course, and turns in from there. The game switches to the runway lane
            /// near the target, and then follows the centerline itself.
            /// </summary>
            private void FlyToFinalFix(Entity entity, in Flight flight, float glideTan)
            {
                float radius = GetTurnRadius(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab]);
                float3 threshold = flight.m_EntryPosition;
                float2 course = flight.m_EntryDirection;
                float distance = GetFinalFixDistance(radius, glideTan);
                float height = threshold.y + math.min(kFinalFixHeight, distance * glideTan);
                float2 outward = -course;
                float2 join = threshold.xz + outward * (distance + radius * kJoinLead);

                // The direction is always the final course: the game may switch to the runway lane near either target, and
                // from then on it places its target along this direction, ahead of the airplane (without it, on the touchdown point).
                float3 direction = new float3(course.x, 0f, course.y);
                float2 side = MathUtils.Right(course);
                float2 position = m_TransformData[entity].m_Position.xz;
                if (FlyDescentHold(entity, flight, new float3(join.x, height, join.y), course, radius))
                {
                    return;
                }

                // On the final course (or turning onto it, within 45 degrees of a target kFinalLead ahead): fly down the
                // centerline on the glide path, still on the airway, and reach the runway lane only kRunwayLaneDistance out.
                float2 fromThreshold = position - threshold.xz;
                float outbound = math.dot(fromThreshold, outward);
                float offCenter = math.abs(math.dot(fromThreshold, side));
                // Inside the hand-over point only one already established (on the centerline, near the glide path) that the
                // game hasn't switched yet: one coming in high beside the runway (out of a hold, say) dived at 50 degrees to
                // a target on the glide path close ahead. It goes out to the FAF instead, as before.
                // Anywhere: no higher than kEstablishedHeight above the glide path (level at the FAF height beyond the FAF).
                // One coming off the airway end high (900 m, 2.8 km out) was given the glide path height at the hand-over
                // point and dived at 60 degrees; it goes out to the FAF instead.
                bool low = m_TransformData[entity].m_Position.y
                    <= threshold.y + math.min(kFinalFixHeight, math.max(outbound, 0f) * glideTan) + kEstablishedHeight;
                bool established = low && offCenter <= kFinalLead * kJoinConeTan
                    && (outbound > kRunwayLaneDistance || (outbound > 0f && offCenter <= kEstablishedLateral));
                if (distance > kRunwayLaneDistance && outbound <= distance + radius * kJoinLead + kFinalLead && established)
                {
                    // Down to the hand-over point; past it (not switched for some reason), a target close ahead lets the game switch.
                    float ahead = outbound > kRunwayLaneDistance
                        ? math.max(outbound - kFinalLead, kRunwayLaneDistance)
                        : math.max(outbound - kLaneSwitchRange, 0f);
                    float2 point = threshold.xz + outward * ahead;
                    Steer(entity, new float3(point.x, threshold.y + math.min(kFinalFixHeight, ahead * glideTan), point.y), direction);
                    return;
                }

                float2 fromJoin = position - join;
                float along = math.dot(fromJoin, outward);
                float lateral = math.dot(fromJoin, side);
                if (along > radius * kJoinMinDistance && math.abs(lateral) <= along * kJoinConeTan)
                {
                    Steer(entity, new float3(join.x, height, join.y), direction);
                    return;
                }
                float2 waypoint = join + outward * (radius * kJoinWaypointDistance * kJoinAngleCos)
                    + side * (lateral >= 0f ? 1f : -1f) * (radius * kJoinWaypointDistance * kJoinAngleSin);
                Steer(entity, new float3(waypoint.x, height, waypoint.y), direction);
            }

            /// <summary>
            /// Flight distance of an airborne arrival at an airport flying the glide path, before its runway lane: to the
            /// final course join point and down the final. The game's path ends beside the runway and knows nothing of the
            /// way out to the FAF, so its estimate stayed at some 50 s while a cleared arrival flew out there; the next arrival
            /// could join the sequence only once that one had landed. False where the glide path isn't flown.
            /// </summary>
            private bool TryGetGlideRouteDistance(Entity entity, in Flight flight, out float routeDistance)
            {
                routeDistance = 0f;
                AtcSettings settings = SettingsForRunway(flight.m_Runway);
                if (!settings.m_GlidePath || !flight.m_Arrival || math.lengthsq(flight.m_EntryDirection) < 0.5f)
                {
                    return false;
                }
                float radius = GetTurnRadius(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab]);
                float2 threshold = flight.m_EntryPosition.xz;
                float2 join = threshold - flight.m_EntryDirection * (GetFinalFixDistance(radius, settings.m_GlideTan) + radius * kJoinLead);
                float2 position = m_TransformData[entity].m_Position.xz;
                // Already on the final course inside the join point: straight down it.
                float outbound = math.dot(position - threshold, -flight.m_EntryDirection);
                bool onFinal = !flight.m_DescentHold && outbound > 0f && outbound < math.distance(join, threshold)
                    && math.abs(math.dot(position - threshold, MathUtils.Right(flight.m_EntryDirection))) < kEstablishedLateral;
                routeDistance = onFinal ? outbound : math.distance(position, join) + math.distance(join, threshold);
                return true;
            }

            /// <summary>
            /// Too high for the glide path: the arrival descends in a hold at the join point (the hold in lieu of a procedure
            /// turn of an RNAV "T" approach, AIM 5-4, used "for course alignment and/or descent"), a standard right-hand
            /// racetrack with the final course as its inbound leg, at the FAF height. It holds once it can't lose its height
            /// at kSteeringSlope on the way to the join point, and leaves on the inbound leg, lined up and down to the FAF
            /// height, straight onto final. Returns true while it holds.
            /// </summary>
            private bool FlyDescentHold(Entity entity, Flight flight, float3 fix, float2 course, float radius)
            {
                float3 position = m_TransformData[entity].m_Position;
                float excess = position.y - fix.y;
                if (!flight.m_DescentHold)
                {
                    // Not for a little height (one just out of the hold, on its way to the fix, must not go back in).
                    if (excess <= math.max(math.distance(position.xz, fix.xz) * kSteeringSlope * kDescentHoldMargin, kEstablishedHeight))
                    {
                        return false;
                    }
                    flight.m_DescentHold = true;
                    flight.m_DescentPattern = new HoldingPattern(fix, course, m_Settings.m_HoldingLegLength, radius);
                    m_Flights[entity] = flight;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} {excess:F0} m too high for the glide path: descending in the hold at the final course join point");
                    }
                }
                HoldingPattern pattern = flight.m_DescentPattern;
                float2 fromFix = position.xz - pattern.m_Fix.xz;
                float2 heading = math.normalizesafe(m_MovingData[entity].m_Velocity.xz);
                // Turning inbound or on the inbound leg, outside the fix: the final course is joined from there (FlyToFinalFix).
                // The hold is flown wider than drawn, so a tight "on the inbound leg" test was never met and it held forever.
                bool inbound = math.dot(heading, course) > kDescentHoldExitAlignment
                    && math.dot(fromFix, course) < 0f && math.abs(math.dot(fromFix, pattern.m_Right)) < radius * kDescentHoldExitOffset;
                if (excess <= kDescentHoldExitHeight && inbound)
                {
                    flight.m_DescentHold = false;
                    m_Flights[entity] = flight;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} leaves the descent hold onto final");
                    }
                    return false;
                }
                Steer(entity, pattern.GetSteeringTarget(position, radius + GetSpeed(entity) * 2f), new float3(course.x, 0f, course.y));
                // As for any hold: the game must not move it along its path meanwhile (AtcPreNavigationSystem).
                m_HoldingFlights.Add(entity);
                return true;
            }

            /// <summary>
            /// On the runway lane the game aims along the centerline, a little ahead of the airplane, no higher than its climb
            /// angle line from the touchdown point; and it sets the lowest pitch so that the airplane keeps up with that line
            /// (no higher than the airway). Both are redone here with the glide path angle.
            /// </summary>
            private void FollowGlidePath(Entity entity, float3 touchdown, float glideTan)
            {
                AircraftNavigation navigation = m_NavigationData[entity];
                // Never steeper than the game's own approach (its climb angle): one that reached the runway lane high (the game
                // switched it on its own) descends as the game would, instead of diving at 60 degrees for the glide path.
                float3 airplane = m_TransformData[entity].m_Position;
                float steepest = airplane.y - math.distance(navigation.m_TargetPosition.xz, airplane.xz)
                    * math.tan(m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab].m_ClimbAngle);
                navigation.m_TargetPosition.y = math.min(navigation.m_TargetPosition.y,
                    math.max(touchdown.y + math.distance(navigation.m_TargetPosition.xz, touchdown.xz) * glideTan, steepest));

                // As AircraftNavigationSystem does for an approach, with the glide path angle instead of the climb angle.
                Entity prefab = m_PrefabRefData[entity].m_Prefab;
                float climbAngle = m_PrefabAirplaneData[prefab].m_ClimbAngle;
                float3 position = m_TransformData[entity].m_Position;
                float3 toTarget = navigation.m_TargetPosition - position;
                float beyond = math.max(0f, math.length(toTarget.xz) - m_PrefabAircraftData[prefab].m_GroundMaxSpeed - navigation.m_MaxSpeed * kTimeStep - 1f);
                float climb = math.min(toTarget.y + beyond * glideTan, touchdown.y + kAirwayHeight - position.y);
                navigation.m_MinClimbAngle = math.atan(climb * 0.02f) * climbAngle / (math.PI / 2f);
                m_NavigationData[entity] = navigation;
            }
        }
    }
}
