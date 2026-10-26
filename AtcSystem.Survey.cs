using System.Collections.Generic;
using Colossal.Entities;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Transform = Game.Objects.Transform;

namespace RealisticAviation
{
    /// <summary>
    /// The lowest heights of one runway end's approach procedure that clear the obstacles under it, in metres above the
    /// threshold; 0 where nothing asks more than the procedure's own height. As a procedure designer would, the tower flies
    /// the IF, the IAFs and the STAR holds at least this high (the FAF stays where the glide path puts it).
    /// </summary>
    internal struct ProcedureFloor
    {
        /// <summary>Intermediate segment (IF -> FAF): highest obstacle plus the intermediate MOC where it stands.</summary>
        public float m_Intermediate;

        /// <summary>Initial segments (IAF -> IF), left and right of the course.</summary>
        public float m_InitialLeft;
        public float m_InitialRight;

        /// <summary>The STAR holds at the left and right IAF and at the IF (straight in).</summary>
        public float m_HoldLeft;
        public float m_HoldRight;
        public float m_HoldStraight;
    }

    /// <summary>The floors of both ends of a runway: along and against its geometry's direction.</summary>
    internal struct ProcedureFloors
    {
        public ProcedureFloor m_Along;
        public ProcedureFloor m_Against;
    }

    public partial class AtcSystem
    {
        // The procedures' obstacle survey, done again whenever the runways change and once a game hour (the city grows).
        private const uint kSurveyFrames = 262144 / 24;

        // Terrain is sampled this far apart over the areas.
        private const float kSurveyStep = 200f;

        // Buildings lower than this above the threshold can't raise a procedure (its lowest MOC is above it): left out.
        private const float kSurveyMinTop = 50f;

        private NativeParallelHashMap<Entity, ProcedureFloors> m_ProcedureFloors;
        private EntityQuery m_BuildingQuery;
        private JobHandle m_SurveyJob;
        private bool m_SurveyPending;
        private bool m_SurveyDue = true;
        private uint m_SurveyFrame;

        // Not right after a load, while the terrain may still be coming in.
        private uint m_SurveyNotBefore;
        private const uint kSurveyLoadDelay = 256;
        private NativeArray<SurveyEnd> m_SurveyEnds;
        private NativeArray<float4> m_SurveyTops;
        private NativeArray<ProcedureFloor> m_SurveyResults;
        private readonly Dictionary<Entity, float> m_PrefabTops = new Dictionary<Entity, float>();

        private struct SurveyEnd
        {
            public Entity m_Runway;
            public bool m_Along;
            public float3 m_Threshold;
            public float2 m_Course;
            public float m_GlideTan;

            /// <summary>How far round its fix a STAR hold reaches (the leg plus a turn diameter).</summary>
            public float m_HoldRadius;
            public float m_InitialMoc;
            public float m_IntermediateMoc;
            public float m_Primary;
            public float m_Secondary;
        }

        /// <summary>
        /// For each runway end: the procedure's fixes (as AtcJob.GetApproachFixes), then over each segment's areas (and round
        /// each holding fix) the highest obstacle or terrain plus the MOC it needs there. Primary area: the full MOC; secondary
        /// area outside it: tapering to 0 at its edge (PANS-OPS), so obstacles beside the track count too.
        /// </summary>
        private struct SurveyJob : IJob
        {
            [ReadOnly] public TerrainHeightData m_Terrain;
            [ReadOnly] public NativeArray<SurveyEnd> m_Ends;

            /// <summary>Building tops: x, top height (world), z.</summary>
            [ReadOnly] public NativeArray<float4> m_Tops;
            public NativeArray<ProcedureFloor> m_Floors;

            public void Execute()
            {
                for (int i = 0; i < m_Ends.Length; i++)
                {
                    SurveyEnd end = m_Ends[i];
                    float2 outward = -end.m_Course;
                    float segment = AtcJob.kProcedureRadius * AtcJob.kSegmentRadii;
                    float2 faf = end.m_Threshold.xz + outward * AtcJob.GetFinalFixDistance(AtcJob.kProcedureRadius, end.m_GlideTan);
                    float2 fix = faf + outward * segment;
                    float2 right = new float2(end.m_Course.y, -end.m_Course.x);
                    float2 iafLeft = fix - right * segment, iafRight = fix + right * segment;
                    m_Floors[i] = new ProcedureFloor
                    {
                        m_Intermediate = Segment(end, fix, faf, end.m_IntermediateMoc),
                        m_InitialLeft = Segment(end, iafLeft, fix, end.m_InitialMoc),
                        m_InitialRight = Segment(end, iafRight, fix, end.m_InitialMoc),
                        m_HoldLeft = Round(end, iafLeft, end.m_InitialMoc),
                        m_HoldRight = Round(end, iafRight, end.m_InitialMoc),
                        m_HoldStraight = Round(end, fix, end.m_InitialMoc),
                    };
                }
            }

