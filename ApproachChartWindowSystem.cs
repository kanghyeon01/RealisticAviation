using System.Collections.Generic;
using System.Globalization;
using Colossal.UI.Binding;
using Game.Common;
using Game.Prefabs;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// The approach chart window (IFR chart, step 3): opened from the airport panel for one runway end ("RNP RWY 07"), it
    /// shows the lower half of an AIP instrument approach chart, as docs/ifr-symbols-preview.html (parts 9-11): the header
    /// (frequencies, elevations), the missed approach text, transition altitude and MSA, the profile, the minima and rate of
    /// descent tables, and the procedure coding tables. Worked out here and sent as one value; RealisticAviation.mjs draws
    /// it. Refreshed every two seconds while open (the procedure can move); the MSA comes from a search on a worker thread.
    /// Every write has the same properties (the UI engine keeps one object layout per type name).
    /// </summary>
    public partial class ApproachChartWindowSystem : UISystemBase
    {
        public const string kBinding = "ApproachChart";
        public const string kCloseTrigger = "CloseApproachChart";
        private const int kRefreshFrames = 120;

        // As the preview (the game has no transition altitude of its own).
        private const string kTransitionAltitude = "14 000";
        private const string kTransitionLevel = "FL 140";

        /// <summary>The MSA clears the highest obstacle in its sector by 1,000 ft (300 m), rounded up to 100 ft.</summary>
        private const float kMsaClearance = 1000f * 0.3048f;

        private const string kNavigationSpecification = "RNP APCH";

        private AtcSystem m_AtcSystem;
        private IfrChartSystem m_Charts;
        private NameSystem m_NameSystem;
        private Game.Simulation.WaterSystem m_WaterSystem;
        private Game.Simulation.TerrainSystem m_TerrainSystem;
        private RawValueBinding m_Binding;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_AirplanePrefabQuery;
        private readonly IfrProcedureCollector m_Collector = new IfrProcedureCollector();
        private IfrChartObstacles m_Msa;
        private readonly List<IfrChartObstacles.Found> m_Found = new List<IfrChartObstacles.Found>();
        private readonly string[] m_MsaText = new string[4];
        private float m_MsaRadius;

        private bool m_Open;
        private bool m_Closed;
        private Entity m_Airport;
        private Entity m_Runway;
        private bool m_Along;
        private int m_Countdown;
        private ChartView m_View;

        /// <summary>What the window shows, worked out once per refresh.</summary>
        private sealed class ChartView
        {
            public string m_Title, m_Runway, m_AirportName, m_Code, m_Tower, m_Approach, m_Missed;
            public int m_Elevation;
            public float m_IafNm, m_IfNm, m_FafNm, m_DaNm;
            public int m_IafAltitude, m_IfAltitude, m_FafAltitude, m_InitialMinimum, m_IntermediateMinimum, m_Da, m_Dh, m_Rdh;
            public string m_IafName, m_IfName, m_FafName;
            public float m_GlideDegrees;
            public string[] m_PathCourses, m_MissedNames, m_MissedCourses;
            public int[] m_RateOfDescent;
            public readonly List<string> m_TableTitles = new List<string>();
            public readonly List<List<string[]>> m_Tables = new List<List<string[]>>();
        }

        private static readonly int[] kKnots = { 60, 90, 120, 150, 180 };

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_Charts = World.GetOrCreateSystemManaged<IfrChartSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_WaterSystem = World.GetOrCreateSystemManaged<Game.Simulation.WaterSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
            m_AirplanePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<AirplaneData>(), ComponentType.ReadOnly<PrefabData>());
            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Buildings.Building>(), ComponentType.ReadOnly<Game.Objects.Transform>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() },
            });
            m_Msa = new IfrChartObstacles();
            AddBinding(m_Binding = new RawValueBinding(Mod.kName, kBinding, Write));
            AddBinding(new TriggerBinding(Mod.kName, kCloseTrigger, Close));
        }

        protected override void OnDestroy()
        {
            m_Msa.Dispose();
            base.OnDestroy();
        }

        /// <summary>Opens the window on the approach to one runway end (from the airport panel).</summary>
        internal void Open(Entity airport, Entity runway, bool along)
        {
            m_Open = true;
            m_Airport = airport;
            m_Runway = runway;
            m_Along = along;
            m_Countdown = 0;
            m_Msa.Clear();
            for (int i = 0; i < 4; i++)
            {
                m_MsaText[i] = "-";
            }
        }

        /// <summary>Closes the window (from its close button; the binding is updated on the next update, outside the trigger).</summary>
        private void Close()
        {
            m_Open = false;
            m_View = null;
            m_Closed = true;
        }

        protected override void OnUpdate()
        {
            if (m_Closed)
            {
                m_Closed = false;
                m_Msa.Clear();
                m_Binding.Update();
            }
            if (!m_Open)
            {
                return;
            }
            if (m_Msa.IsPending && m_Msa.TryCollect(m_Found))
            {
                float seaLevel = m_WaterSystem.SeaLevel;
                for (int i = 0; i < m_Found.Count; i++)
                {
                    int sector = m_Found[i].m_Area;
                    m_MsaText[sector] = IfrCharts.Spaced(IfrCharts.AltitudeFeet(m_Found[i].m_Obstacle.m_Top.y + kMsaClearance, seaLevel));
                }
                m_Binding.Update();
            }
            if (--m_Countdown > 0)
            {
                return;
            }
            m_Countdown = kRefreshFrames;
            m_View = BuildView(out bool found);
            if (!found)
            {
                // The runway end is gone (or flies no approach now): the window closes.
                m_Open = false;
                m_View = null;
            }
            m_Binding.Update();
        }

        /// <summary>Works out the window's contents, and starts the MSA search if it isn't known yet.</summary>
        private ChartView BuildView(out bool found)
        {
            found = false;
            m_Charts.RefreshCharts();
            m_Collector.Collect(m_AtcSystem, m_Charts.Charts, AtcSystem.GetChartRadii(m_AirplanePrefabQuery));
            m_Collector.GiveNames();
            AirportProcedures procedures = m_Collector.Get(m_Airport);
            int index = -1;
            for (int e = 0; procedures != null && e < procedures.m_Ends.Count; e++)
            {
                if (procedures.m_Ends[e].m_RunwayEntity == m_Runway && procedures.m_Ends[e].m_Along == m_Along)
                {
                    index = e;
                }
            }
            if (index < 0 || !m_AtcSystem.GetOperatingValues(m_Airport).InstrumentApproach)
            {
                return null;
            }
            found = true;
            ApproachChartData end = procedures.m_Ends[index];
            IfrCharts.ApproachNames names = procedures.m_EndNames[index];
            OperatingValues values = m_AtcSystem.GetOperatingValues(m_Airport);
            float seaLevel = m_WaterSystem.SeaLevel;
            if (!m_Msa.IsPending && m_MsaText[0] == "-")
            {
                StartMsa(procedures);
            }

            ChartView view = new ChartView();
            view.m_Runway = end.m_Runway;
            view.m_Title = "RNP RWY " + end.m_Runway;
            view.m_AirportName = m_NameSystem.GetRenderedLabelName(m_Airport);
            view.m_Code = AirportCodes.Get(EntityManager, m_NameSystem, m_Airport);
            view.m_Tower = AtcRadio.FrequencyLabel(m_Airport, AtcFrequency.Tower);
            bool approachControl = !m_AtcSystem.TryGetAirspace(m_Airport, out AirportSpace space) || space.m_Class != AirspaceClass.D;
            view.m_Approach = approachControl ? AtcRadio.FrequencyLabel(m_Airport, AtcFrequency.Approach) : string.Empty;
            view.m_Elevation = (int)math.round(math.max(0f, end.m_Threshold.y - seaLevel) / 0.3048f);

            float2 threshold = end.m_Threshold.xz;
            view.m_FafNm = math.distance(end.m_Faf.xz, threshold) / Setting.kMetersPerNauticalMile;
            view.m_IfNm = view.m_FafNm + math.distance(end.m_If.xz, end.m_Faf.xz) / Setting.kMetersPerNauticalMile;
            view.m_IafNm = view.m_IfNm + math.distance(end.m_IafLeft.xz, end.m_If.xz) / Setting.kMetersPerNauticalMile;
            view.m_IafAltitude = IfrCharts.AltitudeFeet(end.m_IafLeft.y, seaLevel);
            view.m_IfAltitude = IfrCharts.AltitudeFeet(end.m_If.y, seaLevel);
            view.m_FafAltitude = IfrCharts.AltitudeFeet(end.m_Faf.y, seaLevel);
            view.m_InitialMinimum = IfrCharts.AltitudeFeet(end.m_FloorInitialLeft, seaLevel);
            view.m_IntermediateMinimum = IfrCharts.AltitudeFeet(end.m_FloorIntermediate, seaLevel);
            view.m_IafName = names.m_IafLeft;
            view.m_IfName = names.m_If;
            view.m_FafName = names.m_Faf;
            view.m_GlideDegrees = values.GlidePathDegrees;
            float glideTan = math.tan(math.radians(values.GlidePathDegrees));
            // The tower's glide path comes down to the threshold itself (AtcJob.FlyToFinalFix): no threshold crossing height.
            view.m_Rdh = 0;
            view.m_Dh = (int)math.round(values.DecisionHeightFeet);
            view.m_Da = view.m_Elevation + view.m_Dh;
            view.m_DaNm = (view.m_Dh - view.m_Rdh) * 0.3048f / math.max(glideTan, 0.001f) / Setting.kMetersPerNauticalMile;
            view.m_PathCourses = new[]
            {
                IfrCharts.Course(end.m_If.xz - end.m_IafLeft.xz), IfrCharts.Course(end.m_Faf.xz - end.m_If.xz), IfrCharts.Course(threshold - end.m_Faf.xz),
            };
            view.m_MissedNames = new[] { names.m_Upwind, names.m_Crosswind, names.m_Downwind };
            view.m_MissedCourses = new[]
            {
                IfrCharts.Course(end.m_Upwind.xz - end.m_RunwayEnd.xz), IfrCharts.Course(end.m_Crosswind.xz - end.m_Upwind.xz),
                IfrCharts.Course(end.m_Downwind.xz - end.m_Crosswind.xz),
            };
            int pattern = IfrCharts.AltitudeFeet(end.m_Crosswind.y, seaLevel);
            view.m_Missed = "Climb to " + IfrCharts.Spaced(pattern) + " on runway heading. Track to " + names.m_Upwind + ", " + names.m_Crosswind
                + " and " + names.m_Downwind + ", then turn onto final at " + names.m_Faf + ". If not cleared, continue to " + names.m_MissedHold
                + " and hold.";
            view.m_RateOfDescent = new int[kKnots.Length];
            for (int i = 0; i < kKnots.Length; i++)
            {
                view.m_RateOfDescent[i] = (int)math.round(kKnots[i] * 101.27f * glideTan);
            }

            // Coding tables: one per transition, one for the missed approach (AIP columns; no coordinates in the game).
            string thresholdId = "RW" + end.m_Runway;
            string vpa = "-" + values.GlidePathDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°/" + view.m_Rdh.ToString(CultureInfo.InvariantCulture);
            string faf = "@" + IfrCharts.Spaced(view.m_FafAltitude), at = "+" + IfrCharts.Spaced(view.m_IfAltitude);
            AddTable(view, view.m_Title + " " + names.m_IafLeft + " TRANSITION", null, null, new[]
            {
                Row(end.m_IafLeft.xz, names.m_IafLeft, "+" + IfrCharts.Spaced(view.m_IafAltitude), false, null),
                Row(end.m_If.xz, names.m_If, at, false, null), Row(end.m_Faf.xz, names.m_Faf, faf, false, null), Row(threshold, thresholdId, null, true, vpa),
            });
            AddTable(view, view.m_Title + " " + names.m_IafRight + " TRANSITION", null, null, new[]
            {
                Row(end.m_IafRight.xz, names.m_IafRight, "+" + IfrCharts.Spaced(IfrCharts.AltitudeFeet(end.m_IafRight.y, seaLevel)), false, null),
                Row(end.m_If.xz, names.m_If, at, false, null), Row(end.m_Faf.xz, names.m_Faf, faf, false, null), Row(threshold, thresholdId, null, true, vpa),
            });
            // The missed approach starts at the MAPt (the threshold), which is not one of its rows.
            string level = "@" + IfrCharts.Spaced(pattern);
            AddTable(view, view.m_Title + " MISSED APPROACH", threshold, end.m_Faf.xz, new[]
            {
                Row(end.m_Upwind.xz, names.m_Upwind, "+" + IfrCharts.Spaced(pattern), false, null), Row(end.m_Crosswind.xz, names.m_Crosswind, level, false, null),
                Row(end.m_Downwind.xz, names.m_Downwind, level, false, null), Row(end.m_MissedHold.xz, names.m_MissedHold, level, false, null),
                HoldRow(end.m_MissedHold.xz, names.m_MissedHold, level, end.m_MissedInbound, end.m_HoldLeg),
            });
            return view;
        }

        private struct LegRow
        {
            public float2 m_Point;
            public string m_Id;
            public string m_Altitude;
            public bool m_FlyOver;
            public string m_Vpa;

            /// <summary>A hold at the fix (path descriptor HM): its inbound course and leg length.</summary>
            public bool m_Hold;
            public float2 m_Inbound;
            public float m_Leg;
        }

        private static LegRow Row(float2 point, string id, string altitude, bool flyOver, string vpa)
        {
            return new LegRow { m_Point = point, m_Id = id, m_Altitude = altitude, m_FlyOver = flyOver, m_Vpa = vpa };
        }

        private static LegRow HoldRow(float2 point, string id, string altitude, float2 inbound, float leg)
        {
            return new LegRow { m_Point = point, m_Id = id, m_Altitude = altitude, m_Hold = true, m_Inbound = inbound, m_Leg = leg };
        }

        /// <summary>
        /// A coding table: serial number, path descriptor (IF first, then TF; one that starts after <paramref name="from"/>
        /// is TF throughout; a hold HM, with its inbound course, leg length and right turns), waypoint, fly-over, course,
        /// distance, turn direction onto the leg, altitude, speed, VPA/RDH and navigation specification.
        /// </summary>
        private static void AddTable(ChartView view, string title, float2? from, float2? fromBefore, LegRow[] legs)
        {
            List<string[]> rows = new List<string[]>(legs.Length);
            for (int i = 0; i < legs.Length; i++)
            {
                if (legs[i].m_Hold)
                {
                    rows.Add(new[]
                    {
                        (i + 1).ToString("000", CultureInfo.InvariantCulture), "HM", legs[i].m_Id, "-", IfrCharts.Course(legs[i].m_Inbound).TrimEnd('°'),
                        (legs[i].m_Leg / Setting.kMetersPerNauticalMile).ToString("0.0", CultureInfo.InvariantCulture), "R", legs[i].m_Altitude ?? "-", "-", "-",
                        kNavigationSpecification,
                    });
                    continue;
                }
                float2? previous = i > 0 ? legs[i - 1].m_Point : from;
                float2? before = i > 1 ? legs[i - 2].m_Point : i == 1 ? from : fromBefore;
                rows.Add(new[]
                {
                    (i + 1).ToString("000", CultureInfo.InvariantCulture),
                    i == 0 && from == null ? "IF" : "TF",
                    legs[i].m_Id,
                    legs[i].m_FlyOver ? "Y" : "-",
                    previous.HasValue ? IfrCharts.Course(legs[i].m_Point - previous.Value).TrimEnd('°') : "-",
                    previous.HasValue ? (math.distance(previous.Value, legs[i].m_Point) / Setting.kMetersPerNauticalMile).ToString("0.0", CultureInfo.InvariantCulture) : "-",
                    previous.HasValue && before.HasValue ? TurnOnto(before.Value, previous.Value, legs[i].m_Point) : "-",
                    legs[i].m_Altitude ?? "-",
                    "-",
                    legs[i].m_Vpa ?? "-",
                    kNavigationSpecification,
                });
            }
            view.m_TableTitles.Add(title);
            view.m_Tables.Add(rows);
        }

        /// <summary>The turn onto the leg b -> c after a -> b: "L", "R", or "-" when straight on.</summary>
        private static string TurnOnto(float2 a, float2 b, float2 c)
        {
            float turn = RunwayNames.Heading(c - b) - RunwayNames.Heading(b - a);
            turn = (turn + 540f) % 360f - 180f;
            return math.abs(turn) < 1f ? "-" : turn < 0f ? "L" : "R";
        }

        /// <summary>The four MSA sectors (NE, SE, SW, NW) round the airport, out to its airspace radius.</summary>
        private void StartMsa(AirportProcedures procedures)
        {
            m_MsaRadius = m_AtcSystem.TryGetAirspace(m_Airport, out AirportSpace space) ? space.m_Radius : 3.5f * Setting.kMetersPerNauticalMile;
            m_Msa.Clear();
            for (int i = 0; i < 4; i++)
            {
                m_Msa.AddArea(new IfrChartObstacles.Area
                {
                    m_Kind = IfrChartObstacles.AreaKind.Sector,
                    m_A = procedures.m_Center,
                    m_B = new float2(i * 90f, (i + 1) * 90f),
                    m_Radius = m_MsaRadius,
                });
            }
            m_Msa.Start(EntityManager, m_BuildingQuery, m_TerrainSystem);
        }

        private void Write(IJsonWriter writer)
        {
            ChartView view = m_Open ? m_View : null;
            writer.TypeBegin("RealisticAviation.ApproachChart");
            writer.PropertyName("open");
            writer.Write(view != null);
            WriteString(writer, "title", view?.m_Title);
            WriteString(writer, "runway", view?.m_Runway);
            WriteString(writer, "airport", view?.m_AirportName);
            WriteString(writer, "code", view?.m_Code);
            WriteString(writer, "tower", view?.m_Tower);
            WriteString(writer, "approach", view?.m_Approach);
            WriteString(writer, "missed", view?.m_Missed);
            WriteString(writer, "transitionAltitude", kTransitionAltitude);
            WriteString(writer, "transitionLevel", kTransitionLevel);
            writer.PropertyName("elevation");
            writer.Write(view?.m_Elevation ?? 0);
            writer.PropertyName("msa");
            writer.ArrayBegin(4u);
            for (int i = 0; i < 4; i++)
            {
                writer.Write(m_MsaText[i] ?? "-");
            }
            writer.ArrayEnd();
            WriteString(writer, "msaRadius", (m_MsaRadius / Setting.kMetersPerNauticalMile).ToString("0.0", CultureInfo.InvariantCulture));
            writer.PropertyName("iafNm");
            writer.Write(view?.m_IafNm ?? 0f);
            writer.PropertyName("ifNm");
            writer.Write(view?.m_IfNm ?? 0f);
            writer.PropertyName("fafNm");
            writer.Write(view?.m_FafNm ?? 0f);
            writer.PropertyName("daNm");
            writer.Write(view?.m_DaNm ?? 0f);
            writer.PropertyName("iafAltitude");
            writer.Write(view?.m_IafAltitude ?? 0);
            writer.PropertyName("ifAltitude");
            writer.Write(view?.m_IfAltitude ?? 0);
            writer.PropertyName("fafAltitude");
            writer.Write(view?.m_FafAltitude ?? 0);
            writer.PropertyName("initialMinimum");
            writer.Write(view?.m_InitialMinimum ?? 0);
            writer.PropertyName("intermediateMinimum");
            writer.Write(view?.m_IntermediateMinimum ?? 0);
            writer.PropertyName("da");
            writer.Write(view?.m_Da ?? 0);
            writer.PropertyName("dh");
            writer.Write(view?.m_Dh ?? 0);
            writer.PropertyName("rdh");
            writer.Write(view?.m_Rdh ?? 0);
            writer.PropertyName("glideDegrees");
            writer.Write(view?.m_GlideDegrees ?? 0f);
            WriteString(writer, "iafName", view?.m_IafName);
            WriteString(writer, "ifName", view?.m_IfName);
            WriteString(writer, "fafName", view?.m_FafName);
            WriteStrings(writer, "pathCourses", view?.m_PathCourses);
            WriteStrings(writer, "missedNames", view?.m_MissedNames);
            WriteStrings(writer, "missedCourses", view?.m_MissedCourses);
            writer.PropertyName("rateOfDescent");
            int[] rates = view?.m_RateOfDescent;
            writer.ArrayBegin((uint)(rates?.Length ?? 0));
            for (int i = 0; rates != null && i < rates.Length; i++)
            {
                writer.Write(rates[i]);
            }
            writer.ArrayEnd();
            writer.PropertyName("tables");
            int tables = view?.m_Tables.Count ?? 0;
            writer.ArrayBegin((uint)tables);
            for (int t = 0; t < tables; t++)
            {
                writer.TypeBegin("RealisticAviation.CodingTable");
                WriteString(writer, "title", view.m_TableTitles[t]);
                writer.PropertyName("rows");
                List<string[]> rows = view.m_Tables[t];
                writer.ArrayBegin((uint)rows.Count);
                for (int r = 0; r < rows.Count; r++)
                {
                    writer.ArrayBegin((uint)rows[r].Length);
                    for (int c = 0; c < rows[r].Length; c++)
                    {
                        writer.Write(rows[r][c]);
                    }
                    writer.ArrayEnd();
                }
                writer.ArrayEnd();
                writer.TypeEnd();
            }
            writer.ArrayEnd();
            writer.TypeEnd();
        }

        private static void WriteString(IJsonWriter writer, string name, string value)
        {
            writer.PropertyName(name);
            writer.Write(value ?? string.Empty);
        }

        private static void WriteStrings(IJsonWriter writer, string name, string[] values)
        {
            writer.PropertyName(name);
            writer.ArrayBegin((uint)(values?.Length ?? 0));
            for (int i = 0; values != null && i < values.Length; i++)
            {
                writer.Write(values[i]);
            }
            writer.ArrayEnd();
        }
    }
}
