using Colossal.Entities;
using Colossal.Mathematics;
using Game;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>
    /// "Airspace" infoview (phase 4-3): a button in the game's infoview menu that draws the map in the style of a VFR
    /// sectional chart: class B as solid blue rings, class C as solid magenta rings (an inner surface area and the outer
    /// shelf), class D as a dashed blue ring, each with its altitude limits (<see cref="ChartLabels"/>); built-up areas in
    /// yellow (<see cref="UrbanAreaMap"/>); and the airport buildings in their class's colour (<see cref="AirportHighlightSystem"/>).
    /// The legend lists the airports and the built-up areas, each of which can be turned off there. Shown only while the
    /// infoview is active; Esc or another infoview closes it as usual.
    /// </summary>
    public partial class AirspaceOverlaySystem : GameSystemBase
    {
        public const string kInfoviewName = "RealisticAviationAirspace";
        public const string kAirportsInfomodeName = "RealisticAviationAirports";
        public const string kAirportsCInfomodeName = "RealisticAviationAirportsC";
        public const string kObstaclesInfomodeName = "RealisticAviationObstacles";
        public const string kUrbanInfomodeName = "RealisticAviationUrbanAreas";
        public const string kTerrainInfomodeName = "RealisticAviationTerrain";

        // Sectional chart colours and line widths (metres on the ground, so they read from far out).
        private static readonly Color kBlue = new Color(0.25f, 0.5f, 1f, 0.9f);
        private static readonly Color kMagenta = new Color(0.85f, 0.25f, 0.8f, 0.9f);
        private static readonly Color kYellow = new Color(1f, 0.85f, 0.25f, 1f);
        private static readonly Color kNavy = new Color(0.2f, 0.3f, 0.75f, 1f);
        private static readonly Color kTerrainGreen = new Color(0.78f, 0.86f, 0.65f, 1f);
        private static readonly Color kMefBlue = new Color(0.12f, 0.37f, 0.63f, 1f);

        // MEF figures: the thousands large, the hundreds smaller and raised, in metres on the ground.
        private const float kMefLarge = 420f;
        private const float kMefSmall = 250f;
        private const float kOuterWidth = 40f;
        private const float kInnerWidth = 60f;

        // Inner ring (the surface area of B and C) as a share of the zone radius.
        private const float kInnerRingFactor = 0.5f;

        // Rings and boundary lines are drawn as this many straight pieces (dashed lines skip every other one).
        private const int kRingSegments = 128;
        private const int kBoundarySegments = 24;

        // Altitude labels: character height as a share of the zone radius, stroke width as a share of the height, and
        // where they sit as shares of the radius (inside the surface area, in the shelf, inside a class D ring).
        private const float kLabelHeightFactor = 0.032f;
        private const float kLabelStrokeFactor = 0.1f;
        private const float kInnerLabelFactor = 0.3f;
        private const float kShelfLabelFactor = 0.75f;
        private const float kClassDLabelFactor = 0.6f;
        private const float kFeetPerMetre = 3.28084f;

        // Typical FAA limits above the field, in feet: class B to 10,000 with the shelf from 3,000, class C to 4,000 with
        // the shelf from 1,200, class D to 2,500.
        private const float kClassBTop = 10000f;
        private const float kClassBShelf = 3000f;
        private const float kClassCTop = 4000f;
        private const float kClassCShelf = 1200f;
        private const float kClassDTop = 2500f;

        // Mode C veil around class B airports: on the chart 30 NM, here the class B radius plus 1 NM (6 NM), so that it
        // crosses the edge of the playable area (about 7.7 NM wide). A thin magenta ring, lettered at two places.
        private const float kModeCMarginNm = 1f;
        private const float kVeilWidth = 20f;
        private static readonly float[] kVeilLabelAngles = { math.radians(-120f), math.radians(60f) };
        private static string s_VeilLabel;
        private static int s_VeilLabelMiles = -1;

        // Directions tried for a label (south-east first, as charts often put them), until one is not covered by
        // another airport's airspace.
        private static readonly float2[] kLabelDirections =
        {
            math.normalize(new float2(1f, -1f)), math.normalize(new float2(-1f, -1f)), math.normalize(new float2(1f, 1f)),
            math.normalize(new float2(-1f, 1f)), new float2(0f, -1f), new float2(1f, 0f), new float2(-1f, 0f), new float2(0f, 1f),
        };

        private static InfoviewPrefab s_Infoview;
        private static InfomodePrefab s_AirportsInfomode;
        private static InfomodePrefab s_AirportsCInfomode;
        private static InfomodePrefab s_ObstaclesInfomode;
        private static InfomodePrefab s_UrbanInfomode;
        private static InfomodePrefab s_TerrainInfomode;

        private ToolSystem m_ToolSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Game.Simulation.WaterSystem m_WaterSystem;
        private PrefabSystem m_PrefabSystem;
        private AtcSystem m_AtcSystem;
        private Game.UI.InGame.SelectedInfoUISystem m_InfoUISystem;
        private UrbanAreaMap m_UrbanAreas;
        private ObstacleMap m_Obstacles;
        private TerrainChartMap m_Terrain;
        private Game.Simulation.TerrainSystem m_TerrainSystem;
        private readonly int[] m_MefHundreds = new int[TerrainChartMap.kMefCells * TerrainChartMap.kMefCells];
        private readonly string[] m_MefLarge = new string[TerrainChartMap.kMefCells * TerrainChartMap.kMefCells];
        private readonly string[] m_MefSmall = new string[TerrainChartMap.kMefCells * TerrainChartMap.kMefCells];
        private readonly float[] m_MefObstacleTop = new float[TerrainChartMap.kMefCells * TerrainChartMap.kMefCells];
        private int m_MefObstacleVersion = -1;
        private bool m_WasActive;

        /// <summary>
        /// Adds the infoview to the game's prefabs, with its legend: airports of class B and D (blue) and of class C (magenta), as
        /// their airspace on the map, built-up areas (yellow) and obstacles (navy). They are building-colour infomodes of a type
        /// the game colours nothing for, so they get a colour and a checkbox in the legend; <see cref="AirportHighlightSystem"/>
        /// and this system do the drawing.
        /// </summary>
        internal static void CreateInfoview(PrefabSystem prefabSystem)
        {
            s_AirportsInfomode = AddInfomode(prefabSystem, kAirportsInfomodeName, kBlue);
            s_AirportsCInfomode = AddInfomode(prefabSystem, kAirportsCInfomodeName, kMagenta);
            s_UrbanInfomode = AddInfomode(prefabSystem, kUrbanInfomodeName, kYellow);
            s_ObstaclesInfomode = AddInfomode(prefabSystem, kObstaclesInfomodeName, kNavy);
            s_TerrainInfomode = AddInfomode(prefabSystem, kTerrainInfomodeName, kTerrainGreen);

            InfoviewPrefab infoview = ScriptableObject.CreateInstance<InfoviewPrefab>();
            infoview.name = kInfoviewName;
            infoview.m_IconPath = "Media/Game/Icons/Airplane.svg";
            infoview.m_Group = 100;
            infoview.m_Priority = 1;
            infoview.m_WarningCategories = new IconCategory[0];
            infoview.m_Infomodes = new[]
            {
                new InfomodeInfo { m_Mode = s_AirportsInfomode, m_Priority = 1 },
                new InfomodeInfo { m_Mode = s_AirportsCInfomode, m_Priority = 2 },
                new InfomodeInfo { m_Mode = s_UrbanInfomode, m_Priority = 3 },
                new InfomodeInfo { m_Mode = s_ObstaclesInfomode, m_Priority = 4 },
                new InfomodeInfo { m_Mode = s_TerrainInfomode, m_Priority = 5 },
            };
            prefabSystem.AddPrefab(infoview);
            s_Infoview = infoview;
        }

        private static BuildingInfomodePrefab AddInfomode(PrefabSystem prefabSystem, string name, Color color)
        {
            BuildingInfomodePrefab infomode = ScriptableObject.CreateInstance<BuildingInfomodePrefab>();
            infomode.name = name;
            infomode.m_Type = BuildingType.None;
            infomode.m_Color = color;
            infomode.m_IncludeInGroups = new InfomodeGroupPrefab[0];
            prefabSystem.AddPrefab(infomode);
            return infomode;
        }

        /// <summary>Whether the infoview is open.</summary>
        internal static bool IsActive(ToolSystem toolSystem) => s_Infoview != null && toolSystem.activeInfoview == s_Infoview;

        /// <summary>The colour index of the airport legend entry for this class, or -1 when it is turned off in the legend.</summary>
        internal static int AirportColorIndex(EntityManager entityManager, PrefabSystem prefabSystem, AirspaceClass airspaceClass)
        {
            InfomodePrefab infomode = airspaceClass == AirspaceClass.C ? s_AirportsCInfomode : s_AirportsInfomode;
            Entity entity = infomode != null ? prefabSystem.GetEntity(infomode) : Entity.Null;
            return entity != Entity.Null && entityManager.TryGetComponent(entity, out InfomodeActive active) ? active.m_Index : -1;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_WaterSystem = World.GetOrCreateSystemManaged<Game.Simulation.WaterSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_InfoUISystem = World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
            m_UrbanAreas = new UrbanAreaMap(EntityManager);
            m_Obstacles = new ObstacleMap(EntityManager);
            m_Terrain = new TerrainChartMap();
            m_TerrainSystem = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
        }

        protected override void OnDestroy()
        {
            m_UrbanAreas.Dispose();
            m_Obstacles.Dispose();
            m_Terrain.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // Airspace class changes from the airport panel are applied here, every frame (also paused) and outside the UI
            // update, then the panel is refreshed.
            if (m_AtcSystem.ApplyAirspaceClassRequests())
            {
                m_InfoUISystem.RequestUpdate();
            }
            bool active = IsActive(m_ToolSystem);
            if (!active)
            {
                if (m_WasActive)
                {
                    m_UrbanAreas.Reset();
                    m_Obstacles.Reset();
                    m_Terrain.Reset();
                    m_WasActive = false;
                }
                return;
            }
            m_WasActive = true;
            bool urban = s_UrbanInfomode != null && EntityManager.HasComponent<InfomodeActive>(m_PrefabSystem.GetEntity(s_UrbanInfomode));
            if (urban)
            {
                JobHandle count = m_UrbanAreas.Update(GetComponentLookup<Game.Objects.Transform>(true), GetBufferLookup<Game.Buildings.Renter>(true),
                    GetBufferLookup<Game.Citizens.HouseholdCitizen>(true), GetBufferLookup<Game.Companies.Employee>(true), Dependency);
                Dependency = JobHandle.CombineDependencies(Dependency, count);
            }
            float seaLevel = m_WaterSystem.SeaLevel;
            bool obstacles = s_ObstaclesInfomode != null && EntityManager.HasComponent<InfomodeActive>(m_PrefabSystem.GetEntity(s_ObstaclesInfomode));
            if (obstacles)
            {
                JobHandle find = m_Obstacles.Update(GetComponentLookup<Game.Objects.Transform>(true), GetComponentLookup<PrefabRef>(true),
                    GetComponentLookup<ObjectGeometryData>(true), GetComponentLookup<WindPoweredData>(true), GetComponentLookup<Game.Common.Owner>(true),
                    GetComponentLookup<Game.Net.Edge>(true), GetComponentLookup<Game.Net.Node>(true), m_TerrainSystem, m_WaterSystem, seaLevel,
                    urban ? m_UrbanAreas : null, Dependency);
                Dependency = JobHandle.CombineDependencies(Dependency, find);
            }
            bool terrain = s_TerrainInfomode != null && EntityManager.HasComponent<InfomodeActive>(m_PrefabSystem.GetEntity(s_TerrainInfomode));
            if (terrain)
            {
                JobHandle sample = m_Terrain.Update(m_TerrainSystem, m_WaterSystem, seaLevel, Dependency);
                Dependency = JobHandle.CombineDependencies(Dependency, sample);
            }
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
            // Under the airspace lines, as on the chart.
            // Elevation tints at the bottom, built-up areas over them, as on the chart.
            if (terrain)
            {
                m_Terrain.DrawTints(buffer);
            }
            if (urban)
            {
                m_UrbanAreas.Draw(buffer);
            }
            if (obstacles)
            {
                m_Obstacles.Draw(buffer, kNavy);
            }
            if (terrain)
            {
                m_Terrain.DrawSpots(buffer);
                DrawMef(buffer, seaLevel);
            }
            NativeParallelHashMap<Entity, AirportSpace> airspaces = m_AtcSystem.Airspaces;
            if (!airspaces.IsCreated || airspaces.IsEmpty)
            {
                return;
            }
            NativeKeyValueArrays<Entity, AirportSpace> all = airspaces.GetKeyValueArrays(Allocator.Temp);
            NativeArray<AirportSpace> spaces = all.Values;
            for (int i = 0; i < spaces.Length; i++)
            {
                AirportSpace space = spaces[i];
                Color color = space.m_Class == AirspaceClass.C ? kMagenta : kBlue;
                bool dashed = space.m_Class == AirspaceClass.D;
                if (space.m_Class != AirspaceClass.D)
                {
                    DrawRing(buffer, spaces, i, space.m_Radius * kInnerRingFactor, true, color, kInnerWidth, false);
                }
                DrawRing(buffer, spaces, i, space.m_Radius, false, color, kOuterWidth, dashed);
                for (int j = i + 1; j < spaces.Length; j++)
                {
                    if (spaces[j].m_Class != space.m_Class)
                    {
                        continue;
                    }
                    if (space.m_Class != AirspaceClass.D)
                    {
                        DrawBoundary(buffer, spaces, i, j, space.m_Radius * kInnerRingFactor, color, kInnerWidth, false);
                    }
                    DrawBoundary(buffer, spaces, i, j, space.m_Radius, color, kOuterWidth, dashed);
                }
                DrawLabels(buffer, spaces, i, color, seaLevel);
                if (space.m_Class == AirspaceClass.B)
                {
                    DrawModeCVeil(buffer, spaces, i);
                }
            }
            all.Dispose();
        }

        /// <summary>
        /// The altitude limits of one airport's airspace, in hundreds of feet above sea level as on the chart: ceiling over
        /// "SFC" in the surface area and ceiling over floor in the shelf (B and C), the ceiling in a dashed box (D).
        /// </summary>
        private static void DrawLabels(OverlayRenderSystem.Buffer buffer, NativeArray<AirportSpace> spaces, int self, Color color, float seaLevel)
        {
            AirportSpace space = spaces[self];
            float elevation = math.max(0f, (space.m_Height - seaLevel) * kFeetPerMetre);
            float height = space.m_Radius * kLabelHeightFactor;
            float width = height * kLabelStrokeFactor;
            if (space.m_Class == AirspaceClass.D)
            {
                if (TryPlaceLabel(spaces, self, kClassDLabelFactor, false, out float2 at))
                {
                    // The ceiling in hundreds of feet; "-25" (up to but not including 2,500 ft) only where a class B or C airspace
                    // overlaps it, its shelf starting where class D ends, as the chart writes it.
                    string ceiling = Hundreds(elevation + kClassDTop);
                    ChartLabels.DrawBracketed(buffer, at, space.m_Height, UnderBOrC(spaces, self) ? "-" + ceiling : ceiling, height, color, width);
                }
                return;
            }
            bool classB = space.m_Class == AirspaceClass.B;
            string top = Hundreds(elevation + (classB ? kClassBTop : kClassCTop));
            if (TryPlaceLabel(spaces, self, kInnerLabelFactor, true, out float2 inner))
            {
                ChartLabels.DrawFraction(buffer, inner, space.m_Height, top, "SFC", height, color, width);
            }
            if (TryPlaceLabel(spaces, self, kShelfLabelFactor, false, out float2 shelf))
            {
                ChartLabels.DrawFraction(buffer, shelf, space.m_Height, top, Hundreds(elevation + (classB ? kClassBShelf : kClassCShelf)), height, color, width);
            }
        }

        /// <summary>
        /// The Mode C veil of a class B airport with "MODE C 8 NM" along it. Where two veils overlap, each keeps the part nearer
        /// to its own airport, as the airspace rings do.
        /// </summary>
        private static void DrawModeCVeil(OverlayRenderSystem.Buffer buffer, NativeArray<AirportSpace> spaces, int self)
        {
            AirportSpace own = spaces[self];
            float radius = own.m_Radius + kModeCMarginNm * Setting.kMetersPerNauticalMile;
            float3 previous = default;
            bool previousVisible = false;
            for (int step = 0; step <= kRingSegments; step++)
            {
                float angle = step * 2f * math.PI / kRingSegments;
                float2 point = own.m_Center + new float2(math.cos(angle), math.sin(angle)) * radius;
                float3 position = new float3(point.x, own.m_Height, point.y);
                bool visible = !IsInOtherVeil(spaces, self, point);
                if (step > 0 && visible && previousVisible)
                {
                    DrawPiece(buffer, previous, position, kMagenta, kVeilWidth);
                }
                previous = position;
                previousVisible = visible;
            }
            float height = own.m_Radius * kLabelHeightFactor;
            int nauticalMiles = (int)math.round(radius / Setting.kMetersPerNauticalMile);
            if (nauticalMiles != s_VeilLabelMiles)
            {
                s_VeilLabelMiles = nauticalMiles;
                s_VeilLabel = "MODE C " + nauticalMiles.ToString(System.Globalization.CultureInfo.InvariantCulture) + " NM";
            }
            string text = s_VeilLabel;
            for (int i = 0; i < kVeilLabelAngles.Length; i++)
            {
                float2 at = own.m_Center + new float2(math.cos(kVeilLabelAngles[i]), math.sin(kVeilLabelAngles[i])) * radius;
                if (!IsInOtherVeil(spaces, self, at))
                {
                    ChartLabels.DrawTextOnCircle(buffer, own.m_Center, radius, kVeilLabelAngles[i], own.m_Height, text, height, kMagenta, height * kLabelStrokeFactor);
                }
            }
        }

        private static bool IsInOtherVeil(NativeArray<AirportSpace> spaces, int self, float2 point)
        {
            float ownDistance = math.distance(point, spaces[self].m_Center);
            for (int i = 0; i < spaces.Length; i++)
            {
                if (i == self || spaces[i].m_Class != AirspaceClass.B)
                {
                    continue;
                }
                float distance = math.distance(point, spaces[i].m_Center);
                float reach = spaces[i].m_Radius + kModeCMarginNm * Setting.kMetersPerNauticalMile;
                if (distance < reach && distance < ownDistance)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Maximum Elevation Figures, as the FAA Chart Users' Guide defines them: the highest elevation in each quadrant,
        /// terrain or obstacle, rounded up to the next 100 ft, the last two digits left off ("12⁵" is 12,500 ft). Drawn at the
        /// quadrant's centre with the thousands large and the hundreds smaller and raised. The texts are rebuilt only when a
        /// figure changes.
        /// </summary>
        private void DrawMef(OverlayRenderSystem.Buffer buffer, float seaLevel)
        {
            float size = TerrainChartMap.MefSize;
            // The obstacles' highest tops per quadrant, looked up again only when a new search was collected.
            if (m_MefObstacleVersion != m_Obstacles.Version)
            {
                m_MefObstacleVersion = m_Obstacles.Version;
                for (int i = 0; i < m_MefObstacleTop.Length; i++)
                {
                    float2 corner = TerrainChartMap.MefOrigin + new float2(i % TerrainChartMap.kMefCells, i / TerrainChartMap.kMefCells) * size;
                    m_MefObstacleTop[i] = m_Obstacles.HighestTop(corner, corner + size);
                }
            }
            for (int z = 0; z < TerrainChartMap.kMefCells; z++)
            {
                for (int x = 0; x < TerrainChartMap.kMefCells; x++)
                {
                    if (!m_Terrain.TryGetMefTerrain(x, z, out float highest))
                    {
                        return;
                    }
                    float2 low = TerrainChartMap.MefOrigin + new float2(x, z) * size;
                    int index = x + z * TerrainChartMap.kMefCells;
                    highest = math.max(highest, m_MefObstacleTop[index]);
                    int hundreds = (int)math.ceil(math.max(0f, (highest - seaLevel) * kFeetPerMetre) / 100f);
                    if (hundreds != m_MefHundreds[index] || m_MefLarge[index] == null)
                    {
                        m_MefHundreds[index] = hundreds;
                        m_MefLarge[index] = (hundreds / 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        m_MefSmall[index] = (hundreds % 10).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    float largeWidth = ChartLabels.TextWidth(m_MefLarge[index], kMefLarge);
                    float totalWidth = largeWidth + 0.15f * kMefLarge + ChartLabels.TextWidth(m_MefSmall[index], kMefSmall);
                    float2 origin = low + size * 0.5f - new float2(totalWidth * 0.5f, kMefLarge * 0.5f);
                    ChartLabels.DrawText(buffer, origin, highest, m_MefLarge[index], kMefLarge, kMefBlue, 0.16f * kMefLarge);
                    ChartLabels.DrawText(buffer, origin + new float2(largeWidth + 0.15f * kMefLarge, kMefLarge - kMefSmall), highest,
                        m_MefSmall[index], kMefSmall, kMefBlue, 0.16f * kMefSmall);
                }
            }
        }

        /// <summary>Whether a class B or C airspace overlaps this class D one (their outer rings meet).</summary>
        private static bool UnderBOrC(NativeArray<AirportSpace> spaces, int self)
        {
            AirportSpace own = spaces[self];
            for (int i = 0; i < spaces.Length; i++)
            {
                AirportSpace other = spaces[i];
                if (i != self && other.m_Class != AirspaceClass.D && math.distance(own.m_Center, other.m_Center) < own.m_Radius + other.m_Radius)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The first label spot at this share of the radius that another airport's airspace doesn't cover.</summary>
        private static bool TryPlaceLabel(NativeArray<AirportSpace> spaces, int self, float factor, bool inner, out float2 at)
        {
            AirportSpace own = spaces[self];
            for (int i = 0; i < kLabelDirections.Length; i++)
            {
                at = own.m_Center + kLabelDirections[i] * own.m_Radius * factor;
                if (!IsHidden(spaces, self, at, inner))
                {
                    return true;
                }
            }
            at = default;
            return false;
        }

        private static string Hundreds(float feet) => ((int)math.round(feet / 100f)).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// One ring of an airport (its outer zone edge, or the inner surface area of B and C), drawn only where it isn't in
        /// another airport's airspace as a sectional chart shows it: no other lines inside a higher class's surface area,
        /// and where two zones of the same class overlap, each keeps the part nearer to its own airport. Dashed lines skip
        /// every other piece.
        /// </summary>
        private static void DrawRing(OverlayRenderSystem.Buffer buffer, NativeArray<AirportSpace> spaces, int self, float radius, bool inner,
            Color color, float width, bool dashed)
        {
            AirportSpace own = spaces[self];
            float3 previous = default;
            bool previousVisible = false;
            for (int step = 0; step <= kRingSegments; step++)
            {
                float angle = step * 2f * math.PI / kRingSegments;
                float2 point = own.m_Center + new float2(math.cos(angle), math.sin(angle)) * radius;
                float3 position = new float3(point.x, own.m_Height, point.y);
                bool visible = !IsHidden(spaces, self, point, inner);
                if (step > 0 && visible && previousVisible && (!dashed || step % 2 == 0))
                {
                    DrawPiece(buffer, previous, position, color, width);
                }
                previous = position;
                previousVisible = visible;
            }
        }

        /// <summary>The line between two overlapping zones of the same class: the chord through the points where their rings cross.</summary>
        private static void DrawBoundary(OverlayRenderSystem.Buffer buffer, NativeArray<AirportSpace> spaces, int a, int b, float radius,
            Color color, float width, bool dashed)
        {
            float2 from = spaces[a].m_Center;
            float2 to = spaces[b].m_Center;
            float distance = math.distance(from, to);
            if (distance >= radius * 2f || distance < 1f)
            {
                return;
            }
            float2 middle = (from + to) * 0.5f;
            float2 across = Colossal.Mathematics.MathUtils.Left((to - from) / distance) * math.sqrt(radius * radius - distance * distance * 0.25f);
            float height = (spaces[a].m_Height + spaces[b].m_Height) * 0.5f;
            float3 previous = default;
            bool previousVisible = false;
            for (int step = 0; step <= kBoundarySegments; step++)
            {
                float2 point = middle + across * math.lerp(-1f, 1f, (float)step / kBoundarySegments);
                float3 position = new float3(point.x, height, point.y);
                bool visible = !IsInHigherCore(spaces, spaces[a].m_Class, point);
                if (step > 0 && visible && previousVisible && (!dashed || step % 2 == 0))
                {
                    DrawPiece(buffer, previous, position, color, width);
                }
                previous = position;
                previousVisible = visible;
            }
        }

        private static bool IsHidden(NativeArray<AirportSpace> spaces, int self, float2 point, bool inner)
        {
            AirportSpace own = spaces[self];
            if (IsInHigherCore(spaces, own.m_Class, point))
            {
                return true;
            }
            float ownDistance = math.distance(point, own.m_Center);
            for (int i = 0; i < spaces.Length; i++)
            {
                AirportSpace other = spaces[i];
                if (i == self || other.m_Class != own.m_Class)
                {
                    continue;
                }
                float distance = math.distance(point, other.m_Center);
                float reach = inner ? other.m_Radius * kInnerRingFactor : other.m_Radius;
                if (distance < reach && distance < ownDistance)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Inside the surface area (inner ring) of an airport of a higher class than <paramref name="airspaceClass"/>.</summary>
        private static bool IsInHigherCore(NativeArray<AirportSpace> spaces, AirspaceClass airspaceClass, float2 point)
        {
            for (int i = 0; i < spaces.Length; i++)
            {
                AirportSpace other = spaces[i];
                float core = other.m_Radius * kInnerRingFactor;
                if (other.m_Class > airspaceClass && math.distancesq(point, other.m_Center) < core * core)
                {
                    return true;
                }
            }
            return false;
        }

        private static void DrawPiece(OverlayRenderSystem.Buffer buffer, float3 from, float3 to, Color color, float width)
        {
            buffer.DrawLine(color, color, 0f, OverlayRenderSystem.StyleFlags.Projected, new Line3.Segment(from, to), width, new float2(1f, 1f));
        }
    }
}
