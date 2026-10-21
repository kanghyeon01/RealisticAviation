using Colossal.Mathematics;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
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
            // ------------------------------------------------------------ standard instrument departure (SID)
            // Right after takeoff the game hands the airplane to its airway, often pointing back past the airport, and turns it
            // around in a few seconds (up to 30 degrees per second, climbing at 60 degrees). Here the departure is flown as a
            // SID instead: a standard rate turn, then direct to a fix on its airway kDepartureFixDistance ahead ("climb runway
            // heading, then direct ..."). The steering target is put a little off the nose, so the heading changes by at most
            // kDepartureTurnRate. Once pointing at the fix, the game takes over with that fix as its target, so departures from
            // one runway to one airway fly the same track. (Joining the airway's own course instead failed on airways made of
            // short zigzag lanes: each bend was chased in turn and the airplane weaved for minutes.)

            /// <summary>
            /// Turn rate limit. A standard rate turn (3 degrees per second) at the speed the game shows has a radius of about
            /// 1.4 km; airplanes move at twice the shown speed, so the same circle over the city takes twice the rate.
            /// </summary>
            private const float kDepartureTurnRate = 6f;

            /// <summary>Steepest climb while turning (the game climbed at up to 60 degrees in its turn).</summary>
            private const float kDepartureClimbAngle = 15f;

            /// <summary>Departures are watched this long after takeoff.</summary>
            private const float kDepartureTurnSeconds = 180f;

            /// <summary>The steering target is put this many seconds of flight ahead of the airplane.</summary>
            private const float kDepartureLeadSeconds = 4f;

            /// <summary>Turns up to this many degrees towards the fix are left to the game.</summary>
            private const float kDepartureHandBackAngle = 10f;

            /// <summary>
            /// Pointing within this many degrees of the fix, the game takes over. Handed over at 10°, the game turned the rest
            /// at its own rate (15-18°/s for some airliners) in half a second: a visible jerk at the end of every turn.
            /// </summary>
            private const float kDepartureAlignedAngle = 2f;

            /// <summary>
            /// A turn under way stops (to fly straight on) only once the fix is this far inside the turning circle, as a share
            /// of its radius; a straight leg turns again once the fix is outside. With one threshold (and a circle 30% too
            /// big) a fix near the edge switched turn and straight every tick: the airplane twitched and turned at half rate.
            /// </summary>
            private const float kDepartureCircleInside = 0.85f;

            /// <summary>Least climb gradient up to the airway height: 200 ft per NM.</summary>
            private const float kDepartureMinClimbGradient = 200f * 0.3048f / 1852f;

            /// <summary>Path elements looked at past the queued lanes for the fix.</summary>
            private const int kDepartureCourseLanes = 8;

            /// <summary>The SID's fix: this far along the airway from the point of it closest to the airplane.</summary>
            private const float kDepartureFixDistance = 4000f;

            /// <summary>Further fixes tried this far apart, up to kDepartureFixMaxDistance, until one can be reached.</summary>
            private const float kDepartureFixStep = 500f;
            private const float kDepartureFixMaxDistance = 15000f;

            /// <summary>A fix counts as reachable only this far outside the turning circle (as a share of what it takes).</summary>
            private const float kDepartureReachMargin = 1.15f;

            /// <summary>Once handed over at its fix, the tower steps in again only for a bend sharper than this.</summary>
            private const float kDepartureRejoinAngle = 30f;

            /// <summary>A SID still turning after this long hands over at its fix anyway.</summary>
            private const float kDepartureMaxSeconds = 90f;

            private void SmoothDepartureTurn(in FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                bool active = m_DepartureTurns.TryGetValue(entity, out DepartureTurn departureTurn);
                float3 gameTarget = departureTurn.m_GameTarget;
                // A path being updated (often as the game moves on to its next lane) doesn't end a turn under way: handed back then,
                // the game got its old target back and swung towards it before the SID took over again.
                if (!m_Settings.m_SmoothDepartureTurns || !scan.m_Flying || scan.m_TakingOff || (scan.m_PathUnknown && !active)
                    || !m_Zones.TryGetValue(entity, out ZoneState zone) || zone.m_TakeoffFrame == 0
                    || (m_Frame - zone.m_TakeoffFrame) * kSecondsPerFrame > kDepartureTurnSeconds
                    || (m_Flights.TryGetValue(entity, out Flight flight) && (flight.m_Arrival || flight.m_Holding || flight.m_InCircuit)))
                {
                    if (active)
                    {
                        HandBackDepartureTurn(entity, gameTarget);
                    }
                    return;
                }
                AircraftNavigation navigation = m_NavigationData[entity];
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                float3 position = m_TransformData[entity].m_Position;
                float3 velocity = m_MovingData[entity].m_Velocity;
                float speed = math.length(velocity.xz);
                float2 actual = math.normalizesafe(velocity.xz, math.normalizesafe(math.forward(m_TransformData[entity].m_Rotation).xz));
                // The heading the tower has the airplane turning through, kept from tick to tick. Stepping from the actual
                // heading instead made the airplane turn the step quickly and then fly straight until the next tick: the turn
                // rate and bank shook between 3 and 5 degrees per second.
                float2 heading = active ? departureTurn.m_Heading : actual;
                // The airplane can't turn faster than its own rate (AircraftMoveSystem: m_FlyingTurning radians per second).
                AirplaneData airplaneData = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab];
                float rate = math.min(math.radians(kDepartureTurnRate), airplaneData.m_FlyingTurning);
                // The game lags a little behind (about 5 instead of 6 degrees per second); held within two steps of the
                // actual heading, the target never runs so far ahead that the game turns hard to catch up.
                float lag = math.atan2(actual.x * heading.y - actual.y * heading.x, math.dot(actual, heading));
                float maxLag = 2f * rate * kTimeStep;
                if (math.abs(lag) > maxLag)
                {
                    heading = Rotate(actual, math.sign(lag) * maxLag);
                }
                float step = rate * kTimeStep;
                float radius = speed / rate;
                if (!active)
                {
                    gameTarget = navigation.m_TargetPosition;
                    if (zone.m_SidJoined)
                    {
                        // Handed over already: the game flies to the fix and on along its airway; the tower steps in again only
                        // when the game itself turns sharply. (Judged by a new fix 4 km on instead, a bend just past the fix
                        // started a turn the other way right after the hand-over.)
                        float2 toGameTarget = math.normalizesafe(gameTarget.xz - position.xz, actual);
                        if (math.abs(math.atan2(actual.x * toGameTarget.y - actual.y * toGameTarget.x, math.dot(actual, toGameTarget)))
                            <= math.radians(kDepartureRejoinAngle))
                        {
                            return;
                        }
                    }
                    // Planned for the speed it reaches on its airway: it accelerates from 150 to some 240 m/s in the turn, and
                    // the turning circle grows from 1.4 to 2.3 km.
                    float planRadius = math.max(speed, airplaneData.m_FlyingSpeed.y) / rate;
                    if (!FindDepartureFix(entity, currentLane, position.xz, actual, planRadius, out departureTurn.m_FixLane, out departureTurn.m_FixT, out departureTurn.m_Fix))
                    {
                        return;
                    }
                }
                float3 fix = departureTurn.m_Fix;

                float2 toFix = fix.xz - position.xz;
                float fixDistance = math.length(toFix);
                float2 desired = math.normalizesafe(toFix, heading);
                float angle = math.atan2(heading.x * desired.y - heading.y * desired.x, math.dot(heading, desired));
                if (!active)
                {
                    // Nothing sharp ahead: the game flies on. After the hand-over the game flies the airway's own bends.
                    if (math.abs(angle) <= math.radians(zone.m_SidJoined ? kDepartureRejoinAngle : kDepartureHandBackAngle))
                    {
                        return;
                    }
                }
                else if (!scan.m_PathUnknown && (math.abs(math.atan2(actual.x * desired.y - actual.y * desired.x, math.dot(actual, desired))) <= math.radians(kDepartureAlignedAngle)
                    || fixDistance < radius || (m_Frame - departureTurn.m_StartFrame) * kSecondsPerFrame > kDepartureMaxSeconds))
                {
                    // Pointing at the fix (or, as safety nets, close to it or turning for too long): the game takes over with the
                    // fix as its target. A new fix was once picked here instead, from the airway point nearest the airplane, and
                    // that lay behind it: the airplane reversed its turn.
                    HandOverAtFix(entity, ref currentLane, departureTurn.m_FixLane, departureTurn.m_FixT);
                    zone.m_SidJoined = true;
                    m_Zones[entity] = zone;
                    HandBackDepartureTurn(entity, new float3(fix.x, gameTarget.y, fix.z));
                    return;
                }

                // A fix inside the circle the turn would fly can't be reached by turning: fly on straight until it is outside.
                float turn = math.sign(angle) * math.min(step, math.abs(angle));
                float2 side = math.select(-1f, 1f, angle > 0f) * new float2(-heading.y, heading.x);
                float centerToFix = math.distance(position.xz + side * radius, fix.xz);
                departureTurn.m_Straight = departureTurn.m_Straight ? centerToFix < radius : centerToFix < radius * kDepartureCircleInside;
                if (departureTurn.m_Straight)
                {
                    turn = 0f;
                }
                heading = Rotate(heading, turn);
                // The target one more step round, so that the airplane is still turning when the next tick comes.
                float2 direction = Rotate(heading, turn);

                float lead = math.max(speed * kDepartureLeadSeconds, 200f);
                float reach = lead * math.tan(math.radians(kDepartureClimbAngle));
                float3 target = new float3(position.x + direction.x * lead, math.clamp(gameTarget.y, position.y - reach, position.y + reach),
                    position.z + direction.y * lead);
                float climbAngle = m_PrefabAirplaneData[m_PrefabRefData[entity].m_Prefab].m_ClimbAngle;
                navigation.m_TargetPosition = target;
                navigation.m_TargetDirection = default;
                // Same climb rule the game uses for airplanes that aren't landing, but never less than the SID's 200 ft/NM
                // while below the airway.
                navigation.m_MinClimbAngle = math.atan((target.y - position.y) * 0.02f) * climbAngle / (math.PI * 0.5f);
                if (target.y > position.y + 10f)
                {
                    navigation.m_MinClimbAngle = math.max(navigation.m_MinClimbAngle, math.atan(kDepartureMinClimbGradient));
                }
                m_NavigationData[entity] = navigation;
                if (!active)
                {
                    departureTurn.m_StartFrame = m_Frame;
                    if (m_Settings.m_DetailedLog)
                    {
                        Mod.Log.Info($"{entity} SID: turn of {math.degrees(angle):F0} deg flown at {math.degrees(rate):F1} deg/s, direct to a fix {fixDistance:F0} m away");
                    }
                }
                departureTurn.m_GameTarget = gameTarget;
                departureTurn.m_Heading = heading;
                departureTurn.m_Tick = m_Tick;
                m_DepartureTurns[entity] = departureTurn;
            }

            /// <summary>
            /// The SID's fix: the first point along the airway, from kDepartureFixDistance on (in steps of kDepartureFixStep, up
            /// to kDepartureFixMaxDistance), that a turn of <paramref name="radius"/> from the present heading can reach, i.e. that
            /// lies outside the turning circle on its side. The airway is followed from its point closest to the airplane, over
            /// the current lane and the airway lanes after it (queued, then on the path), skipping lanes the airplane is already
            /// abeam past the end of. A fix 4 km out lay inside the turning circle of a 150° turn at airway speed: the airplane
            /// circled it. False without an airway lane (the game flies on).
            /// </summary>
            private bool FindDepartureFix(Entity entity, in AircraftCurrentLane currentLane, float2 position, float2 heading, float radius,
                out Entity fixLane, out float fixT, out float3 fix)
            {
                fixLane = Entity.Null;
                fixT = 0f;
                fix = default;
                DynamicBuffer<AircraftNavigationLane> queued = m_NavigationLanes[entity];
                PathOwner pathOwner = m_PathOwnerData[entity];
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                if (!GetAirwayChainLane(currentLane, 0, queued, pathOwner, path, out Entity lane, out Curve curve, out float2 delta))
                {
                    return false;
                }
                int k = 0;
                float t;
                while (IsPastLaneEnd(curve, delta, position, out t)
                    && GetAirwayChainLane(currentLane, k + 1, queued, pathOwner, path, out Entity nextLane, out Curve nextCurve, out float2 nextDelta))
                {
                    k++;
                    lane = nextLane;
                    curve = nextCurve;
                    delta = nextDelta;
                }
                float walked = 0f;
                float check = kDepartureFixDistance;
                for (int next = k + 1; ; next++)
                {
                    float length = curve.m_Length * math.abs(delta.y - t);
                    while (walked + length >= check)
                    {
                        fixLane = lane;
                        fixT = t + math.sign(delta.y - delta.x) * (check - walked) / math.max(curve.m_Length, 1f);
                        fix = MathUtils.Position(curve.m_Bezier, fixT);
                        if (IsReachable(position, heading, radius, fix.xz) || check >= kDepartureFixMaxDistance)
                        {
                            return true;
                        }
                        check += kDepartureFixStep;
                    }
                    walked += length;
                    if (!GetAirwayChainLane(currentLane, next, queued, pathOwner, path, out Entity nextLane, out Curve nextCurve, out float2 nextDelta))
                    {
                        // The airway ends before: its end.
                        fixLane = lane;
                        fixT = delta.y;
                        fix = MathUtils.Position(curve.m_Bezier, delta.y);
                        return true;
                    }
                    lane = nextLane;
                    curve = nextCurve;
                    delta = nextDelta;
                    t = delta.x;
                }
            }

            /// <summary>
            /// A point a turn of this radius can reach: outside the turning circle on its side (distance at least twice the
            /// radius × sine of the bearing off the heading), with a margin.
            /// </summary>
            private static bool IsReachable(float2 position, float2 heading, float radius, float2 point)
            {
                float2 to = point - position;
                float distance = math.length(to);
                float sine = math.abs(heading.x * to.y - heading.y * to.x) / math.max(distance, 1f);
                return distance >= 2f * radius * sine * kDepartureReachMargin;
            }

            /// <summary>
            /// Gives the game the SID's fix as the point of its path to fly to: the lane state moves on to the fix's lane at the
            /// fix (as the airway fast-forward does). Nothing changes when the game has already gone past that lane.
            /// </summary>
            private void HandOverAtFix(Entity entity, ref AircraftCurrentLane currentLane, Entity fixLane, float fixT)
            {
                DynamicBuffer<AircraftNavigationLane> queued = m_NavigationLanes[entity];
                PathOwner pathOwner = m_PathOwnerData[entity];
                DynamicBuffer<PathElement> path = m_PathElements[entity];
                for (int k = 0; GetAirwayChainLane(currentLane, k, queued, pathOwner, path, out Entity lane, out _, out float2 delta); k++)
                {
                    if (lane != fixLane)
                    {
                        continue;
                    }
                    if (k == 0)
                    {
                        currentLane.m_CurvePosition.x = fixT;
                        currentLane.m_CurvePosition.y = delta.y >= fixT ? math.max(currentLane.m_CurvePosition.y, fixT) : math.min(currentLane.m_CurvePosition.y, fixT);
                    }
                    else
                    {
                        AdvanceDepartureLane(entity, ref currentLane, lane, delta, fixT, k <= queued.Length ? 1 : 2,
                            k <= queued.Length ? k - 1 : pathOwner.m_ElementIndex + k - 1 - queued.Length);
                    }
                    m_CurrentLaneData[entity] = currentLane;
                    return;
                }
            }

            /// <summary>
            /// Lane <paramref name="k"/> of the airway ahead: 0 the current lane, then the queued navigation lanes, then the path
            /// elements (up to kDepartureCourseLanes). False where the airway ends (or the lane has no length).
            /// </summary>
            private bool GetAirwayChainLane(in AircraftCurrentLane currentLane, int k, DynamicBuffer<AircraftNavigationLane> queued, in PathOwner pathOwner,
                DynamicBuffer<PathElement> path, out Entity lane, out Curve curve, out float2 delta)
            {
                curve = default;
                if (k == 0)
                {
                    lane = currentLane.m_Lane;
                    delta = currentLane.m_CurvePosition.xz;
                }
                else if (k <= queued.Length)
                {
                    AircraftNavigationLane next = queued[k - 1];
                    lane = next.m_Lane;
                    delta = next.m_CurvePosition;
                    if ((next.m_Flags & AircraftLaneFlags.Airway) == 0 || (next.m_Flags & AircraftLaneFlags.TransformTarget) != 0)
                    {
                        return false;
                    }
                }
                else
                {
                    int i = pathOwner.m_ElementIndex + k - 1 - queued.Length;
                    if (i >= path.Length || i >= pathOwner.m_ElementIndex + kDepartureCourseLanes)
                    {
                        lane = Entity.Null;
                        delta = default;
                        return false;
                    }
                    lane = path[i].m_Target;
                    delta = path[i].m_TargetDelta;
                    if (!m_ConnectionLaneData.TryGetComponent(lane, out Game.Net.ConnectionLane connection) || (connection.m_Flags & ConnectionLaneFlags.Airway) == 0)
                    {
                        return false;
                    }
                }
                return m_CurveData.TryGetComponent(lane, out curve) && math.abs(delta.y - delta.x) > 0.001f;
            }

            /// <summary>The closest point of the lane's part (<paramref name="delta"/>) to the position; true when that is its end.</summary>
            private static bool IsPastLaneEnd(in Curve curve, float2 delta, float2 position, out float t)
            {
                MathUtils.Distance(curve.m_Bezier.xz, position, out t);
                t = math.clamp(t, math.min(delta.x, delta.y), math.max(delta.x, delta.y));
                return math.abs(t - delta.y) <= 0.001f;
            }

            /// <summary>Moves the airplane's lane state on to a later airway lane it joined (as TryFastForwardAirway does).</summary>
            private void AdvanceDepartureLane(Entity entity, ref AircraftCurrentLane currentLane, Entity lane, float2 delta, float t, int source, int index)
            {
                Entity fromLane = currentLane.m_Lane;
                currentLane.m_Lane = lane;
                currentLane.m_CurvePosition = new float3(t, t, delta.y);
                currentLane.m_LaneFlags = AircraftLaneFlags.Airway | (currentLane.m_LaneFlags & AircraftLaneFlags.Flying);
                DynamicBuffer<AircraftNavigationLane> queued = m_NavigationLanes[entity];
                if (source == 1)
                {
                    queued.RemoveRange(0, index + 1);
                }
                else
                {
                    queued.Clear();
                    PathOwner pathOwner = m_PathOwnerData[entity];
                    pathOwner.m_ElementIndex = index + 1;
                    m_PathOwnerData[entity] = pathOwner;
                }
                if (m_Settings.m_DetailedLog)
                {
                    Mod.Log.Info($"{entity} SID: joined the airway on a later lane, {fromLane} -> {lane} ({(source == 1 ? "queued lane " : "path element ")}{index})");
                }
            }

            private static float2 Rotate(float2 vector, float angle)
            {
                math.sincos(angle, out float sin, out float cos);
                return new float2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
            }

            /// <summary>Forgets turns of airplanes that weren't seen this tick (deleted).</summary>
            private void EndDepartureTurnTick()
            {
                if (m_DepartureTurns.IsEmpty)
                {
                    return;
                }
                NativeList<Entity> stale = new NativeList<Entity>(Allocator.Temp);
                foreach (var pair in m_DepartureTurns)
                {
                    if (pair.Value.m_Tick != m_Tick)
                    {
                        stale.Add(pair.Key);
                    }
                }
                for (int i = 0; i < stale.Length; i++)
                {
                    m_DepartureTurns.Remove(stale[i]);
                }
                stale.Dispose();
            }

            /// <summary>Hands the airplane back with this steering target: the game's own, or a point on the course just ahead.</summary>
            private void HandBackDepartureTurn(Entity entity, float3 gameTarget)
            {
                m_DepartureTurns.Remove(entity);
                if (m_NavigationData.HasComponent(entity))
                {
                    AircraftNavigation navigation = m_NavigationData[entity];
                    navigation.m_TargetPosition = gameTarget;
                    m_NavigationData[entity] = navigation;
                }
            }
        }
    }
}
