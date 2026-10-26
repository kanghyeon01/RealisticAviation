using System.Collections.Generic;
using Colossal;
using Colossal.UI.Binding;
using Game.Prefabs;
using Game.SceneFlow;
using Game.Tools;
using Game.UI;
using Unity.Entities;
using UnityEngine;

namespace RealisticAviation
{
    /// <summary>The kind of one IFR chart: the airport's SIDs, its STARs, or the approach to one runway end.</summary>
    internal enum IfrChartKind : byte
    {
        Sid,
        Star,
        Approach,
    }

    /// <summary>One chart of the IFR chart infoview, e.g. "RKSS RNP RWY 07".</summary>
    internal struct IfrChart
    {
        public Entity m_Airport;
        public IfrChartKind m_Kind;

        /// <summary>The runway and its end (along or against its geometry's direction), for an approach chart.</summary>
        public Entity m_Runway;
        public bool m_Along;
        public string m_Name;
    }

    /// <summary>
    /// "IFR chart" infoview (next update): a button in the game's infoview menu whose legend lists the city's charts, one
    /// checkbox each, named "airport code + chart" as on the AIP: "RKSS SID", "RKSS STAR", "RKSS RNP RWY 07" (one approach
    /// chart per runway end in use). The checkboxes are the game's own: a fixed number of infomodes made at load as slots,
    /// their names set per city through the localization; RealisticAviation.mjs hides the slots not in use.
    /// </summary>
    public partial class IfrChartSystem : UISystemBase
    {
        public const string kInfoviewName = "RealisticAviationIfr";
        public const string kChartInfomodePrefix = "RealisticAviationIfrChart";
        public const string kChartTypeKey = "RealisticAviationChart";
        public const string kPaperInfomodeName = "RealisticAviationIfrPaper";
        public const string kChartCountBinding = "IfrChartCount";

        /// <summary>Charts listed at most (a city rarely has more than a few airports).</summary>
        internal const int kChartSlots = 32;

        // The list is checked this often (frames) while the infoview is open: airport codes, runways and options can change.
        private const int kRefreshFrames = 64;

        private static InfoviewPrefab s_Infoview;
        private static InfomodePrefab s_Paper;
        private static InfomodePrefab s_PaperWater;

        /// <summary>
        /// The paper as the screen should show it: a faint off-white (user, 2026-10-27: pure white hurt the eyes once the
        /// charts' tonemapping was off; before, white ten times as bright was pressed down by it). The ground and the water
        /// each reach it through their own gain, from the VFR chart's calibration. On screen it came out a warm paper colour
        /// (water #feeed4, ground #fefddd), which the user kept.
        /// </summary>
        internal static readonly Color kPaperColor = new Color(0xf0 / 255f, 0xf0 / 255f, 0xec / 255f, 1f);
        private static readonly Vector3 kPaperGain = new Vector3(1.2f, 1.5f, 2.0f);
        private static readonly Vector3 kPaperWaterGain = new Vector3(0.5f, 0.5f, 0.55f);
        public const string kPaperWaterInfomodeName = "RealisticAviationIfrPaperWater";
        private static readonly ChartInfomodePrefab[] s_Slots = new ChartInfomodePrefab[kChartSlots];

        private ToolSystem m_ToolSystem;
        private PrefabSystem m_PrefabSystem;
        private AtcSystem m_AtcSystem;
        private ValueBinding<int> m_CountBinding;
        private readonly List<IfrChart> m_Charts = new List<IfrChart>();
        private readonly List<IfrChart> m_Building = new List<IfrChart>();
        private readonly List<Entity> m_Airports = new List<Entity>();
        private readonly List<IfrChart> m_Approaches = new List<IfrChart>();
        private ChartNameSource m_Names;
        private bool m_WasActive;
        private int m_Countdown;

        /// <summary>Adds the infoview and its chart slots to the game's prefabs.</summary>
        internal static void CreateInfoview(PrefabSystem prefabSystem)
        {
            // First the paper background (on when the infoview opens; its water twin is hidden from the legend and follows
            // it), then the chart slots.
            InfomodeInfo[] infomodes = new InfomodeInfo[kChartSlots + 2];
            // Terrain colour entries, as the game's heatmaps: the paper colour over the ground and the water (IfrChartPaperSystem).
            s_Paper = AddPaper(prefabSystem, kPaperInfomodeName, false, kPaperGain);
            s_PaperWater = AddPaper(prefabSystem, kPaperWaterInfomodeName, true, kPaperWaterGain);
            infomodes[kChartSlots] = new InfomodeInfo { m_Mode = s_Paper, m_Priority = 0 };
            infomodes[kChartSlots + 1] = new InfomodeInfo { m_Mode = s_PaperWater, m_Priority = 0 };
            for (int i = 0; i < kChartSlots; i++)
            {
                ChartInfomodePrefab slot = ScriptableObject.CreateInstance<ChartInfomodePrefab>();
                slot.name = SlotName(i);
                slot.m_IncludeInGroups = new InfomodeGroupPrefab[0];
                prefabSystem.AddPrefab(slot);
                s_Slots[i] = slot;
                // Supplemental: off whenever the infoview is opened from the menu, so the player ticks the charts wanted.
                infomodes[i] = new InfomodeInfo { m_Mode = slot, m_Priority = i + 1, m_Supplemental = true };
            }
            InfoviewPrefab infoview = ScriptableObject.CreateInstance<InfoviewPrefab>();
            infoview.name = kInfoviewName;
            infoview.m_IconPath = "Media/Game/Icons/AirplaneStop.svg";
            infoview.m_Group = 100;
            infoview.m_Priority = 2;
            infoview.m_WarningCategories = new IconCategory[0];
            infoview.m_Infomodes = infomodes;
            prefabSystem.AddPrefab(infoview);
            s_Infoview = infoview;
        }

