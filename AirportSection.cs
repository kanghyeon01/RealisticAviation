using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.Net;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Tower options in an airport's info panel: line up and wait for the airport, and the traffic pattern direction for
    /// each runway end (listed as RWY 09, RWY 27, ...). RealisticAviation.mjs draws the checkboxes; a click comes back
    /// through a trigger binding and updates <see cref="LineUpAndWaitAirport"/> on the airport building or
    /// <see cref="RunwayPattern"/> on the runway's edges.
    /// </summary>
    public partial class AirportSection : InfoSectionBase
    {
        public const string kToggleLineUpTrigger = "ToggleLineUpAndWait";
        public const string kToggleRightHandTrigger = "ToggleRunwayRightHand";

        private struct RunwayEnd
        {
            public RunwayInfo m_Runway;
            public float2 m_Landing;
            public int m_Designator;

            /// <summary>Distance to the right of the landing direction, used to tell parallel runways apart.</summary>
            public float m_Lateral;

            public string m_Name;
            public bool m_RightHand;
        }

        private readonly List<RunwayEnd> m_RunwayEnds = new List<RunwayEnd>();
        private AtcSystem m_AtcSystem;
        private EntityQuery m_AirplaneStopQuery;
        private Entity m_Airport;
        private bool m_LineUp;

        protected override string group => nameof(AirportSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(Mod.kName, kToggleLineUpTrigger, ToggleLineUp));
            AddBinding(new TriggerBinding<int>(Mod.kName, kToggleRightHandTrigger, ToggleRightHand));
        }

        protected override void OnUpdate()
        {
            m_Airport = Airports.GetAirport(EntityManager, m_AirplaneStopQuery, selectedEntity);
            visible = m_Airport != Entity.Null;
        }

        protected override void Reset()
        {
            m_LineUp = false;
            m_RunwayEnds.Clear();
        }

        protected override void OnProcess()
        {
            m_LineUp = EntityManager.HasComponent<LineUpAndWaitAirport>(m_Airport);
            IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (runways[i].Airport != m_Airport)
                {
                    continue;
                }
                // Only the ends airplanes actually use (game runways are usually one-way); both if the lanes say nothing.
                RunwayGeometry geometry = runways[i].Geometry;
                byte landings = geometry.m_Landings != 0 ? geometry.m_Landings : (byte)(RunwayGeometry.kAlong | RunwayGeometry.kAgainst);
                if ((landings & RunwayGeometry.kAlong) != 0)
                {
                    AddEnd(runways[i], geometry.m_Direction);
                }
                if ((landings & RunwayGeometry.kAgainst) != 0)
                {
                    AddEnd(runways[i], -geometry.m_Direction);
                }
            }
            m_RunwayEnds.Sort((a, b) => a.m_Designator != b.m_Designator ? a.m_Designator.CompareTo(b.m_Designator) : a.m_Lateral.CompareTo(b.m_Lateral));
            NameParallelRunways();
        }

        /// <summary>
        /// Parallel runways share a number; as at real airports they are told apart by position, seen in the landing
        /// direction: L and R, or L, C and R for three (beyond that they are numbered). The list is sorted left to right.
        /// </summary>
        private void NameParallelRunways()
        {
            int start = 0;
            while (start < m_RunwayEnds.Count)
            {
                int end = start + 1;
                while (end < m_RunwayEnds.Count && m_RunwayEnds[end].m_Designator == m_RunwayEnds[start].m_Designator)
                {
                    end++;
                }
                int count = end - start;
                for (int i = start; i < end; i++)
                {
                    RunwayEnd runwayEnd = m_RunwayEnds[i];
                    runwayEnd.m_Name = runwayEnd.m_Designator.ToString("00") + Suffix(i - start, count);
                    m_RunwayEnds[i] = runwayEnd;
                }
                start = end;
            }
        }

        private static string Suffix(int index, int count)
        {
            if (count == 1)
            {
                return string.Empty;
            }
            if (count == 2)
            {
                return index == 0 ? "L" : "R";
            }
            if (count == 3)
            {
                return index == 0 ? "L" : index == 1 ? "C" : "R";
            }
            return "-" + (index + 1);
        }

        private void AddEnd(RunwayInfo runway, float2 landing)
        {
            m_RunwayEnds.Add(new RunwayEnd
            {
                m_Runway = runway,
                m_Landing = landing,
                m_Designator = Designator(landing),
                m_Lateral = math.dot(runway.Geometry.Middle, Colossal.Mathematics.MathUtils.Right(landing)),
                m_RightHand = IsRightHand(runway, landing),
            });
        }

        /// <summary>Runway number: the landing heading in tens of degrees, 01–36 (the game has no magnetic variation).</summary>
        private static int Designator(float2 landing)
        {
            float heading = math.degrees(math.atan2(landing.x, landing.y));
            int designator = (int)math.round(heading / 10f) % 36;
            if (designator <= 0)
            {
                designator += 36;
            }
            return designator;
        }

        /// <summary>The player's choice for this runway end, or else the default the tower uses (see AtcSystem).</summary>
        private bool IsRightHand(RunwayInfo runway, float2 landing)
        {
            for (int i = 0; i < runway.Edges.Count; i++)
            {
                Entity edge = runway.Edges[i];
                if (EntityManager.Exists(edge) && EntityManager.HasComponent<RunwayPattern>(edge) && EntityManager.HasComponent<Curve>(edge)
                    && EntityManager.GetComponentData<RunwayPattern>(edge).TryGetRightHand(EdgeDirection(edge), landing, out bool rightHand))
                {
                    return rightHand;
                }
            }
            return IsRightHandByDefault(runway, landing);
        }

        /// <summary>Right-hand when a parallel runway lies to the left, so that the pattern stays on the outside.</summary>
        private bool IsRightHandByDefault(RunwayInfo runway, float2 landing)
        {
            float width = Mod.Setting.PatternWidthNm * Setting.kMetersPerNauticalMile;
            float2 left = Colossal.Mathematics.MathUtils.Left(landing);
            IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (runways[i] != runway && RunwayGeometry.IsParallelOnSide(runway.Geometry, runways[i].Geometry, landing, left, width))
                {
                    return true;
                }
            }
            return false;
        }

        private float2 EdgeDirection(Entity edge)
        {
            var bezier = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
            return bezier.d.xz - bezier.a.xz;
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("lineUp");
            writer.Write(m_LineUp);
            writer.PropertyName("runways");
            writer.ArrayBegin(m_RunwayEnds.Count);
            for (int i = 0; i < m_RunwayEnds.Count; i++)
            {
                writer.TypeBegin("RealisticAviation.RunwayEnd");
                writer.PropertyName("name");
                writer.Write(m_RunwayEnds[i].m_Name);
                writer.PropertyName("rightHand");
                writer.Write(m_RunwayEnds[i].m_RightHand);
                writer.TypeEnd();
            }
            writer.ArrayEnd();
        }

        private void ToggleLineUp()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            bool enable = !EntityManager.HasComponent<LineUpAndWaitAirport>(m_Airport);
            if (enable)
            {
                EntityManager.AddComponent<LineUpAndWaitAirport>(m_Airport);
            }
            else
            {
                EntityManager.RemoveComponent<LineUpAndWaitAirport>(m_Airport);
            }
            Mod.Log.Info($"Line up and wait {(enable ? "enabled" : "disabled")} at airport {m_Airport}");
            // Re-sends every section to the UI; only marking this one dirty would leave the checkbox unchanged on screen.
            m_InfoUISystem.RequestUpdate();
        }

        /// <summary>Flips one runway end between left- and right-hand, on every edge of that runway.</summary>
        private void ToggleRightHand(int index)
        {
            if (index < 0 || index >= m_RunwayEnds.Count)
            {
                return;
            }
            RunwayEnd end = m_RunwayEnds[index];
            bool rightHand = !end.m_RightHand;
            List<Entity> edges = end.m_Runway.Edges;
            for (int i = 0; i < edges.Count; i++)
            {
                Entity edge = edges[i];
                if (!EntityManager.Exists(edge) || !EntityManager.HasComponent<Curve>(edge))
                {
                    continue;
                }
                RunwayPattern pattern = EntityManager.HasComponent<RunwayPattern>(edge)
                    ? EntityManager.GetComponentData<RunwayPattern>(edge)
                    : default;
                pattern.SetRightHand(EdgeDirection(edge), end.m_Landing, rightHand);
                if (EntityManager.HasComponent<RunwayPattern>(edge))
                {
                    EntityManager.SetComponentData(edge, pattern);
                }
                else
                {
                    EntityManager.AddComponentData(edge, pattern);
                }
            }
            Mod.Log.Info($"RWY {end.m_Name} at airport {m_Airport}: {(rightHand ? "right" : "left")}-hand traffic pattern");
            m_InfoUISystem.RequestUpdate();
        }
    }
}
