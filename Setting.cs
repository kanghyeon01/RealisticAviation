using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace RealisticAviation
{
    [FileLocation("ModsSettings/" + Mod.kName + "/" + Mod.kName)]
    [SettingsUIGroupOrder(kArrivalGroup, kGroundGroup)]
    [SettingsUIShowGroupName(kArrivalGroup, kGroundGroup)]
    public sealed class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kArrivalGroup = "Arrivals";
        public const string kGroundGroup = "Ground";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        /// <summary>Metres per nautical mile.</summary>
        public const float kMetersPerNauticalMile = 1852f;

        /// <summary>Path distance to the runway, in nautical miles, at which an arriving airplane calls the tower.</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUISlider(min = 2f, max = 12f, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float ContactDistanceNm { get; set; }

        /// <summary>Length of the straight legs of the holding pattern, in nautical miles (as in DME/RNAV holds).</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUISlider(min = 0.5f, max = 4f, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float HoldingLegNm { get; set; }

        /// <summary>Extra gap between the nose of a waiting airplane and the edge of the runway.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 0, max = 150, step = 5, unit = Unit.kLength)]
        public int HoldShortSetback { get; set; }

        /// <summary>Gap a taxiing airplane keeps between its nose and the tail of the airplane ahead (0 = game default).</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 0, max = 150, step = 5, unit = Unit.kLength)]
        public int TaxiSpacing { get; set; }

        /// <summary>A ground airplane may take off or cross ahead of an arrival that is at least this many seconds out.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 30, max = 300, step = 10, unit = Unit.kInteger)]
        public int DepartureGapSeconds { get; set; }

        public override void SetDefaults()
        {
            DepartureGapSeconds = 90;
            TaxiSpacing = 40;
            ContactDistanceNm = 5.5f;
            HoldingLegNm = 1.5f;
            HoldShortSetback = 30;
        }
    }
}