            /// <summary>The MOC an obstacle needs this far beside the track, or a negative value outside the areas.</summary>
            private static float MocAt(in SurveyEnd end, float lateral, float moc)
            {
                if (lateral <= end.m_Primary)
                {
                    return moc;
                }
                return lateral <= end.m_Primary + end.m_Secondary ? moc * (1f - (lateral - end.m_Primary) / end.m_Secondary) : -1f;
            }

            /// <summary>Highest obstacle plus its MOC over the areas of the segment a -> b, above the threshold (at least 0).</summary>
            private float Segment(in SurveyEnd end, float2 a, float2 b, float moc)
            {
                float2 along = b - a;
                float length = math.length(along);
                float2 unit = along / math.max(length, 1f), side = new float2(-unit.y, unit.x);
                float width = end.m_Primary + end.m_Secondary, floor = 0f;
                for (float s = 0f; s <= length; s += kSurveyStep)
                {
                    for (float l = -width; l <= width; l += kSurveyStep)
                    {
                        float2 p = a + unit * s + side * l;
                        float need = MocAt(end, math.abs(l), moc);
                        floor = math.max(floor, TerrainUtils.SampleHeight(ref m_Terrain, new float3(p.x, 0f, p.y)) + need - end.m_Threshold.y);
                    }
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float2 p = m_Tops[i].xz;
                    float t = math.clamp(math.dot(p - a, unit), 0f, length);
                    float need = MocAt(end, math.distance(p, a + unit * t), moc);
                    if (need >= 0f)
                    {
                        floor = math.max(floor, m_Tops[i].y + need - end.m_Threshold.y);
                    }
                }
                return floor;
            }

            /// <summary>The same round a holding fix: inside the hold's reach the full MOC, then the areas outward.</summary>
            private float Round(in SurveyEnd end, float2 fix, float moc)
            {
                float reach = end.m_HoldRadius + end.m_Primary + end.m_Secondary, floor = 0f;
                for (float x = -reach; x <= reach; x += kSurveyStep)
                {
                    for (float z = -reach; z <= reach; z += kSurveyStep)
                    {
                        float2 p = fix + new float2(x, z);
                        float need = MocAt(end, math.max(0f, math.length(new float2(x, z)) - end.m_HoldRadius), moc);
                        if (need >= 0f)
                        {
                            floor = math.max(floor, TerrainUtils.SampleHeight(ref m_Terrain, new float3(p.x, 0f, p.y)) + need - end.m_Threshold.y);
                        }
                    }
                }
                for (int i = 0; i < m_Tops.Length; i++)
                {
                    float need = MocAt(end, math.max(0f, math.distance(m_Tops[i].xz, fix) - end.m_HoldRadius), moc);
                    if (need >= 0f)
                    {
                        floor = math.max(floor, m_Tops[i].y + need - end.m_Threshold.y);
                    }
                }
                return floor;
            }
        }

        /// <summary>Collects a finished survey into the floors, or starts one when due (runways changed, or a game hour on).</summary>
        private void UpdateSurvey()
        {
            if (m_SurveyPending)
            {
                if (!m_SurveyJob.IsCompleted)
                {
                    return;
                }
                m_SurveyJob.Complete();
                m_SurveyPending = false;
                // The tower job reads the floors: it has to be done before they change.
                m_JobHandle.Complete();
                m_ProcedureFloors.Clear();
                for (int i = 0; i < m_SurveyEnds.Length; i++)
                {
                    SurveyEnd end = m_SurveyEnds[i];
                    ProcedureFloor floor = m_SurveyResults[i];
                    m_ProcedureFloors.TryGetValue(end.m_Runway, out ProcedureFloors floors);
                    if (end.m_Along)
                    {
                        floors.m_Along = floor;
                    }
                    else
                    {
                        floors.m_Against = floor;
                    }
                    m_ProcedureFloors[end.m_Runway] = floors;
                    LogFloor(end, floor);
                }
                DisposeSurvey();
            }
            uint frame = m_SimulationSystem.frameIndex;
            if ((int)(frame - m_SurveyNotBefore) < 0 || (!m_SurveyDue && frame - m_SurveyFrame < kSurveyFrames))
            {
                return;
            }
            m_SurveyDue = false;
            m_SurveyFrame = frame;
            StartSurvey();
        }

