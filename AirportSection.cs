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
    /// banner is <see cref="AirportClosedSection"/>). Options: line up and wait and long pushback (with pushback on) for the
    /// airport, and the traffic pattern direction for each runway end (listed as RWY 09, RWY 27, ...).
    /// RealisticAviation.mjs draws the checkboxes; a click comes back through a trigger binding and updates
    /// <see cref="LineUpSetting"/> or <see cref="LongPushbackAirport"/> on the airport building or <see cref="RunwayPattern"/> on
    /// the runway's edges.
    /// </summary>
    public partial class AirportSection : InfoSectionBase
    {
        public const string kToggleLineUpTrigger = "ToggleLineUpAndWait";
        public const string kToggleLongPushbackTrigger = "ToggleLongPushback";
        public const string kToggleRightHandTrigger = "ToggleRunwayRightHand";
        public const string kToggleGroundDelayTrigger = "ToggleGroundDelay";
        public const string kAdjustGroundDelayTrigger = "AdjustGroundDelayLimit";
        public const string kSetAirspaceClassTrigger = "SetAirspaceClass";
        public const string kSetAirportCodeTrigger = "SetAirportCode";
        public const string kOpenApproachChartTrigger = "OpenApproachChart";

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
        private bool m_LongPushback;
        private GroundDelaySetting m_GroundDelay;
        private string m_EnteredCode;
        private string m_AutomaticCode;
        private bool m_InstrumentApproach;
        private ApproachChartWindowSystem m_ChartWindow;

        protected override string group => nameof(AirportSection);

        protected override void OnCreate()
        {
            base.OnCreate();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            m_AirplaneStopQuery = Airports.CreateAirplaneStopQuery(EntityManager);
            m_InfoUISystem.AddMiddleSection(this);
            AddBinding(new TriggerBinding(Mod.kName, kToggleLineUpTrigger, ToggleLineUp));
            AddBinding(new TriggerBinding(Mod.kName, kToggleLongPushbackTrigger, ToggleLongPushback));
            AddBinding(new TriggerBinding<int>(Mod.kName, kToggleRightHandTrigger, ToggleRightHand));
            AddBinding(new TriggerBinding(Mod.kName, kToggleGroundDelayTrigger, ToggleGroundDelay));
            AddBinding(new TriggerBinding<int>(Mod.kName, kAdjustGroundDelayTrigger, AdjustGroundDelayLimit));
            AddBinding(new TriggerBinding<int>(Mod.kName, kSetAirspaceClassTrigger, SetAirspaceClass));
            AddBinding(new TriggerBinding<string>(Mod.kName, kSetAirportCodeTrigger, SetAirportCode));
            AddBinding(new TriggerBinding<int>(Mod.kName, kOpenApproachChartTrigger, OpenApproachChart));
            m_ChartWindow = World.GetOrCreateSystemManaged<ApproachChartWindowSystem>();
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
            m_LongPushback = AirportSettings.IsLongPushback(EntityManager, m_Airport);
            m_EnteredCode = AirportCodes.GetEntered(EntityManager, m_Airport) ?? string.Empty;
            m_AutomaticCode = AirportCodes.GetAutomatic(EntityManager, m_NameSystem, m_Airport);
            m_GroundDelay = GetGroundDelay();
            m_InstrumentApproach = m_AtcSystem.GetOperatingValues(m_Airport).InstrumentApproach;
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
                m_RightHand = AirportSettings.IsRightHand(EntityManager, m_AtcSystem, runway, landing),
            });
        }

        public override void OnWriteProperties(IJsonWriter writer)
        {
            writer.PropertyName("code");
            writer.Write(m_EnteredCode ?? string.Empty);
            writer.PropertyName("automaticCode");
            writer.Write(m_AutomaticCode ?? string.Empty);
            writer.PropertyName("lineUp");
            writer.Write(m_LineUp);
            // Long pushback: written always (a property that comes and goes crashes the UI), shown only with pushback on.
            writer.PropertyName("longPushback");
            writer.Write(m_LongPushback);
            writer.PropertyName("pushback");
            writer.Write(Mod.Setting != null && Mod.Setting.EnablePushback);
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
            // An approach chart per runway end (IFR chart window) where the airport flies instrument approaches.
            writer.PropertyName("instrumentApproach");
            writer.Write(m_InstrumentApproach);
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

        private bool IsLineUpEnabled()
        {
            return AirportSettings.IsLineUpEnabled(EntityManager, m_AtcSystem, m_Airport);
        }

        /// <summary>Flips line up and wait for the airport, as a choice for its present class (a class change brings back the default).</summary>
        private void ToggleLineUp()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            AirportSettings.SetLineUp(EntityManager, m_AtcSystem, m_Airport, !IsLineUpEnabled());
            // Re-sends every section to the UI; only marking this one dirty would leave the checkbox unchanged on screen.
            m_InfoUISystem.RequestUpdate();
        }

        /// <summary>Flips long pushback for the airport.</summary>
        private void ToggleLongPushback()
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            AirportSettings.SetLongPushback(EntityManager, m_Airport, !AirportSettings.IsLongPushback(EntityManager, m_Airport));
            m_InfoUISystem.RequestUpdate();
        }

        private GroundDelaySetting GetGroundDelay()
        {
            return AirportSettings.GetGroundDelay(EntityManager, m_AtcSystem, m_Airport);
        }

        private void SetGroundDelay(GroundDelaySetting setting)
        {
            AirportSettings.SetGroundDelay(EntityManager, m_Airport, setting);
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

        /// <summary>
        /// Sets the airport's code from the panel's text field: 3–4 letters or digits; empty brings back the automatic code;
        /// anything else is ignored (the field then shows the code it had).
        /// </summary>
        private void SetAirportCode(string text)
        {
            if (m_Airport == Entity.Null || !EntityManager.Exists(m_Airport))
            {
                return;
            }
            AirportSettings.SetAirportCode(EntityManager, m_Airport, text);
            m_InfoUISystem.RequestUpdate();
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

        /// <summary>Opens the approach chart window on one runway end of the panel's list.</summary>
        private void OpenApproachChart(int index)
        {
            if (index < 0 || index >= m_RunwayEnds.Count)
            {
                return;
            }
            RunwayEnd end = m_RunwayEnds[index];
            m_ChartWindow.Open(m_Airport, end.m_Runway.Key, math.dot(end.m_Landing, end.m_Runway.Geometry.m_Direction) > 0f);
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
            AirportSettings.SetRightHand(EntityManager, end.m_Runway, end.m_Landing, rightHand);
            Mod.Log.Info($"RWY {end.m_Name} at airport {m_Airport}: {(rightHand ? "right" : "left")}-hand traffic pattern");
            m_InfoUISystem.RequestUpdate();
        }
    }
}
