using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Draws the charts ticked in the IFR chart legend (<see cref="IfrChartSystem"/>) while the infoview is open. The chart
    /// is built once (lines, symbols, label placement over all the ticked charts together) and replayed every frame; it is
    /// built again when the ticks change or, checked every second, when the procedures move (runways, options, the obstacle
    /// survey, outside connections). Each build first finds the charts' obstacles on a worker thread
    /// (<see cref="IfrChartObstacles"/>), so they are those of the city as it is when the chart opens or changes; the old
    /// chart stays up meanwhile. Waypoint names are given over every chart of the city in list order, ticked or not, so they
    /// don't depend on what is shown.
    /// </summary>
    public partial class IfrChartOverlaySystem : GameSystemBase
    {
        private const int kCheckFrames = 60;

        /// <summary>Obstacles of one chart closer than this are shown as one.</summary>
        private const float kMergeDistance = 1000f;


        /// <summary>Shown knots per internal m/s: the UI shows half the internal speed (see PLAN, speed units).</summary>
        private const float kShownKnots = 0.5f * 1.943844f;

        private ToolSystem m_ToolSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Simulation.WaterSystem m_WaterSystem;
        private Game.Simulation.TerrainSystem m_TerrainSystem;
        private IfrChartSystem m_Charts;
        private AtcSystem m_AtcSystem;
        private EntityQuery m_AirplanePrefabQuery;
        private EntityQuery m_BuildingQuery;
        private readonly IfrChartCanvas m_Canvas = new IfrChartCanvas();
        private readonly IfrProcedureCollector m_Collector = new IfrProcedureCollector();
        private IfrChartObstacles m_Obstacles;

        // The build waiting for its obstacles: the charts and ticks it is for, each search area's chart slot, the results.
        private readonly List<IfrChart> m_BuildCharts = new List<IfrChart>();
        private uint m_BuildShown;
        private float m_BuildSeaLevel;
        private float m_BuildHoldingKnots;
        private float m_BuildHoldRadius;
        private readonly List<int> m_AreaSlots = new List<int>();
        private readonly List<IfrChartObstacles.Found> m_Found = new List<IfrChartObstacles.Found>();
        private readonly Dictionary<int, List<ChartObstacle>> m_SlotObstacles = new Dictionary<int, List<ChartObstacle>>();
        private readonly List<float2> m_Track = new List<float2>();

        private uint m_Shown;
        private ulong m_Signature;
        private int m_Countdown;
        private bool m_WasActive;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_WaterSystem = World.GetOrCreateSystemManaged<Game.Simulation.WaterSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
            m_Charts = World.GetOrCreateSystemManaged<IfrChartSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_AirplanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<AirplaneData>(), ComponentType.ReadOnly<PrefabData>());
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_Obstacles = new IfrChartObstacles();
        }

        protected override void OnDestroy()
        {
            m_Obstacles.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (!IfrChartSystem.IsActive(m_ToolSystem))
            {
                if (m_WasActive)
                {
                    m_Obstacles.Clear();
                    m_Canvas.Clear();
                    m_Signature = 0;
                    m_WasActive = false;
                }
                return;
            }
            IReadOnlyList<IfrChart> charts = m_Charts.Charts;
            uint shown = 0;
            for (int i = 0; i < charts.Count; i++)
            {
                if (m_Charts.IsShown(i))
                {
                    shown |= 1u << i;
                }
            }
            if (!m_WasActive || shown != m_Shown || --m_Countdown <= 0)
            {
                m_Countdown = kCheckFrames;
                m_Shown = shown;
                Refresh(charts, shown);
            }
            m_WasActive = true;
            if (m_Obstacles.IsPending && m_Obstacles.TryCollect(m_Found))
            {
                Build();
            }
            if (m_Canvas.IsEmpty)
            {
                return;
            }
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            m_Canvas.Draw(buffer);
        }

        /// <summary>Works out every procedure of the city's charts; if anything has changed, starts a new build.</summary>
        private void Refresh(IReadOnlyList<IfrChart> charts, uint shown)
        {
            float seaLevel = m_WaterSystem.SeaLevel;
            float holdingKnots = AtcSystem.GetHighestHoldingSpeed(m_AirplanePrefabQuery) * kShownKnots;
            ChartRadii radii = AtcSystem.GetChartRadii(m_AirplanePrefabQuery);
            float holdRadius = radii.m_Hold;
            ulong signature = IfrProcedureCollector.Mix(m_Collector.Collect(m_AtcSystem, charts, radii), shown ^ math.hash(new float4(seaLevel, holdingKnots, holdRadius, charts.Count)));
            if (signature == m_Signature)
            {
                return;
            }
            m_Signature = signature;
            m_Collector.GiveNames();

            m_BuildCharts.Clear();
            for (int i = 0; i < charts.Count; i++)
            {
                m_BuildCharts.Add(charts[i]);
            }
            m_BuildShown = shown;
            m_BuildSeaLevel = seaLevel;
            m_BuildHoldingKnots = holdingKnots;
            m_BuildHoldRadius = holdRadius;
            m_Obstacles.Clear();
            m_AreaSlots.Clear();
            for (int i = 0; i < m_BuildCharts.Count; i++)
            {
                if ((shown & (1u << i)) != 0)
                {
                    AddAreas(i, m_BuildCharts[i], m_Collector.Get(m_BuildCharts[i].m_Airport));
                }
            }
            m_Obstacles.Start(EntityManager, m_BuildingQuery, m_TerrainSystem);
        }

        /// <summary>The obstacle search areas of one ticked chart (docs/PLAN.md, IFR chart, obstacles).</summary>
        private void AddAreas(int slot, in IfrChart chart, AirportProcedures procedures)
        {
            OperatingValues values = m_AtcSystem.GetOperatingValues(chart.m_Airport);
            float initial = values.InitialMocFeet * 0.3048f, intermediate = values.IntermediateMocFeet * 0.3048f;
            IfrChartObstacles.Area area = new IfrChartObstacles.Area
            {
                m_Primary = values.ObstacleAreaPrimaryMeters,
                m_Secondary = values.ObstacleAreaSecondaryMeters,
            };
            switch (chart.m_Kind)
            {
                case IfrChartKind.Approach:
                    for (int e = 0; e < procedures.m_Ends.Count; e++)
                    {
                        ApproachChartData end = procedures.m_Ends[e];
                        if (end.m_RunwayEntity != chart.m_Runway || end.m_Along != chart.m_Along)
                        {
                            continue;
                        }
                        area.m_Base = end.m_Threshold.y;
                        AddSegment(slot, area, end.m_IafLeft.xz, end.m_If.xz, initial);
                        AddSegment(slot, area, end.m_IafRight.xz, end.m_If.xz, initial);
                        AddSegment(slot, area, end.m_If.xz, end.m_Faf.xz, intermediate);
                        area.m_Kind = IfrChartObstacles.AreaKind.Final;
                        area.m_A = end.m_Threshold.xz;
                        area.m_B = -end.m_Course;
                        area.m_Radius = math.distance(end.m_Threshold.xz, end.m_Faf.xz);
                        area.m_GlideTan = math.tan(math.radians(values.GlidePathDegrees));
                        Add(slot, area);
                    }
                    break;
                case IfrChartKind.Star:
                    for (int e = 0; e < procedures.m_Ends.Count; e++)
                    {
                        ApproachChartData end = procedures.m_Ends[e];
                        area.m_Base = end.m_Threshold.y;
                        bool3 used = false;
                        for (int a = 0; a < procedures.m_Airways.Count; a++)
                        {
                            float2 entry = procedures.m_Airways[a].m_Entry;
                            int side = AtcSystem.ApproachSide(end, entry);
                            used[side + 1] = true;
                            AddSegment(slot, area, entry, side < 0 ? end.m_IafLeft.xz : side > 0 ? end.m_IafRight.xz : end.m_If.xz, initial);
                        }
                        for (int side = -1; side <= 1; side++)
                        {
                            if (!used[side + 1])
                            {
                                continue;
                            }
                            area.m_Kind = IfrChartObstacles.AreaKind.Round;
                            area.m_A = side < 0 ? end.m_IafLeft.xz : side > 0 ? end.m_IafRight.xz : end.m_If.xz;
                            area.m_Moc = initial;
                            // As the survey that sets the hold's MNM ALT: the leg plus a turn diameter at the reference radius.
                            area.m_Radius = end.m_HoldLeg + 2f * AtcSystem.kProcedureTurnRadius;
                            Add(slot, area);
                        }
                    }
                    break;
                case IfrChartKind.Sid:
                    for (int a = 0; a < procedures.m_Airways.Count; a++)
                    {
                        for (int e = 0; e < procedures.m_Ends.Count; e++)
                        {
                            ApproachChartData end = procedures.m_Ends[e];
                            IfrCharts.SidTrack(end, procedures.m_Airways[a].m_Sids[e], m_Track);
                            area.m_Kind = IfrChartObstacles.AreaKind.Departure;
                            area.m_Base = end.m_RunwayEnd.y;
                            area.m_PointStart = m_Obstacles.AddPoint(m_Track[0]);
                            for (int k = 1; k < m_Track.Count; k++)
                            {
                                m_Obstacles.AddPoint(m_Track[k]);
                            }
                            area.m_PointCount = m_Track.Count;
                            Add(slot, area);
                        }
                    }
                    break;
            }
        }

        private void AddSegment(int slot, IfrChartObstacles.Area area, float2 a, float2 b, float moc)
        {
            area.m_Kind = IfrChartObstacles.AreaKind.Segment;
            area.m_A = a;
            area.m_B = b;
            area.m_Moc = moc;
            Add(slot, area);
        }

        private void Add(int slot, in IfrChartObstacles.Area area)
        {
            m_Obstacles.AddArea(area);
            m_AreaSlots.Add(slot);
        }

        /// <summary>Builds the chart from the procedures and the obstacles found for it.</summary>
        private void Build()
        {
            // Each chart's obstacles, once each; on a SID chart numbered for the climb notes, the steepest gradient first.
            foreach (List<ChartObstacle> list in m_SlotObstacles.Values)
            {
                list.Clear();
            }
            for (int i = 0; i < m_Found.Count; i++)
            {
                int slot = m_AreaSlots[m_Found[i].m_Area];
                if (!m_SlotObstacles.TryGetValue(slot, out List<ChartObstacle> list))
                {
                    m_SlotObstacles[slot] = list = new List<ChartObstacle>();
                }
                // Obstacles close together are one on the chart, the one that matters most kept (the steepest gradient on a
                // SID, else the highest): the controlling points of neighbouring segments and holds are often samples of the
                // same hill a few hundred metres apart, and their labels covered each other.
                ChartObstacle obstacle = m_Found[i].m_Obstacle;
                bool sid = m_BuildCharts[slot].m_Kind == IfrChartKind.Sid;
                int same = list.FindIndex(o => math.distancesq(o.m_Top.xz, obstacle.m_Top.xz) < kMergeDistance * kMergeDistance);
                if (same < 0)
                {
                    list.Add(obstacle);
                }
                else if (sid ? obstacle.m_Gradient > list[same].m_Gradient : obstacle.m_Top.y > list[same].m_Top.y)
                {
                    list[same] = obstacle;
                }
            }
            m_Canvas.Clear();
            // The whole chart at the holds' scale (they are drawn at the tower's real size), never above the preview's.
            IfrChartCanvas.SetScale(math.min(1f, m_BuildHoldRadius / IfrCharts.kPreviewHoldRadius));
            for (int i = 0; i < m_BuildCharts.Count; i++)
            {
                if ((m_BuildShown & (1u << i)) == 0)
                {
                    continue;
                }
                IfrChart chart = m_BuildCharts[i];
                AirportProcedures procedures = m_Collector.Get(chart.m_Airport);
                m_SlotObstacles.TryGetValue(i, out List<ChartObstacle> obstacles);
                switch (chart.m_Kind)
                {
                    case IfrChartKind.Approach:
                        for (int e = 0; e < procedures.m_Ends.Count; e++)
                        {
                            ApproachChartData end = procedures.m_Ends[e];
                            if (end.m_RunwayEntity == chart.m_Runway && end.m_Along == chart.m_Along)
                            {
                                IfrCharts.BuildApproach(m_Canvas, end, procedures.m_EndNames[e], obstacles, m_BuildHoldingKnots, m_BuildSeaLevel);
                            }
                        }
                        break;
                    case IfrChartKind.Star:
                        IfrCharts.BuildStar(m_Canvas, procedures, obstacles, m_BuildHoldingKnots, m_BuildHoldRadius, m_BuildSeaLevel);
                        break;
                    case IfrChartKind.Sid:
                        if (obstacles != null)
                        {
                            obstacles.Sort((a, b) => b.m_Gradient.CompareTo(a.m_Gradient));
                            for (int k = 0; k < obstacles.Count; k++)
                            {
                                ChartObstacle numbered = obstacles[k];
                                numbered.m_Number = k + 1;
                                obstacles[k] = numbered;
                            }
                        }
                        IfrCharts.BuildSid(m_Canvas, procedures, obstacles, m_BuildSeaLevel);
                        break;
                }
            }
            m_Canvas.Place();
        }
    }
}
