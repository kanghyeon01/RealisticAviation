using Colossal.Mathematics;
using Game.Net;
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
            // ------------------------------------------------------------ departure turns
            // Right after takeoff the game hands the airplane to its airway, often pointing back past the airport, and turns it
            // around in a few seconds (up to 30 degrees per second, climbing at 60 degrees). Here the turn is flown at a
            // standard rate instead: the steering target is put a little off the nose, so the heading changes by at most
            // kDepartureTurnRate, towards the airway course (the game's lane), which the airplane joins at up to 45 degrees.
            // Once on it, the game takes over from a point on the course just ahead.

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

            /// <summary>Within this many degrees of the game's target the game finishes the turn itself.</summary>
            private const float kDepartureHandBackAngle = 10f;

            /// <summary>The turning circle used to tell whether the target can be reached, as a share of speed / rate.</summary>
            private const float kDepartureCircleMargin = 1.3f;

            /// <summary>Steepest cut towards the airway course, reached one turning radius or more off it.</summary>
            private const float kDepartureInterceptAngle = 45f;

            /// <summary>Within this distance of the course (and lined up with it) the airplane is handed back.</summary>
            private const float kDepartureOnCourse = 150f;

            private void SmoothDepartureTurn(in FlightScan scan)
            {
                Entity entity = scan.m_Entity;
                bool active = m_DepartureTurns.TryGetValue(entity, out DepartureTurn departureTurn);
                float3 gameTarget = departureTurn.m_GameTarget;
                if (!m_Settings.m_SmoothDepartureTurns || !scan.m_Flying || scan.m_TakingOff || scan.m_PathUnknown
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
                if (!active)
                {
                    gameTarget = navigation.m_TargetPosition;
                }
                float3 position = m_TransformData[entity].m_Position;
                float3 velocity = m_MovingData[entity].m_Velocity;
                float speed = math.length(velocity.xz);
                float2 actual = math.normalizesafe(velocity.xz, math.normalizesafe(math.forward(m_TransformData[entity].m_Rotation).xz));
                // The heading the tower has the airplane turning through, kept from tick to tick. Stepping from the actual
                // heading instead made the airplane turn the step quickly and then fly straight until the next tick: the turn
                // rate and bank shook between 3 and 5 degrees per second.
                float2 heading = active ? departureTurn.m_Heading : actual;
                // The game lags a little behind (about 5 instead of 6 degrees per second); held within two steps of the
                // actual heading, the target never runs so far ahead that the game turns hard to catch up.
                float lag = math.atan2(actual.x * heading.y - actual.y * heading.x, math.dot(actual, heading));
                float maxLag = 2f * math.radians(kDepartureTurnRate) * kTimeStep;
                if (math.abs(lag) > maxLag)
                {
                    heading = Rotate(actual, math.sign(lag) * maxLag);
                }
                float rate = math.radians(kDepartureTurnRate);
                float step = rate * kTimeStep;
                float radius = speed / rate * kDepartureCircleMargin;

                // The airway course: the game's lane from its target on. The airplane joins the course itself, as a controller
                // vectors an airplane onto a course, cutting towards it at up to 45 degrees in proportion to how far off it is.
                // (Flying to the game's target point instead overshot it in the wide turn; with the point then inside the turning
                // circle the airplane flew straight before turning again, a teardrop around the point.)
                AircraftCurrentLane currentLane = m_CurrentLaneData[entity];
                bool onCourse = false;
                float courseT = 0f;
                float2 desired;
                float2 course = default;
                Curve curve = default;
                if (m_CurveData.TryGetComponent(currentLane.m_Lane, out curve) && math.abs(currentLane.m_CurvePosition.z - currentLane.m_CurvePosition.x) > 0.001f)
                {
                    float from = currentLane.m_CurvePosition.x;
                    float to = currentLane.m_CurvePosition.z;
                    MathUtils.Distance(curve.m_Bezier.xz, position.xz, out courseT);
                    courseT = math.clamp(courseT, math.min(from, to), math.max(from, to));
                    if (math.abs(courseT - to) > 0.001f)
                    {
                        float2 tangent = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, courseT).xz);
                        course = to >= from ? tangent : -tangent;
                        float offCourse = math.dot(position.xz - MathUtils.Position(curve.m_Bezier, courseT).xz, MathUtils.Right(course));
                        // Right of the course: turn left onto it, and the other way round.
                        desired = Rotate(course, math.clamp(offCourse / radius, -1f, 1f) * math.radians(kDepartureInterceptAngle));
                        onCourse = math.abs(offCourse) < kDepartureOnCourse;
                    }
                    else
                    {
                        // The lane ends before the course could be joined: to its end, as to any point.
                        desired = math.normalizesafe(MathUtils.Position(curve.m_Bezier, to).xz - position.xz, heading);
                    }
                }
                else
                {
                    desired = math.normalizesafe(gameTarget.xz - position.xz, heading);
                }
                float angle = math.atan2(heading.x * desired.y - heading.y * desired.x, math.dot(heading, desired));
                float handBack = math.radians(kDepartureHandBackAngle);
                if (math.abs(angle) <= handBack)
                {
                    if (!active)
                    {
                        // Nothing sharp ahead: the game flies on.
                        return;
                    }
                    if (onCourse && math.dot(actual, course) > math.cos(handBack))
                    {
                        // On the course: the game takes over from a point on it just ahead, as if it had flown the turn itself.
                        float ahead = math.max(speed * kDepartureLeadSeconds, 200f) / math.max(curve.m_Length, 1f);
                        float to = currentLane.m_CurvePosition.z;
                        float t = to >= currentLane.m_CurvePosition.x ? math.min(courseT + ahead, to) : math.max(courseT - ahead, to);
                        currentLane.m_CurvePosition.x = t;
                        currentLane.m_CurvePosition.y = to >= t ? math.max(currentLane.m_CurvePosition.y, t) : math.min(currentLane.m_CurvePosition.y, t);
                        m_CurrentLaneData[entity] = currentLane;
                        float3 point = MathUtils.Position(curve.m_Bezier, t);
                        HandBackDepartureTurn(entity, new float3(point.x, gameTarget.y, point.z));
                        return;
                    }
                    if (math.lengthsq(course) < 0.5f)
                    {
                        // Point mode (no course to join): pointing at it, the game flies on from here.
                        HandBackDepartureTurn(entity, gameTarget);
                        return;
                    }
                }

                // A point (the end of the lane) inside the circle the turn would fly can't be reached by turning: fly on
                // straight until it is outside. Joining a course needs no such care.
                float turn = math.sign(angle) * math.min(step, math.abs(angle));
                if (math.lengthsq(course) < 0.5f)
                {
                    float2 point = math.abs(courseT - currentLane.m_CurvePosition.z) <= 0.001f && curve.m_Length > 0f
                        ? MathUtils.Position(curve.m_Bezier, currentLane.m_CurvePosition.z).xz
                        : gameTarget.xz;
                    float2 side = math.select(-1f, 1f, angle > 0f) * new float2(-heading.y, heading.x);
                    if (math.distance(position.xz + side * radius, point) < radius)
                    {
                        turn = 0f;
                    }
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
                // Same climb rule the game uses for airplanes that aren't landing.
                navigation.m_MinClimbAngle = math.atan((target.y - position.y) * 0.02f) * climbAngle / (math.PI * 0.5f);
                m_NavigationData[entity] = navigation;
                if (!active && m_Settings.m_DetailedLog)
                {
                    Mod.Log.Info($"{entity} departure turn of {math.degrees(angle):F0} deg flown at {kDepartureTurnRate:F0} deg/s");
                }
                m_DepartureTurns[entity] = new DepartureTurn { m_GameTarget = gameTarget, m_Heading = heading, m_Tick = m_Tick };
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
