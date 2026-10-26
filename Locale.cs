using System.Collections.Generic;
using Colossal;

namespace RealisticAviation
{
    internal abstract class LocaleBase : IDictionarySource
    {
        protected readonly Setting m_Setting;

        protected LocaleBase(Setting setting)
        {
            m_Setting = setting;
        }

        protected abstract Dictionary<string, string> Entries();

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return Entries();
        }

        public void Unload()
        {
        }
    }

    internal sealed class LocaleEN : LocaleBase
    {
        public LocaleEN(Setting setting) : base(setting)
        {
        }

        protected override Dictionary<string, string> Entries() => new Dictionary<string, string>
        {
            { m_Setting.GetSettingsLocaleID(), "Realistic Aviation" },
            { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kArrivalGroup), "Arrivals" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kGroundGroup), "Ground" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kPatternGroup), "Traffic pattern" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kClosureGroup), "Airport closure" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kLogGroup), "Log" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DetailedLog)), "Detailed log" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.DetailedLog)),
                "Writes every airplane's radio exchanges, events, waits and pushback steps to the mod's log file (Logs/RealisticAviation.log), to help report a problem. The file grows quickly at a busy airport. Off: only settings changes, airspaces, closures, ground delays and warnings are logged."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForTornado)), "Close for tornadoes" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForTornado)),
                "An active tornado that reaches within 1 NM (1,852 m) of a runway, or puts the airport building in danger, closes the airport."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForHail)), "Close for hail storms" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForHail)),
                "An active hail storm that reaches within 1 NM (1,852 m) of a runway, or puts the airport building in danger, closes the airport."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForOtherDisasters)), "Close for other disasters" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForOtherDisasters)),
                "Thunderstorms near a runway, and any other disaster that puts the airport building in danger, close the airport."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LimitClosureTime)), "Limit closure time" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.LimitClosureTime)),
                "Off: the airport stays closed as long as the disaster lasts. On: it reopens after the time below, even if the disaster is still going on."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxClosureMinutes)), "Maximum closure (game minutes)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxClosureMinutes)),
                "With the closure time limit on, the airport reopens after this many in-game minutes."
            },

            { "RealisticAviation.Charts.Label", "Approach charts" },
            { "RealisticAviation.Charts.Tooltip", "Opens the instrument approach chart of a runway end: profile, minima, missed approach and the procedure coding tables." },
            { "RealisticAviation.Charts.ChartTab", "Approach chart" },
            { "RealisticAviation.Charts.CodingTab", "Coding tables" },
            { "RealisticAviation.RightHand.Label", "right-hand pattern" },
            {
                "RealisticAviation.RightHand.Tooltip",
                "After a go-around, airplanes landing on this runway end fly the traffic pattern with right turns instead of the " +
                "standard left turns. Where a parallel runway lies on the pattern side, the other side is used automatically."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PatternWidthNm)), "Pattern width (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.PatternWidthNm)),
                "Distance between the runway centerline and the downwind leg. The pattern is flown 1,500 ft above the runway."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FinalLengthNm)), "Final length (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.FinalLengthNm)),
                "How far before the runway the base turn puts the airplane on final."
            },

            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldingLegNm)), "Holding leg length (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldingLegNm)),
                "Length of each straight leg of the holding pattern, as in DME/RNAV holds. Shorter legs keep the holds compact."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ArrivalSpacingSeconds)), "Arrival spacing (seconds)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ArrivalSpacingSeconds)),
                "Minimum time between two arrivals cleared to land on the same runway, so that several airplanes can be on final " +
                "one behind the other. A smaller airplane behind a larger one gets extra spacing for wake turbulence. If the runway " +
                "is still not clear when an arrival reaches 100 ft, it goes around."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WeatherGoArounds)), "Weather go-arounds" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.WeatherGoArounds)),
                "Now and then an arriving airplane goes around at 100 ft on the pilot's own decision: 0.3% of approaches in clear weather, " +
                "more the worse the weather, up to 8% in the heaviest rain, 12% in heavy snow or thick fog and 15% in a thunderstorm."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.InstrumentApproach)), "Instrument approach procedure (IAP)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.InstrumentApproach)),
                "Arrivals fly an RNAV \"T\" approach: to an initial approach fix (left, right or straight in, by where they come " +
                "from), then the intermediate fix on the extended runway centerline, then the final approach fix 1,500 ft above the " +
                "runway, and descend from there at the angle below instead of diving at the game's 20 degrees. The final length " +
                "follows from the angle, also after a go-around."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StandardArrival)), "Standard instrument arrival (STAR)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StandardArrival)),
                "With the instrument approach: from its first call an arrival leaves the airway for its initial approach fix, and " +
                "when the runway is busy it holds there, stacked 1,000 ft apart, instead of where it happens to be."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.GlidePathDegrees)), "Glide path angle (degrees)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.GlidePathDegrees)),
                "Descent angle on final. Real approaches use 3 degrees; the game itself uses 20. A smaller angle means a longer final, " +
                "which may reach past the edge of a small map."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApproachHeightLimits)), "Building height limits under approaches" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ApproachHeightLimits)),
                "Zoned buildings under each approach, from the runway end out to the final approach fix, are not built or levelled up " +
                "higher than 50 m below the glide path. The area widens away from the runway, as an instrument runway's approach " +
                "surface. Existing buildings stay. Only lowers the game's own limits, never raises them."
            },
            { m_Setting.GetOptionGroupLocaleID(Setting.kDepartureGroup), "Departures" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StandardDeparture)), "Standard instrument departure (SID)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StandardDeparture)),
                "After takeoff, departures turn at a standard rate (a turn about 1.4 km in radius) onto a course that joins their " +
                "airway, and climb at most 15 degrees, instead of the game's sharp turns of up to 30 degrees per second."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetToDefaults)), "Reset to defaults" },
            { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetToDefaults)), "Puts every option on this page back to its default value." },
            { m_Setting.GetOptionWarningLocaleID(nameof(Setting.ResetToDefaults)), "Reset all Realistic Aviation options to their defaults?" },
            { "RealisticAviation.GroundDelay.Toggle", "Ground delay" },
            {
                "RealisticAviation.GroundDelay.Tooltip",
                "When too many airplanes move on this airport's ground (taxiing, waiting for the runway or a gate, pushing back; " +
                "not those boarding at gates), approach stops clearing new arrivals, which hold, and departures go first, " +
                "until the ground has two airplanes fewer than the limit."
            },
            { "RealisticAviation.GroundDelay.Limit", "Ground delay limit" },
            { "RealisticAviation.GroundDelay.LimitValue", "{COUNT} airplanes" },
            { "RealisticAviation.GroundDelay.Label", "Ground delay" },
            { "RealisticAviation.GroundDelay.Count", "{COUNT} airplanes moving on the ground" },
            { "RealisticAviation.Closed.Label", "Airport closed" },
            { "RealisticAviation.Radio.Label", "Tower radio" },
            { "RealisticAviation.Airspace.Label", "Airspace" },
            { "RealisticAviation.Airspace.Choose", "Airspace class" },
            { "RealisticAviation.Airspace.Auto", "Auto" },
            { "Infoviews.INFOVIEW[" + AirspaceOverlaySystem.kInfoviewName + "]", "VFR chart" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kAirportsInfomodeName + "]", "Airports (class B, D)" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kAirportsCInfomodeName + "]", "Airports (class C)" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kUrbanInfomodeName + "]", "Built-up areas" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kObstaclesInfomodeName + "]", "Obstacles" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kTerrainInfomodeName + "]", "Terrain elevations" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kWaterInfomodeName + "]", "Water" },
            { "Infoviews.INFOVIEW[" + IfrChartSystem.kInfoviewName + "]", "IFR chart" },
            { "Infoviews.INFOMODE_TYPE[" + IfrChartSystem.kChartTypeKey + "]", "Chart" },
            { "Infoviews.INFOMODE[" + IfrChartSystem.kPaperInfomodeName + "]", "Background" },
            { "RealisticAviation.Airspace.Value", "Class {CLASS}, {RADIUS} NM" },
            {
                "RealisticAviation.Airspace.Tooltip",
                "Set from the airport's size unless chosen below: two or more runways or 16 gates make class B, 8 gates class C, else class D. " +
                "Arrivals call when they are within the radius plus 0.5 NM. Holds are stacked by class (B highest). " +
                "A class D airport has no approach control: its tower handles the arrivals."
            },
            { "RealisticAviation.Radio.Empty", "No recent transmissions" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldShortSetback)), "Hold short setback" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldShortSetback)),
                "Gap between the nose of an airplane holding short and the edge of the runway it is waiting for."
            },
            { "RealisticAviation.Code.Label", "ICAO code" },
            { "RealisticAviation.Code.Tooltip", "The airport's location code (3 or 4 letters or digits). Add-ons can use it. Leave it empty for an automatic code from the airport's name." },
            { "RealisticAviation.LineUp.Label", "Line up and wait" },
            {
                "RealisticAviation.LineUp.Tooltip",
                "When on, a departing airplane may enter the runway and wait lined up as soon as the landing airplane, or the airplane taking off ahead of it, has passed its entry point, " +
                "then takes off the moment the runway is clear. When off, it holds short until the runway is clear. " +
                "On by default at class B and C airports, off at class D; when the airport's class changes, it goes back to the new class's default."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnablePushback)), "Pushback" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.EnablePushback)),
                "An airplane leaving a nose-in gate is pushed back out of the stand with its nose towards the gate, then swung " +
                "around to face along its taxi route, instead of turning around on the spot."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RemoveAirplaneButton)), "\"Remove this airplane\" button" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.RemoveAirplaneButton)),
                "Adds a button to every airplane's info panel that removes the airplane, the way the game removes a stuck vehicle. " +
                "Use it for an airplane stuck on a runway; if you can, send the log afterwards: the airplane's state is written " +
                "there when it is removed."
            },
            { "RealisticAviation.RemoveAirplane", "Remove this airplane" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TaxiSpacing)), "Taxi spacing" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.TaxiSpacing)),
                "Gap a taxiing airplane keeps between its nose and the tail of the airplane ahead of it. 0 keeps the game's own behavior."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DepartureGapSeconds)), "Departure gap before arrivals (seconds)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.DepartureGapSeconds)),
                "An airplane may take off or cross the runway ahead of a cleared arrival only if that arrival is at least " +
                "this many seconds from the runway. Larger values are safer; smaller values move more traffic."
            },
        };
    }

    internal sealed class LocaleKO : LocaleBase
    {
        public LocaleKO(Setting setting) : base(setting)
        {
        }

        protected override Dictionary<string, string> Entries() => new Dictionary<string, string>
        {
            { m_Setting.GetSettingsLocaleID(), "Realistic Aviation" },
            { m_Setting.GetOptionTabLocaleID(Setting.kSection), "메인" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kArrivalGroup), "도착" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kGroundGroup), "지상" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kPatternGroup), "장주 (Traffic Pattern)" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kClosureGroup), "공항 폐쇄" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kLogGroup), "로그" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DetailedLog)), "자세한 로그" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.DetailedLog)),
                "비행기마다 교신, 이벤트, 대기, 푸시백 단계를 모드 로그 파일(Logs/RealisticAviation.log)에 남깁니다. 문제를 알릴 때 도움이 됩니다. 붐비는 공항에서는 파일이 빨리 커집니다. 끄면 설정 변경, 공역, 폐쇄, 지상 혼잡, 경고만 남깁니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForTornado)), "토네이도 시 폐쇄" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForTornado)),
                "진행 중인 토네이도가 활주로 1NM(1,852m) 안까지 다가오거나 공항 건물이 위험 상태가 되면 공항을 폐쇄합니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForHail)), "우박 시 폐쇄" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForHail)),
                "진행 중인 우박 폭풍이 활주로 1NM(1,852m) 안까지 다가오거나 공항 건물이 위험 상태가 되면 공항을 폐쇄합니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.CloseForOtherDisasters)), "기타 재난 시 폐쇄" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.CloseForOtherDisasters)),
                "활주로 근처의 뇌우, 그리고 공항 건물을 위험 상태로 만드는 그 밖의 재난이 오면 공항을 폐쇄합니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LimitClosureTime)), "폐쇄 시간 제한" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.LimitClosureTime)),
                "끄면 재난이 계속되는 동안 공항을 폐쇄합니다. 켜면 재난이 계속되더라도 아래 시간이 지나면 공항을 다시 엽니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxClosureMinutes)), "최대 폐쇄 시간 (게임 내 분)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxClosureMinutes)),
                "폐쇄 시간 제한을 켰을 때, 게임 시간으로 이 시간(분)이 지나면 공항을 다시 엽니다."
            },

            { "RealisticAviation.Charts.Label", "접근 차트" },
            { "RealisticAviation.Charts.Tooltip", "활주로 끝의 계기접근 차트를 엽니다. 단면도, 최저치, 실패접근, 절차 코딩표가 있습니다." },
            { "RealisticAviation.Charts.ChartTab", "접근 차트" },
            { "RealisticAviation.Charts.CodingTab", "코딩표" },
            { "RealisticAviation.RightHand.Label", "우선회 장주" },
            {
                "RealisticAviation.RightHand.Tooltip",
                "이 활주로 방향으로 착륙하다 Go-around 한 비행기가 표준인 좌선회 대신 우선회로 장주를 돕니다. " +
                "장주 쪽에 평행 활주로가 있으면 자동으로 반대쪽에서 돕니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.PatternWidthNm)), "장주 폭 (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.PatternWidthNm)),
                "활주로 중심선과 Downwind 구간 사이의 거리입니다. 장주는 활주로보다 1,500ft 높은 고도에서 돕니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.FinalLengthNm)), "Final 길이 (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.FinalLengthNm)),
                "Base 선회 후 Final에 들어서는 지점이 활주로에서 얼마나 떨어져 있는지입니다."
            },

            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldingLegNm)), "Holding 직선 구간 길이 (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldingLegNm)),
                "Holding 패턴 각 직선 구간의 길이입니다. 실제 DME/RNAV Holding처럼 거리로 지정합니다. 짧을수록 Holding이 작아집니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ArrivalSpacingSeconds)), "도착기 간격(초)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ArrivalSpacingSeconds)),
                "같은 활주로에 착륙 허가를 받는 도착기 사이의 최소 시간 간격입니다. 여러 비행기가 앞뒤로 줄지어 Final에 들어올 수 있습니다. " +
                "큰 비행기 뒤의 작은 비행기는 후류 난기류 때문에 간격을 더 둡니다. 100ft에 도달했을 때 활주로가 비어 있지 않으면 Go-around 합니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.WeatherGoArounds)), "기상 Go-around" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.WeatherGoArounds)),
                "도착기 조종사가 100ft에서 가끔 스스로 Go-around 합니다. 맑은 날은 접근의 0.3%이고, " +
                "날씨가 나쁠수록 늘어나 가장 강한 비에서 8%, 폭설이나 짙은 안개에서 12%, 뇌우에서 15%까지 올라갑니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.InstrumentApproach)), "계기 접근 절차 (IAP)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.InstrumentApproach)),
                "도착기가 RNAV \"T\"자형 접근을 합니다. 들어오는 방향에 따라 왼쪽·오른쪽·직진 IAF로 간 뒤, 활주로 연장선 위의 IF, " +
                "활주로보다 1,500ft 높은 FAF를 거쳐, 게임 기본 20도 급강하 대신 아래 각도로 내려옵니다. Final 길이는 각도에 따라 " +
                "정해지며, Go-around 후 재접근에도 적용됩니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StandardArrival)), "표준 계기 도착 (STAR)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StandardArrival)),
                "계기 접근 절차와 함께 씁니다. 도착기가 첫 교신 때부터 항로를 벗어나 자기 IAF로 가고, 활주로가 바쁘면 " +
                "아무 데서나 대기하지 않고 IAF에서 1,000ft 간격으로 층을 쌓아 Holding합니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.GlidePathDegrees)), "강하각(도)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.GlidePathDegrees)),
                "Final에서 내려오는 각도입니다. 실제 접근은 3도, 게임 기본은 20도입니다. 각도가 작을수록 Final이 길어져 " +
                "작은 맵에서는 맵 가장자리 밖까지 나갈 수 있습니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ApproachHeightLimits)), "접근로 아래 건물 높이 제한" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ApproachHeightLimits)),
                "활주로 끝에서 FAF까지 각 접근로 아래의 구역 건물은 강하선보다 50m 낮은 높이를 넘어 새로 짓거나 레벨업하지 못합니다. " +
                "계기 활주로의 접근 표면처럼 활주로에서 멀어질수록 넓어집니다. 이미 지어진 건물은 그대로 둡니다. " +
                "게임의 기존 제한보다 낮추기만 하고, 높이지는 않습니다."
            },
            { m_Setting.GetOptionGroupLocaleID(Setting.kDepartureGroup), "출발" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StandardDeparture)), "표준 계기 출발 (SID)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StandardDeparture)),
                "이륙한 비행기가 게임 기본의 급선회(초당 최대 30도) 대신 표준 선회율(반경 약 1.4km)로 돌아 항로에 합류하는 " +
                "코스로 가고, 15도보다 가파르게 상승하지 않습니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ResetToDefaults)), "기본값으로 되돌리기" },
            { m_Setting.GetOptionDescLocaleID(nameof(Setting.ResetToDefaults)), "이 페이지의 모든 옵션을 기본값으로 되돌립니다." },
            { m_Setting.GetOptionWarningLocaleID(nameof(Setting.ResetToDefaults)), "Realistic Aviation의 모든 옵션을 기본값으로 되돌릴까요?" },
            { "RealisticAviation.GroundDelay.Toggle", "지상 항공교통 관리" },
            {
                "RealisticAviation.GroundDelay.Tooltip",
                "켜 두면, 이 공항 지상에서 움직이는 비행기(택싱, 활주로·게이트 대기, Pushback 중. 게이트에서 탑승 중인 비행기는 제외)가 너무 많으면, " +
                "접근관제가 새 도착기에 접근 허가를 주지 않고 Holding 시키며 출발기를 먼저 내보냅니다. " +
                "지상 비행기가 기준보다 2대 적어지면 해제합니다."
            },
            { "RealisticAviation.GroundDelay.Limit", "관리 시작 기준" },
            { "RealisticAviation.GroundDelay.LimitValue", "{COUNT}대" },
            { "RealisticAviation.GroundDelay.Label", "지상 항공교통 관리 중" },
            { "RealisticAviation.GroundDelay.Count", "지상 이동 중 {COUNT}대" },
            { "RealisticAviation.Closed.Label", "공항 폐쇄" },
            { "RealisticAviation.Radio.Label", "관제 교신" },
            { "RealisticAviation.Airspace.Label", "관제공역" },
            { "RealisticAviation.Airspace.Choose", "공역 등급" },
            { "RealisticAviation.Airspace.Auto", "자동" },
            { "Infoviews.INFOVIEW[" + AirspaceOverlaySystem.kInfoviewName + "]", "VFR 차트" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kAirportsInfomodeName + "]", "공항 (B·D 등급)" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kAirportsCInfomodeName + "]", "공항 (C 등급)" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kUrbanInfomodeName + "]", "시가지" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kObstaclesInfomodeName + "]", "장애물" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kTerrainInfomodeName + "]", "지형 표고" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kWaterInfomodeName + "]", "수면" },
            { "Infoviews.INFOVIEW[" + IfrChartSystem.kInfoviewName + "]", "IFR 차트" },
            { "Infoviews.INFOMODE_TYPE[" + IfrChartSystem.kChartTypeKey + "]", "차트" },
            { "Infoviews.INFOMODE[" + IfrChartSystem.kPaperInfomodeName + "]", "배경" },
            { "RealisticAviation.Airspace.Value", "Class {CLASS}, 반경 {RADIUS}NM" },
            {
                "RealisticAviation.Airspace.Tooltip",
                "아래에서 직접 고르지 않으면 공항 규모로 자동 결정됩니다. 활주로 2개 이상 또는 게이트 16개 이상이면 B, 게이트 8개 이상이면 C, 그 밖은 D입니다. " +
                "도착기는 반경 + 0.5NM 안에 들어오면 교신을 시작합니다. Holding 고도는 등급별로 나뉘며 B가 가장 높습니다. " +
                "D 공항은 접근관제가 없어 관제탑이 도착기를 직접 처리합니다."
            },
            { "RealisticAviation.Radio.Empty", "최근 교신 없음" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldShortSetback)), "Hold Short 정지 여유 거리" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldShortSetback)),
                "활주로 진입을 기다리는 비행기의 기수와 활주로 가장자리 사이에 두는 거리입니다."
            },
            { "RealisticAviation.Code.Label", "ICAO 코드" },
            { "RealisticAviation.Code.Tooltip", "공항 식별 코드(영문자나 숫자 3~4자리)입니다. 다양한 애드온에서 활용할 수 있습니다. 비워 두면 공항 이름으로 자동 코드를 만듭니다." },
            { "RealisticAviation.LineUp.Label", "Line Up and Wait" },
            {
                "RealisticAviation.LineUp.Tooltip",
                "켜면 착륙기나 앞서 이륙하는 비행기가 진입 지점을 지나가는 즉시 출발기가 활주로에 들어가 정렬한 채 기다리다가, 활주로가 비는 순간 이륙합니다. " +
                "끄면 활주로가 빌 때까지 Hold Short로 기다립니다. " +
                "기본값은 Class B·C 공항은 켜짐, Class D 공항은 꺼짐이고, 공항 등급이 바뀌면 바뀐 등급의 기본값으로 돌아갑니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnablePushback)), "Pushback" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.EnablePushback)),
                "게이트에 기수를 대고 서 있던 비행기가 출발할 때, 제자리에서 돌지 않고 기수를 게이트 쪽에 둔 채 뒤로 밀려 나온 뒤 " +
                "택싱 방향으로 돌려집니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RemoveAirplaneButton)), "\"이 비행기 제거\" 버튼" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.RemoveAirplaneButton)),
                "모든 비행기 정보란에 비행기를 제거하는 버튼을 추가합니다. 게임이 막힌 차량을 없앨 때와 같은 방식으로 제거합니다. " +
                "활주로에 멈춘 비행기가 있을 때 쓰세요. 제거할 때 비행기 상태가 로그에 남으니, 가능하면 로그를 보내 주세요."
            },
            { "RealisticAviation.RemoveAirplane", "이 비행기 제거" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TaxiSpacing)), "택싱 간격" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.TaxiSpacing)),
                "택싱 중인 비행기가 앞 비행기 꼬리와 자기 기수 사이에 두는 거리입니다. 0이면 게임 기본 동작을 따릅니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DepartureGapSeconds)), "도착기 앞 출발 간격(초)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.DepartureGapSeconds)),
                "착륙 허가를 받은 도착기가 활주로까지 이 시간 이상 남았을 때만, 그 앞에서 다른 비행기가 이륙하거나 활주로를 건널 수 있습니다. " +
                "값이 클수록 안전하고, 작을수록 더 많은 비행기를 처리합니다."
            },
        };
    }
}
