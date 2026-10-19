using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace RealisticAviation
{
    [FileLocation("ModsSettings/" + Mod.kName + "/" + Mod.kName)]
    [SettingsUIGroupOrder(kArrivalGroup, kPatternGroup, kGroundGroup, kClosureGroup)]
    [SettingsUIShowGroupName(kArrivalGroup, kPatternGroup, kGroundGroup, kClosureGroup)]
    public sealed class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kArrivalGroup = "Arrivals";
        public const string kGroundGroup = "Ground";
        public const string kPatternGroup = "Pattern";
        public const string kClosureGroup = "Closure";

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        /// <summary>Metres per nautical mile.</summary>
        public const float kMetersPerNauticalMile = 1852f;

        /// <summary>Simulation frames per in-game minute (a game day is 262,144 frames).</summary>
        public const float kFramesPerGameMinute = 262144f / 1440f;

        /// <summary>Length of the straight legs of the holding pattern, in nautical miles (as in DME/RNAV holds).</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUISlider(min = 0.5f, max = 4f, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float HoldingLegNm { get; set; }

        /// <summary>Minimum time between two arrivals cleared to land on the same runway, in seconds (in-trail spacing).</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUISlider(min = 15, max = 75, step = 5, unit = Unit.kInteger)]
        public int ArrivalSpacingSeconds { get; set; }

        /// <summary>An arrival now and then goes around at the decision height: 0.3% in clear weather, up to 8–15% in bad weather.</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        public bool WeatherGoArounds { get; set; }

        /// <summary>Extra gap between the nose of a waiting airplane and the edge of the runway.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 0, max = 150, step = 5, unit = Unit.kLength)]
        public int HoldShortSetback { get; set; }

        /// <summary>Distance between the runway centerline and the downwind leg, in nautical miles.</summary>
        [SettingsUISection(kSection, kPatternGroup)]
        [SettingsUISlider(min = 0.5f, max = 4f, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float PatternWidthNm { get; set; }

        /// <summary>Length of the final approach after the base turn, in nautical miles.</summary>
        [SettingsUISection(kSection, kPatternGroup)]
        [SettingsUISlider(min = 1f, max = 5f, step = 0.5f, unit = Unit.kFloatSingleFraction)]
        public float FinalLengthNm { get; set; }

        /// <summary>Airplanes leaving a nose-in gate are pushed back instead of turning around on the spot.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        public bool EnablePushback { get; set; }

        /// <summary>Gap a taxiing airplane keeps between its nose and the tail of the airplane ahead (0 = game default).</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 0, max = 150, step = 5, unit = Unit.kLength)]
        public int TaxiSpacing { get; set; }

        /// <summary>A ground airplane may take off or cross ahead of an arrival that is at least this many seconds out.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        [SettingsUISlider(min = 10, max = 50, step = 5, unit = Unit.kInteger)]
        public int DepartureGapSeconds { get; set; }

        /// <summary>A tornado near the airport closes it.</summary>
        [SettingsUISection(kSection, kClosureGroup)]
        public bool CloseForTornado { get; set; }

        /// <summary>A hail storm near the airport closes it.</summary>
        [SettingsUISection(kSection, kClosureGroup)]
        public bool CloseForHail { get; set; }

        /// <summary>Any other disaster near the airport (thunderstorms, or anything putting the airport in danger) closes it.</summary>
        [SettingsUISection(kSection, kClosureGroup)]
        public bool CloseForOtherDisasters { get; set; }

        /// <summary>Off: an airport stays closed as long as the disaster lasts. On: at most <see cref="MaxClosureMinutes"/>.</summary>
        [SettingsUISection(kSection, kClosureGroup)]
        public bool LimitClosureTime { get; set; }

        /// <summary>Longest an airport stays closed for one event, in game minutes (used when the limit is on).</summary>
        [SettingsUISection(kSection, kClosureGroup)]
        [SettingsUISlider(min = 5, max = 180, step = 5, unit = Unit.kInteger)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsClosureTimeUnlimited))]
        public int MaxClosureMinutes { get; set; }

        public bool IsClosureTimeUnlimited() => !LimitClosureTime;

        public override void SetDefaults()
        {
            DepartureGapSeconds = 30;
            TaxiSpacing = 40;
            EnablePushback = true;
            PatternWidthNm = 1.5f;
            FinalLengthNm = 2f;
            ArrivalSpacingSeconds = 45;
            WeatherGoArounds = true;
            HoldingLegNm = 1.5f;
            HoldShortSetback = 30;
            CloseForTornado = true;
            CloseForHail = true;
            CloseForOtherDisasters = true;
            LimitClosureTime = false;
            MaxClosureMinutes = 30;
        }
    }
}
