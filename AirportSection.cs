using System.Collections.Generic;
using Colossal.UI.Binding;
using Game.Net;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;

namespace RealisticAviation
{
    /// <summary>
    /// Tower options (including the ground delay and its limit) and the radio log (latest exchanges on the approach, tower and ground frequencies) in an airport's info panel (the closure
    /// banner is <see cref="AirportClosedSection"/>). Options: line up
    /// and wait for the airport, and the traffic pattern direction for each runway end (listed as RWY 09, RWY 27, ...).
    /// RealisticAviation.mjs draws the checkboxes; a click comes back through a trigger binding and updates
    /// <see cref="LineUpSetting"/> on the airport building or <see cref="RunwayPattern"/> on the runway's edges.
    /// </summary>
    public partial class AirportSection : InfoSectionBase
    {
        public const string kToggleLineUpTrigger = "ToggleLineUpAndWait";
        public const string kToggleRightHandTrigger = "ToggleRunwayRightHand";
        public const string kToggleGroundDelayTrigger = "ToggleGroundDelay";
        public const string kAdjustGroundDelayTrigger = "AdjustGroundDelayLimit";
        public const string kSetAirspaceClassTrigger = "SetAirspaceClass";

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

        // Radio log (phase 4-2): the latest exchanges on each frequency.
        private const int kRadioExchanges = 4;
        private readonly RadioBox m_TowerRadio = new RadioBox();
        private readonly RadioBox m_GroundRadio = new RadioBox();
        private readonly RadioBox m_ApproachRadio = new RadioBox();
        private readonly RadioBox m_DepartureRadio = new RadioBox();
        private uint m_RadioVersion;
        private bool m_HasAirspace;
        private AirportSpace m_Airspace;
        private AtcSystem m_AtcSystem;
        private EntityQuery m_AirplaneStopQuery;
        private Entity m_Airport;
        private bool m_LineUp;
        private GroundDelaySetting m_GroundDelay;

        protected override string group => nameof(AirportSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(Mod.kName, kToggleLineUpTrigger, ToggleLineUp));
            AddBinding(new TriggerBinding<int>(Mod.kName, kToggleRightHandTrigger, ToggleRightHand));
            AddBinding(new TriggerBinding(Mod.kName, kToggleGroundDelayTrigger, ToggleGroundDelay));
            AddBinding(new TriggerBinding<int>(Mod.kName, kAdjustGroundDelayTrigger, AdjustGroundDelayLimit));
            AddBinding(new TriggerBinding<int>(Mod.kName, kSetAirspaceClassTrigger, SetAirspaceClass));
        }

        protected override void OnUpdate()
        {
            m_Airport = Airports.GetAirport(EntityManager, m_AirplaneStopQuery, selectedEntity);
            visible = m_Airport != Entity.Null;
        }

        /// <summary>Refreshes the panel as soon as there is a new transmission, not only when the panel next updates by itself.</summary>
        protected override void OnPreUpdate()
        {
            if (visible && m_AtcSystem.TryGetRadioVersion(out uint version) && version != m_RadioVersion)
            {
                m_RadioVersion = version;
                m_InfoUISystem.RequestUpdate();
            }
        }

        protected override void Reset()
        {
            m_LineUp = false;
            m_RunwayEnds.Clear();
            m_TowerRadio.Clear();
            m_GroundRadio.Clear();
            m_ApproachRadio.Clear();
            m_DepartureRadio.Clear();
        }

        protected override void OnProcess()
        {
            m_LineUp = IsLineUpEnabled();
            m_GroundDelay = GetGroundDelay();
            IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (runways[i].Airport != m_Airport)
                {
                    continue;
                }
                RunwayGeometry geometry = runways[i].Geometry;
                byte landings = RunwayNames.UsedEnds(geometry);
                if ((landings & RunwayGeometry.kAlong) != 0)
                {
                    AddEnd(runways[i], geometry.m_Direction);
                }
                if ((landings & RunwayGeometry.kAgainst) != 0)
                {
                    AddEnd(runways[i], -geometry.m_Direction);
                }
            }
            // Named as on the radio (RunwayNames), then listed by number and left to right.
            m_RunwayEnds.Sort((a, b) => a.m_Designator != b.m_Designator ? a.m_Designator.CompareTo(b.m_Designator) : a.m_Lateral.CompareTo(b.m_Lateral));

