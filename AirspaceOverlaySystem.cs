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
    /// "Airspace" infoview (phase 4-3): a button in the game's infoview menu that highlights the airplane stops (gates)
    /// and draws each airport's airspace on the map in the style of a VFR sectional chart: class B as solid blue rings,
    /// class C as solid magenta rings (an inner surface area and the outer shelf), class D as a dashed blue ring. Shown only
    /// while the infoview is active; Esc or another infoview closes it as usual.
    /// </summary>
    public partial class AirspaceOverlaySystem : GameSystemBase
    {
        public const string kInfoviewName = "RealisticAviationAirspace";
        public const string kInfomodeName = "RealisticAviationAirplaneStops";

        // Sectional chart colours and line widths (metres on the ground, so they read from far out).
        private static readonly Color kBlue = new Color(0.25f, 0.5f, 1f, 0.9f);
        private static readonly Color kMagenta = new Color(0.85f, 0.25f, 0.8f, 0.9f);
        private const float kOuterWidth = 40f;
        private const float kInnerWidth = 60f;

        // Inner ring (the surface area of B and C) as a share of the zone radius.
        private const float kInnerRingFactor = 0.5f;

        // Rings and boundary lines are drawn as this many straight pieces (dashed lines skip every other one).
        private const int kRingSegments = 128;
        private const int kBoundarySegments = 24;

        private static InfoviewPrefab s_Infoview;

        private ToolSystem m_ToolSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private AtcSystem m_AtcSystem;
        private Game.UI.InGame.SelectedInfoUISystem m_InfoUISystem;

        /// <summary>
        /// Adds the infoview to the game's prefabs. An infoview needs at least one infomode to be listed; this one highlights
        /// airplane stops, which fits a map of airports.
        /// </summary>
        public static void CreateInfoview(PrefabSystem prefabSystem)
        {
            TransportStopInfomodePrefab stops = ScriptableObject.CreateInstance<TransportStopInfomodePrefab>();
            stops.name = kInfomodeName;
            stops.m_Type = TransportType.Airplane;
            stops.m_Color = kBlue;
            stops.m_IncludeInGroups = new InfomodeGroupPrefab[0];
            prefabSystem.AddPrefab(stops);

            InfoviewPrefab infoview = ScriptableObject.CreateInstance<InfoviewPrefab>();
            infoview.name = kInfoviewName;
            infoview.m_IconPath = "Media/Game/Icons/Airplane.svg";
            infoview.m_Group = 100;
            infoview.m_Priority = 1;
            infoview.m_WarningCategories = new IconCategory[0];
            infoview.m_Infomodes = new[] { new InfomodeInfo { m_Mode = stops, m_Priority = 1 } };
            prefabSystem.AddPrefab(infoview);
            s_Infoview = infoview;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_InfoUISystem = World.GetOrCreateSystemManaged<Game.UI.InGame.SelectedInfoUISystem>();
        }

        protected override void OnUpdate()
        {
            // Airspace class changes from the airport panel are applied here, every frame (also paused) and outside the UI
            // update, then the panel is refreshed.
            if (m_AtcSystem.ApplyAirspaceClassRequests())
            {
                m_InfoUISystem.RequestUpdate();
            }
            if (s_Infoview == null || m_ToolSystem.activeInfoview != s_Infoview)
            {
                return;
            }
            NativeParallelHashMap<Entity, AirportSpace> airspaces = m_AtcSystem.Airspaces;
            if (!airspaces.IsCreated || airspaces.IsEmpty)
            {
                return;
            }
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle dependencies);
            dependencies.Complete();
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
            }
            all.Dispose();
        }

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
