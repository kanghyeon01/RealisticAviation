using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace RealisticAviation
{
    [FileLocation("ModsSettings/" + Mod.kName + "/" + Mod.kName)]
    [SettingsUIGroupOrder(kArrivalGroup, kDepartureGroup, kPatternGroup, kGroundGroup, kClosureGroup, kLogGroup, kResetGroup)]
    [SettingsUIShowGroupName(kArrivalGroup, kDepartureGroup, kPatternGroup, kGroundGroup, kClosureGroup, kLogGroup)]
    public sealed class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kArrivalGroup = "Arrivals";
        public const string kGroundGroup = "Ground";
        public const string kPatternGroup = "Pattern";
        public const string kClosureGroup = "Closure";
        public const string kLogGroup = "Log";
        public const string kDepartureGroup = "Departures";
        public const string kResetGroup = "Reset";

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

        /// <summary>
        /// Instrument approach procedure (1.2): arrivals fly an RNAV "Basic T" (initial approach fix, intermediate fix, final
        /// approach fix 1,500 ft above the runway) and descend on final at <see cref="GlidePathDegrees"/> instead of diving at
        /// the game's 20 degrees. Replaces 1.1's "Straight-in final with glide path" (a new name, so that the new default
        /// reaches settings files that saved the old option off).
        /// </summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        public bool InstrumentApproach { get; set; }

        /// <summary>Descent angle on final with <see cref="InstrumentApproach"/> (real approaches: 3 degrees; the game: 20).</summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUISlider(min = 3, max = 20, step = 1, unit = Unit.kInteger)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInstrumentApproachOff))]
        public int GlidePathDegrees { get; set; }

        /// <summary>
        /// Standard instrument arrival (1.2): from its first call an arrival flies to its initial approach fix and, when it
        /// has to wait, holds there (stacked) instead of at its present position on the airway.
        /// </summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInstrumentApproachOff))]
        public bool StandardArrival { get; set; }

        /// <summary>
        /// With <see cref="InstrumentApproach"/>: zoned buildings under each approach, out to the final approach fix, may not
        /// be built or level up above a surface 50 m below the glide path (see ApproachZoneSystem).
        /// </summary>
        [SettingsUISection(kSection, kArrivalGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInstrumentApproachOff))]
        public bool ApproachHeightLimits { get; set; }

        /// <summary>
        /// Standard instrument departure (1.2): after takeoff, a turn of standard rate onto a course that joins the airway,
        /// handed back where it meets it. Replaces 1.1's "Smooth departure turns".
        /// </summary>
        [SettingsUISection(kSection, kDepartureGroup)]
        public bool StandardDeparture { get; set; }

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
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsInstrumentApproachOn))]
        public float FinalLengthNm { get; set; }

        /// <summary>Airplanes leaving a nose-in gate are pushed back instead of turning around on the spot.</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        public bool EnablePushback { get; set; }

        /// <summary>Every airplane's info panel has a "Remove this airplane" button (for one stuck on a runway; AirplaneAtcSection).</summary>
        [SettingsUISection(kSection, kGroundGroup)]
        public bool RemoveAirplaneButton { get; set; }

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

        /// <summary>
        /// Writes each airplane's radio exchanges, events, waits and pushback steps to the log, for reporting problems. Off: only
        /// settings changes, airspaces, closures, ground delays and warnings.
        /// </summary>
        [SettingsUISection(kSection, kLogGroup)]
        public bool DetailedLog { get; set; }

        /// <summary>Puts every option back to its default, after a confirmation.</summary>
        [SettingsUISection(kSection, kResetGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ResetToDefaults
        {
            set
            {
                SetDefaults();
                ApplyAndSave();
            }
        }

        public bool IsClosureTimeUnlimited() => !LimitClosureTime;

        public bool IsInstrumentApproachOn() => InstrumentApproach;

        public bool IsInstrumentApproachOff() => !InstrumentApproach;

        public override void SetDefaults()
        {
            DepartureGapSeconds = 30;
            TaxiSpacing = 40;
            EnablePushback = true;
            PatternWidthNm = 1.5f;
            FinalLengthNm = 2f;
            ArrivalSpacingSeconds = 45;
            WeatherGoArounds = true;
            InstrumentApproach = true;
            GlidePathDegrees = 5;
            ApproachHeightLimits = false;
            StandardDeparture = true;
            StandardArrival = false;
            RemoveAirplaneButton = false;
            HoldingLegNm = 1.5f;
            HoldShortSetback = 30;
            CloseForTornado = true;
            CloseForHail = true;
            CloseForOtherDisasters = true;
            LimitClosureTime = false;
            MaxClosureMinutes = 30;
            DetailedLog = false;
        }
    }
}