            m_TowerRadio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Airport, AtcFrequency.Tower, kRadioExchanges, Entity.Null);
            m_GroundRadio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Airport, AtcFrequency.Ground, kRadioExchanges, Entity.Null);
            m_HasAirspace = m_AtcSystem.TryGetAirspace(m_Airport, out m_Airspace);
            m_ApproachRadio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Airport, AtcFrequency.Approach, kRadioExchanges, Entity.Null);
            if (m_HasAirspace && m_Airspace.m_Class == AirspaceClass.B)
            {
                m_DepartureRadio.Fill(m_AtcSystem, EntityManager, m_NameSystem, m_PrefabSystem, m_Airport, AtcFrequency.Departure, kRadioExchanges, Entity.Null);
            }
        }

        private void AddEnd(RunwayInfo runway, float2 landing)
        {
            string name = RunwayNames.Name(m_AtcSystem.RunwayList, runway.Key, landing, out int number);
            m_RunwayEnds.Add(new RunwayEnd
            {
                m_Runway = runway,
                m_Landing = landing,
                m_Designator = number,
                m_Name = name,
                m_Lateral = RunwayNames.Lateral(runway.Geometry, landing),
                m_RightHand = IsRightHand(runway, landing),
            });
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

        /// <summary>Right-hand when a parallel runway lies to the left (so the pattern stays outside), or else a neighbouring airport.</summary>
        private bool IsRightHandByDefault(RunwayInfo runway, float2 landing)
        {
            float width = Mod.Setting.PatternWidthNm * Setting.kMetersPerNauticalMile;
            float2 left = Colossal.Mathematics.MathUtils.Left(landing);
            float2 right = -left;
            bool parallelRight = false;
            IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (runways[i] == runway)
                {
                    continue;
                }
                if (RunwayGeometry.IsParallelOnSide(runway.Geometry, runways[i].Geometry, landing, left, width))
                {
                    return true;
                }
                parallelRight |= RunwayGeometry.IsParallelOnSide(runway.Geometry, runways[i].Geometry, landing, right, width);
            }
            // No parallel runway: away from a neighbouring airport on the left (the same rule as the tower's).
            return !parallelRight && m_AtcSystem.IsNeighbourOnLeft(runway.Airport, runway.Geometry.Middle, landing);
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
            writer.PropertyName("groundDelay");
            writer.Write(m_GroundDelay.m_Enabled);
            writer.PropertyName("groundDelayLimit");
            writer.Write((int)m_GroundDelay.m_Limit);
            writer.PropertyName("towerRadio");
            m_TowerRadio.Write(writer);
            writer.PropertyName("groundRadio");
            m_GroundRadio.Write(writer);
            // Every write has the same properties: the UI engine keeps one object layout per type name, and a property
            // that comes and goes (the APP box when the class changes) crashes it natively. Flags say which boxes to show:
            // a class D airport has no approach control, and only class B has its own departure control.
            writer.PropertyName("hasApproach");
            writer.Write(!m_HasAirspace || m_Airspace.m_Class != AirspaceClass.D);
            writer.PropertyName("approachRadio");
            m_ApproachRadio.Write(writer);
            writer.PropertyName("hasDeparture");
            writer.Write(m_HasAirspace && m_Airspace.m_Class == AirspaceClass.B);
            writer.PropertyName("departureRadio");
            m_DepartureRadio.Write(writer);
            writer.PropertyName("airspaceClass");
            writer.Write(m_HasAirspace ? m_Airspace.m_Class.ToString() : string.Empty);
            writer.PropertyName("airspaceRadius");
            writer.Write(m_HasAirspace ? m_Airspace.m_Radius / Setting.kMetersPerNauticalMile : 0f);
            writer.PropertyName("airspaceChosen");
            writer.Write(m_HasAirspace && m_Airspace.m_Chosen);
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

        /// <summary>The airport's present airspace class (D without airspace data, as the tower assumes).</summary>
        private AirspaceClass CurrentClass()
        {
            return m_AtcSystem.TryGetAirspace(m_Airport, out AirportSpace space) ? space.m_Class : AirspaceClass.D;
        }

        private bool IsLineUpEnabled()
        {
            bool hasSetting = EntityManager.HasComponent<LineUpSetting>(m_Airport);
            LineUpSetting setting = hasSetting ? EntityManager.GetComponentData<LineUpSetting>(m_Airport) : default;
            return LineUpSetting.IsEnabled(hasSetting, setting, CurrentClass());
        }

        /// <summary>Flips line up and wait for the airport, as a choice for its present class (a class change brings back the default).</summary>
        private void ToggleLineUp()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            bool enable = !IsLineUpEnabled();
            LineUpSetting setting = new LineUpSetting { m_Enabled = enable, m_Class = (byte)CurrentClass() };
            if (EntityManager.HasComponent<LineUpSetting>(m_Airport))
            {
                EntityManager.SetComponentData(m_Airport, setting);
            }
            else
            {
                EntityManager.AddComponentData(m_Airport, setting);
            }
            Mod.Log.Info($"Line up and wait {(enable ? "enabled" : "disabled")} at airport {m_Airport}");
            // Re-sends every section to the UI; only marking this one dirty would leave the checkbox unchanged on screen.
            m_InfoUISystem.RequestUpdate();
        }

        private GroundDelaySetting GetGroundDelay()
        {
            return EntityManager.HasComponent<GroundDelaySetting>(m_Airport)
                ? EntityManager.GetComponentData<GroundDelaySetting>(m_Airport)
                : GroundDelaySetting.Default(m_AtcSystem.GetGateCount(m_Airport));
        }

        private void SetGroundDelay(GroundDelaySetting setting)
        {
            if (EntityManager.HasComponent<GroundDelaySetting>(m_Airport))
            {
                EntityManager.SetComponentData(m_Airport, setting);
            }
            else
            {
                EntityManager.AddComponentData(m_Airport, setting);
            }
            Mod.Log.Info($"Ground delay at airport {m_Airport}: {(setting.m_Enabled ? "on" : "off")}, limit {setting.m_Limit}");
            m_InfoUISystem.RequestUpdate();
        }

        private void ToggleGroundDelay()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            GroundDelaySetting setting = GetGroundDelay();
            setting.m_Enabled = !setting.m_Enabled;
            SetGroundDelay(setting);
        }

        /// <summary>
        /// Asks for the airport's airspace class: -1 for automatic (from its size), else an <see cref="AirspaceClass"/> value. Only
        /// queued here: changing the airport building and rebuilding the airspaces inside the UI's trigger callback crashed the
        /// game's UI engine (2026-10-19), so it is applied on the next frame outside the UI update (AirspaceOverlaySystem).
        /// </summary>
        private void SetAirspaceClass(int value)
        {
            if (m_Airport != Entity.Null)
            {
                m_AtcSystem.RequestAirspaceClass(m_Airport, value);
            }
        }

        /// <summary>Raises or lowers the limit by <paramref name="delta"/> airplanes, within the allowed range.</summary>
        private void AdjustGroundDelayLimit(int delta)
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            GroundDelaySetting setting = GetGroundDelay();
            setting.m_Limit = (byte)math.clamp(setting.m_Limit + delta, GroundDelaySetting.kMinLimit, GroundDelaySetting.kMaxLimit);
            SetGroundDelay(setting);
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
