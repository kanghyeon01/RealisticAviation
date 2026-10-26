using Colossal.Entities;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// One runway end's instrument approach as the tower flies it, for the IFR chart: the "T" (both IAFs, the IF, the FAF),
    /// the missed approach (the go-around pattern drawn with the procedures' reference turn radius) and the STAR holds.
    /// World positions; y of the fixes is the height the tower flies them at, y of the rest the threshold's.
    /// </summary>
    internal struct ApproachChartData
    {
        public Entity m_Airport;
        public Entity m_RunwayEntity;
        public bool m_Along;
        public string m_Runway;
        public float3 m_Threshold;

        /// <summary>The far end of the runway (the missed approach and the SIDs start there).</summary>
        public float3 m_RunwayEnd;
        public float2 m_Course;
        public float3 m_IafLeft;
        public float3 m_IafRight;
        public float3 m_If;
        public float3 m_Faf;

        /// <summary>Missed approach: upwind end, crosswind end, downwind end (abeam the FAF), at the pattern height.</summary>
        public float3 m_Upwind;
        public float3 m_Crosswind;
        public float3 m_Downwind;

        /// <summary>
        /// Missed approach hold: not cleared by the end of downwind, the airplane extends it (2 NM) and holds there at pattern
        /// height, the downwind direction inbound (AtcJob.EndCircuit).
        /// </summary>
        public float3 m_MissedHold;
        public float2 m_MissedInbound;

        /// <summary>The go-around pattern's turn radius (its fly-by arcs): the widest airplane type's, as the tower flies it.</summary>
        public float m_TurnRadius;

        /// <summary>STAR holds: the lowest level at the left and right IAF and at the IF (world height), and the leg length.</summary>
        public float m_HoldLeft;
        public float m_HoldRight;
        public float m_HoldStraight;
        public float m_HoldLeg;

        /// <summary>The segments' minimum altitudes from the obstacle survey (world height): both initial and the intermediate.</summary>
        public float m_FloorInitialLeft;
        public float m_FloorInitialRight;
        public float m_FloorIntermediate;
    }

    /// <summary>The turn radii the IFR chart draws with (<see cref="AtcSystem.GetChartRadii"/>).</summary>
    internal struct ChartRadii
    {
        public float m_Hold;
        public float m_SidTurn;
        public float m_SidPlan;
    }

    public partial class AtcSystem
    {
        /// <summary>
        /// Straight ahead past the runway end before a SID turns (the game's takeoff phase ends about there; the turn is the
        /// tower's from then on, AtcJob.SmoothDepartureTurn).
        /// </summary>
        internal const float kSidStraight = 460f;

        private EntityQuery m_OutsideConnectionQuery;

        /// <summary>The procedures' reference turn radius (their fixes; the obstacle areas round the holds).</summary>
        internal const float kProcedureTurnRadius = AtcJob.kProcedureRadius;

        /// <summary>The approach of one runway end (<paramref name="along"/> its geometry's direction or against it).</summary>
        internal ApproachChartData GetApproachChart(RunwayInfo runway, bool along, float patternRadius)
        {
            RunwayGeometry geometry = runway.Geometry;
            float2 course = along ? geometry.m_Direction : -geometry.m_Direction;
            RunwayEndInfo info = ToEndInfo(runway, course);
            OperatingValues values = GetOperatingValues(runway.Airport);
            float glideTan = math.tan(math.radians(values.GlidePathDegrees));
            m_JobHandle.Complete();
            m_ProcedureFloors.TryGetValue(runway.Key, out ProcedureFloors floors);
            ProcedureFloor floor = along ? floors.m_Along : floors.m_Against;
            float3 threshold = info.Start;
            AtcJob.GetApproachFixes(threshold, course, glideTan, floor, -1, out float3 iafLeft, out float3 intermediate, out float3 faf);
            AtcJob.GetApproachFixes(threshold, course, glideTan, floor, 1, out float3 iafRight, out _, out _);

            // The go-around pattern as AtcJob.GoAround lays it out, with the airplane's own radius (the widest type's,
            // patternRadius): upwind past the runway end, crosswind out to the pattern width, downwind back
            // abeam the FAF.
            float radius = patternRadius > 0f ? patternRadius : AtcJob.kProcedureRadius;
            float length = info.LengthMeters;
            float2 side = info.RightHandPattern ? MathUtils.Right(course) : MathUtils.Left(course);
            float width = math.max(values.PatternWidthMeters, radius * 2.2f);
            float upwind = length + math.max(AtcJob.kUpwindExtension, radius);
            float final = AtcJob.GetFinalFixDistance(radius, glideTan);
            float altitude = threshold.y + AtcJob.kPatternHeight;
            float2 up = threshold.xz + course * upwind;
            float2 cross = up + side * width;
            float2 down = threshold.xz - course * final + side * width;
            float2 end = threshold.xz + course * length;
            float2 downwindDirection = math.normalizesafe(down - cross, -course);
            float2 extension = down + downwindDirection * AtcJob.kDownwindExtension;

            // STAR holds as AtcJob.StartHolding stacks them: the class band, raised over the obstacles round the fix.
            float holdBase = m_Airspaces.TryGetValue(runway.Airport, out AirportSpace space) ? space.HoldBase : 0f;
            return new ApproachChartData
            {
                m_Airport = runway.Airport,
                m_RunwayEntity = runway.Key,
                m_Along = along,
                m_Runway = info.Name,
                m_Threshold = threshold,
                m_RunwayEnd = new float3(end.x, threshold.y, end.y),
                m_Course = course,
                m_IafLeft = iafLeft,
                m_IafRight = iafRight,
                m_If = intermediate,
                m_Faf = faf,
                m_Upwind = new float3(up.x, altitude, up.y),
                m_Crosswind = new float3(cross.x, altitude, cross.y),
                m_Downwind = new float3(down.x, altitude, down.y),
                m_MissedHold = new float3(extension.x, altitude, extension.y),
                m_MissedInbound = downwindDirection,
                m_TurnRadius = radius,
                m_HoldLeft = threshold.y + math.max(holdBase, floor.m_HoldLeft),
                m_HoldRight = threshold.y + math.max(holdBase, floor.m_HoldRight),
                m_HoldStraight = threshold.y + math.max(holdBase, floor.m_HoldStraight),
                m_HoldLeg = values.HoldingLegLengthMeters,
                m_FloorInitialLeft = threshold.y + floor.m_InitialLeft,
                m_FloorInitialRight = threshold.y + floor.m_InitialRight,
                m_FloorIntermediate = threshold.y + floor.m_Intermediate,
            };
        }

        /// <summary>
        /// The IAF an arrival from <paramref name="position"/> holds at and flies the procedure from, as
        /// AtcJob.ChooseApproachSide picks it (the TAA areas): +1 / -1 the right / left IAF, 0 straight in to the IF.
        /// </summary>
        internal static int ApproachSide(in ApproachChartData data, float2 position)
        {
            float2 fromIf = position - data.m_If.xz;
            if (math.dot(fromIf, -data.m_Course) > 0f)
            {
                return 0;
            }
            return math.dot(fromIf, MathUtils.Right(data.m_Course)) >= 0f ? 1 : -1;
        }

        /// <summary>Where an arrival towards the airport from <paramref name="from"/> first calls: the airspace's contact ring.</summary>
        internal bool TryGetContactPoint(Entity airport, float2 from, out float2 point, out float2 center)
        {
            point = center = default;
            if (!m_Airspaces.TryGetValue(airport, out AirportSpace space))
            {
                return false;
            }
            center = space.m_Center;
            point = center + math.normalizesafe(from - center) * space.ContactDistance;
            return true;
        }

        /// <summary>The positions of the city's airplane outside connections (at the map edge).</summary>
        internal void GetAirplaneConnections(List<float2> positions)
        {
            positions.Clear();
            // The outside connection objects whose prefab takes air transfers (the game's own airplane connections).
            if (m_OutsideConnectionQuery == default)
            {
                m_OutsideConnectionQuery = GetEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<Game.Objects.OutsideConnection>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                    None = new[] { ComponentType.ReadOnly<Game.Common.Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
                });
            }
            NativeArray<Entity> connections = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            for (int i = 0; i < connections.Length; i++)
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(connections[i]).m_Prefab;
                if (!EntityManager.TryGetComponent(prefab, out OutsideConnectionData data) || (data.m_Type & OutsideConnectionTransferType.Air) == 0)
                {
                    continue;
                }
                float2 position = EntityManager.GetComponentData<Game.Objects.Transform>(connections[i]).m_Position.xz;
                bool known = false;
                for (int k = 0; k < positions.Count && !known; k++)
                {
                    known = math.distancesq(positions[k], position) < 1f;
                }
                if (!known)
                {
                    positions.Add(position);
                }
            }
            connections.Dispose();
        }

        /// <summary>The highest holding speed of the airplane types in the game (internal m/s), for the holds' MAX SPD.</summary>
        internal static float GetHighestHoldingSpeed(EntityQuery airplanePrefabs)
        {
            NativeArray<AirplaneData> airplanes = airplanePrefabs.ToComponentDataArray<AirplaneData>(Allocator.Temp);
            float highest = 0f;
            for (int i = 0; i < airplanes.Length; i++)
            {
                highest = math.max(highest, AtcJob.GetHoldingSpeed(airplanes[i]));
            }
            airplanes.Dispose();
            return highest;
        }

        /// <summary>
        /// The turn radii the chart draws the procedures with, of the airplane types that fly them widest (smaller ones fly
        /// inside): holds and the go-around pattern (AtcJob.GetTurnRadius: holding speed over the airplane's turn rate, plus
        /// a margin); the SID's fix chosen at top speed and its turn at its lowest flying speed, both at the SID's turn rate
        /// (AtcJob.SmoothDepartureTurn), of the type with the widest plan.
        /// </summary>
        internal static ChartRadii GetChartRadii(EntityQuery airplanePrefabs)
        {
            NativeArray<AirplaneData> airplanes = airplanePrefabs.ToComponentDataArray<AirplaneData>(Allocator.Temp);
            ChartRadii radii = default;
            for (int i = 0; i < airplanes.Length; i++)
            {
                AirplaneData airplane = airplanes[i];
                radii.m_Hold = math.max(radii.m_Hold, AtcJob.GetTurnRadius(airplane));
                float rate = math.max(math.min(math.radians(AtcJob.kDepartureTurnRate), airplane.m_FlyingTurning), 0.001f);
                float plan = airplane.m_FlyingSpeed.y / rate;
                if (plan > radii.m_SidPlan)
                {
                    radii.m_SidPlan = plan;
                    radii.m_SidTurn = airplane.m_FlyingSpeed.x / rate;
                }
            }
            airplanes.Dispose();
            if (radii.m_Hold <= 0f)
            {
                // No airplane types (shouldn't happen): the procedures' reference radius.
                radii = new ChartRadii { m_Hold = kProcedureTurnRadius, m_SidPlan = kProcedureTurnRadius, m_SidTurn = kProcedureTurnRadius };
            }
            return radii;
        }


        /// <summary>
        /// A SID as AtcJob.SmoothDepartureTurn flies it, with the widest type's radii: straight ahead past the runway end, then a
        /// turn towards the first point on the airway (from <paramref name="airwayStart"/> towards <paramref name="airwayEnd"/>)
        /// 4 km or more beyond its point nearest the turn's start that the turn can reach (AtcJob.FindDepartureFix), then
        /// direct to it. Returns the turn's start, its centre, the point it ends at and the fix; false if the fix can't be
        /// reached by turning (then it is flown straight).
        /// </summary>
        internal static bool GetSid(in ApproachChartData data, in ChartRadii radii, float2 airwayStart, float2 airwayEnd, out float2 start, out float2 center,
            out float2 finish, out float2 fix)
        {
            // The fix is chosen with the circle at top speed, the turn flown from where it starts (lowest flying speed).
            float plan = radii.m_SidPlan, radius = radii.m_SidTurn;
            float2 heading = data.m_Course;
            start = data.m_RunwayEnd.xz + heading * kSidStraight;
            float2 airway = airwayEnd - airwayStart;
            float airwayLength = math.length(airway);
            float2 unit = airway / math.max(airwayLength, 1f);
            float nearest = math.clamp(math.dot(start - airwayStart, unit), 0f, airwayLength);
            fix = airwayEnd;
            for (float check = AtcJob.kDepartureFixDistance; check <= AtcJob.kDepartureFixMaxDistance; check += AtcJob.kDepartureFixStep)
            {
                if (nearest + check >= airwayLength)
                {
                    break;
                }
                fix = airwayStart + unit * (nearest + check);
                if (AtcJob.IsReachable(start, heading, plan, fix))
                {
                    break;
                }
            }
            float2 to = fix - start;
            bool left = heading.x * to.y - heading.y * to.x > 0f;
            center = start + (left ? MathUtils.Left(heading) : MathUtils.Right(heading)) * radius;
            float distance = math.distance(center, fix);
            if (distance <= radius)
            {
                finish = start;
                return false;
            }
            // The tangent point where the heading points at the fix.
            float toFix = math.atan2(fix.y - center.y, fix.x - center.x), offset = math.acos(radius / distance);
            float angle = left ? toFix - offset : toFix + offset;
            finish = center + radius * new float2(math.cos(angle), math.sin(angle));
            return true;
        }
    }
}