        internal static string SlotName(int slot) => kChartInfomodePrefix + slot.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Whether the infoview is open.</summary>
        internal static bool IsActive(ToolSystem toolSystem) => s_Infoview != null && toolSystem.activeInfoview == s_Infoview;

        /// <summary>The white background entry of the legend.</summary>
        internal static InfomodePrefab Paper => s_Paper;

        /// <summary>The paper's water entry (hidden in the legend; on while the paper is).</summary>
        internal static InfomodePrefab PaperWater => s_PaperWater;

        private static PaperInfomodePrefab AddPaper(PrefabSystem prefabSystem, string name, bool water, Vector3 gain)
        {
            PaperInfomodePrefab paper = ScriptableObject.CreateInstance<PaperInfomodePrefab>();
            paper.name = name;
            paper.m_IncludeInGroups = new InfomodeGroupPrefab[0];
            paper.m_Low = paper.m_Medium = paper.m_High = kPaperColor;
            paper.m_LegendType = GradientLegendType.Fields;
            paper.m_Water = water;
            paper.m_Gain = gain;
            prefabSystem.AddPrefab(paper);
            return paper;
        }


        /// <summary>The charts listed, in slot order.</summary>
        internal IReadOnlyList<IfrChart> Charts => m_Charts;

        /// <summary>Whether the chart in this slot is ticked in the legend.</summary>
        internal bool IsShown(int slot)
        {
            return slot < m_Charts.Count && EntityManager.HasComponent<InfomodeActive>(m_PrefabSystem.GetEntity(s_Slots[slot]));
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_AtcSystem = World.GetOrCreateSystemManaged<AtcSystem>();
            AddBinding(m_CountBinding = new ValueBinding<int>(Mod.kName, kChartCountBinding, 0));
        }

        protected override void OnUpdate()
        {
            if (!IsActive(m_ToolSystem))
            {
                m_WasActive = false;
                return;
            }
            if (m_WasActive && --m_Countdown > 0)
            {
                return;
            }
            m_WasActive = true;
            m_Countdown = kRefreshFrames;
            RefreshCharts();
        }

        /// <summary>Lists the city's charts again (also for the approach chart window while the infoview is closed).</summary>
        internal void RefreshCharts()
        {
            BuildCharts();
            if (Same(m_Building, m_Charts))
            {
                return;
            }
            m_Charts.Clear();
            m_Charts.AddRange(m_Building);
            PublishNames();
            m_CountBinding.Update(m_Charts.Count);
        }

        /// <summary>
        /// The city's charts into <see cref="m_Building"/>: for each airport with runways (by code), its SID and STAR charts
        /// where it flies them, then an approach chart per runway end in use, by designator.
        /// </summary>
        private void BuildCharts()
        {
            m_Building.Clear();
            m_Airports.Clear();
            IReadOnlyList<RunwayInfo> runways = m_AtcSystem.RunwayList;
            for (int i = 0; i < runways.Count; i++)
            {
                if (!m_Airports.Contains(runways[i].Airport))
                {
                    m_Airports.Add(runways[i].Airport);
                }
            }
            List<(string code, Entity airport)> airports = new List<(string, Entity)>(m_Airports.Count);
            for (int i = 0; i < m_Airports.Count; i++)
            {
                airports.Add((AirportCodes.Get(EntityManager, m_AtcSystem.NameSystem, m_Airports[i]), m_Airports[i]));
            }
            airports.Sort((a, b) => string.CompareOrdinal(a.code, b.code));
            for (int a = 0; a < airports.Count; a++)
            {
                (string code, Entity airport) = airports[a];
                OperatingValues values = m_AtcSystem.GetOperatingValues(airport);
                if (values.StandardDeparture)
                {
                    m_Building.Add(new IfrChart { m_Airport = airport, m_Kind = IfrChartKind.Sid, m_Name = code + " SID" });
                }
                if (values.StandardArrival)
                {
                    m_Building.Add(new IfrChart { m_Airport = airport, m_Kind = IfrChartKind.Star, m_Name = code + " STAR" });
                }
                if (!values.InstrumentApproach)
                {
                    continue;
                }
                m_Approaches.Clear();
                for (int i = 0; i < runways.Count; i++)
                {
                    RunwayInfo runway = runways[i];
                    if (runway.Airport != airport)
                    {
                        continue;
                    }
                    byte ends = RunwayNames.UsedEnds(runway.Geometry);
                    for (int k = 0; k < 2; k++)
                    {
                        bool along = k == 0;
                        if ((ends & (along ? RunwayGeometry.kAlong : RunwayGeometry.kAgainst)) == 0)
                        {
                            continue;
                        }
                        string designator = RunwayNames.Name(runways, runway.Key, along ? runway.Geometry.m_Direction : -runway.Geometry.m_Direction);
                        m_Approaches.Add(new IfrChart
                        {
                            m_Airport = airport,
                            m_Kind = IfrChartKind.Approach,
                            m_Runway = runway.Key,
                            m_Along = along,
                            m_Name = code + " RNP RWY " + designator,
                        });
                    }
                }
                m_Approaches.Sort((x, y) => string.CompareOrdinal(x.m_Name, y.m_Name));
                m_Building.AddRange(m_Approaches);
            }
            if (m_Building.Count > kChartSlots)
            {
                Mod.Log.Warn($"IFR chart: {m_Building.Count} charts, only the first {kChartSlots} are listed.");
                m_Building.RemoveRange(kChartSlots, m_Building.Count - kChartSlots);
            }
        }