        private void StartSurvey()
        {
            List<SurveyEnd> ends = new List<SurveyEnd>();
            for (int i = 0; i < m_RunwayList.Count; i++)
            {
                RunwayInfo runway = m_RunwayList[i];
                RunwayGeometry geometry = runway.Geometry;
                byte used = RunwayNames.UsedEnds(geometry);
                for (int k = 0; k < 2; k++)
                {
                    bool along = k == 0;
                    if ((used & (along ? RunwayGeometry.kAlong : RunwayGeometry.kAgainst)) == 0)
                    {
                        continue;
                    }
                    RunwayEndInfo info = ToEndInfo(runway, along ? geometry.m_Direction : -geometry.m_Direction);
                    OperatingValues values = GetOperatingValues(runway.Airport);
                    ends.Add(new SurveyEnd
                    {
                        m_Runway = runway.Key,
                        m_Along = along,
                        m_Threshold = info.Start,
                        m_Course = info.Direction,
                        m_GlideTan = math.tan(math.radians(values.GlidePathDegrees)),
                        m_HoldRadius = values.HoldingLegLengthMeters + 2f * AtcJob.kProcedureRadius,
                        m_InitialMoc = values.InitialMocFeet * 0.3048f,
                        m_IntermediateMoc = values.IntermediateMocFeet * 0.3048f,
                        m_Primary = values.ObstacleAreaPrimaryMeters,
                        m_Secondary = math.max(1f, values.ObstacleAreaSecondaryMeters),
                    });
                }
            }
            if (ends.Count == 0)
            {
                m_JobHandle.Complete();
                m_ProcedureFloors.Clear();
                return;
            }

            // Building tops (world height), the prefab's geometry looked up once per prefab.
            NativeArray<Transform> transforms = m_BuildingQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            NativeArray<PrefabRef> prefabs = m_BuildingQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            NativeList<float4> tops = new NativeList<float4>(256, Allocator.Temp);
            for (int i = 0; i < transforms.Length; i++)
            {
                Entity prefab = prefabs[i].m_Prefab;
                if (!m_PrefabTops.TryGetValue(prefab, out float height))
                {
                    height = EntityManager.TryGetComponent(prefab, out ObjectGeometryData geometry) ? geometry.m_Bounds.max.y : 0f;
                    m_PrefabTops[prefab] = height;
                }
                if (height >= kSurveyMinTop)
                {
                    float3 position = transforms[i].m_Position;
                    tops.Add(new float4(position.x, position.y + height, position.z, 0f));
                }
            }
            transforms.Dispose();
            prefabs.Dispose();

            m_SurveyEnds = new NativeArray<SurveyEnd>(ends.Count, Allocator.Persistent);
            for (int i = 0; i < ends.Count; i++)
            {
                m_SurveyEnds[i] = ends[i];
            }
            m_SurveyTops = new NativeArray<float4>(tops.AsArray(), Allocator.Persistent);
            tops.Dispose();
            m_SurveyResults = new NativeArray<ProcedureFloor>(ends.Count, Allocator.Persistent);
            TerrainSystem terrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_SurveyJob = new SurveyJob
            {
                m_Terrain = terrainSystem.GetHeightData(),
                m_Ends = m_SurveyEnds,
                m_Tops = m_SurveyTops,
                m_Floors = m_SurveyResults,
            }.Schedule();
            terrainSystem.AddCPUHeightReader(m_SurveyJob);
            m_SurveyPending = true;
        }

        private void LogFloor(in SurveyEnd end, in ProcedureFloor floor)
        {
            if (!m_LastSettings.m_DetailedLog)
            {
                return;
            }
            int Feet(float metres) => (int)math.round(metres / 0.3048f);
            Mod.Log.Info($"Procedure survey for runway {end.m_Runway} ({(end.m_Along ? "along" : "against")}): minimum IF {Feet(math.max(floor.m_Intermediate, math.max(floor.m_InitialLeft, floor.m_InitialRight)))} ft, "
                + $"IAF {Feet(floor.m_InitialLeft)} / {Feet(floor.m_InitialRight)} ft, holds {Feet(floor.m_HoldLeft)} / {Feet(floor.m_HoldRight)} / {Feet(floor.m_HoldStraight)} ft above the threshold");
        }

        private void DisposeSurvey()
        {
            if (m_SurveyEnds.IsCreated)
            {
                m_SurveyEnds.Dispose();
            }
            if (m_SurveyTops.IsCreated)
            {
                m_SurveyTops.Dispose();
            }
            if (m_SurveyResults.IsCreated)
            {
                m_SurveyResults.Dispose();
            }
        }
    }
}