        private static bool Same(List<IfrChart> a, List<IfrChart> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].m_Name != b[i].m_Name || a[i].m_Runway != b[i].m_Runway || a[i].m_Along != b[i].m_Along || a[i].m_Airport != b[i].m_Airport)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The slots' names into the localization, as a new source added to the active and the fallback locale (adding one
        /// loads only it, where removing one reloads every source). The source it replaces is emptied, so a later full
        /// reload (another language) still ends with the current names.
        /// </summary>
        private void PublishNames()
        {
            Dictionary<string, string> entries = new Dictionary<string, string>(m_Charts.Count);
            for (int i = 0; i < m_Charts.Count; i++)
            {
                entries["Infoviews.INFOMODE[" + SlotName(i) + "]"] = m_Charts[i].m_Name;
            }
            m_Names?.Clear();
            m_Names = new ChartNameSource(entries);
            Colossal.Localization.LocalizationManager manager = GameManager.instance.localizationManager;
            manager.AddSource(manager.activeLocaleId, m_Names);
            if (manager.fallbackLocaleId != manager.activeLocaleId)
            {
                manager.AddSource(manager.fallbackLocaleId, m_Names);
            }
        }

        /// <summary>The chart names of one version of the list.</summary>
        private sealed class ChartNameSource : IDictionarySource
        {
            private readonly Dictionary<string, string> m_Entries;

            public ChartNameSource(Dictionary<string, string> entries)
            {
                m_Entries = entries;
            }

            public void Clear() => m_Entries.Clear();

            public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
            {
                return m_Entries;
            }

            public void Unload()
            {
            }
        }
    }

    /// <summary>
    /// A chart slot in the IFR chart legend: an infomode the game colours nothing for and shows without a colour swatch,
    /// typed "Chart".
    /// </summary>
    internal sealed class ChartInfomodePrefab : InfomodeBasePrefab
    {
        public override string infomodeTypeLocaleKey => IfrChartSystem.kChartTypeKey;
    }

    /// <summary>
    /// The IFR chart's paper background: a terrain colour entry, as the game's heatmaps (telecom coverage, natural
    /// resources), for the ground (colour group 0), or the water (colour group 1) with <see cref="m_Water"/>. Not a heatmap
    /// prefab, so the game's OverlayInfomodeSystem writes nothing for it; <see cref="IfrChartPaperSystem"/> fills its channels.
    /// </summary>
    internal sealed class PaperInfomodePrefab : GradientInfomodeBasePrefab
    {
        /// <summary>The water's entry (colour group 1) instead of the ground's: the water is lit differently and needs its own gain.</summary>
        public bool m_Water;

        /// <summary>Per-channel gain in linear light, so the screen shows the paper colour (calibrated from screenshots).</summary>
        public Vector3 m_Gain = Vector3.one;

        public override string infomodeTypeLocaleKey => "TerrainColor";

        public override int GetColorGroup(out int secondaryGroup)
        {
            secondaryGroup = -1;
            return m_Water ? 1 : 0;
        }

        public override void GetColors(out Color color0, out Color color1, out Color color2, out float steps, out float speed, out float tiling, out float fill)
        {
            base.GetColors(out color0, out color1, out color2, out steps, out speed, out tiling, out fill);
            // The gain in linear light (the game hands the shader the colour's linear value).
            Vector3 gain = m_Gain;
            Color linear = color1.linear;
            Color gamma = new Color(linear.r * gain.x, linear.g * gain.y, linear.b * gain.z, 1f).gamma;
            color0 = color1 = color2 = new Color(gamma.r, gamma.g, gamma.b, 1f);
        }
    }
}
