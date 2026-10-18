using System.Collections.Generic;
using Colossal;

namespace RealisticAviation
{
    public abstract class LocaleBase : IDictionarySource
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

    public sealed class LocaleEN : LocaleBase
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
            { m_Setting.GetOptionGroupLocaleID(Setting.kTestGroup), "Testing (temporary)" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestDisaster)), "Test disaster" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.TestDisaster)),
                "Weather event the button below starts, to test the airport closure."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StartTestDisaster)), "Start test disaster at airport" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StartTestDisaster)),
                "Starts the chosen weather event centered on the selected airport (or, with none selected, the first airport). " +
                "It is a real disaster and damages the city: use a test save."
            },

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
                "In rain or snow, an arriving airplane now and then goes around at 100 ft because the pilot can't see the runway: " +
                "0.5% of approaches in light rain up to 3% in heavy rain, half as much again in snow (at most 5%). Never in dry weather."
            },
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
            { "Infoviews.INFOVIEW[" + AirspaceOverlaySystem.kInfoviewName + "]", "Airspace" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kInfomodeName + "]", "Airplane stops" },
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
            { "RealisticAviation.LineUp.Label", "Line up and wait" },
            {
                "RealisticAviation.LineUp.Tooltip",
                "When on, a departing airplane may enter the runway and wait lined up as soon as the landing airplane, or the airplane taking off ahead of it, has passed its entry point, " +
                "then takes off the moment the runway is clear. When off, it holds short until the runway is clear."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnablePushback)), "Pushback" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.EnablePushback)),
                "An airplane leaving a nose-in gate is pushed back out of the stand with its nose towards the gate, then swung " +
                "around to face along its taxi route, instead of turning around on the spot."
            },
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

    public sealed class LocaleKO : LocaleBase
    {
        public LocaleKO(Setting setting) : base(setting)
        {
        }

        protected override Dictionary<string, string> Entries() => new Dictionary<string, string>
        {
            { m_Setting.GetSettingsLocaleID(), "리얼리스틱 항공 (Realistic Aviation)" },
            { m_Setting.GetOptionTabLocaleID(Setting.kSection), "메인" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kArrivalGroup), "도착" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kGroundGroup), "지상" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kPatternGroup), "장주 (Traffic Pattern)" },
            { m_Setting.GetOptionGroupLocaleID(Setting.kClosureGroup), "공항 폐쇄" },
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
            { m_Setting.GetOptionGroupLocaleID(Setting.kTestGroup), "테스트 (임시)" },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TestDisaster)), "테스트 재난" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.TestDisaster)),
                "공항 폐쇄를 시험하려고 아래 버튼으로 일으킬 기상 재난입니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StartTestDisaster)), "공항에 테스트 재난 일으키기" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.StartTestDisaster)),
                "고른 기상 재난을 선택된 공항(선택된 공항이 없으면 첫 번째 공항)을 중심으로 일으킵니다. " +
                "실제 재난이라 도시에 피해가 생기니 테스트용 저장 파일에서 사용하세요."
            },

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
                "비나 눈이 오면 조종사가 활주로를 보지 못해 100ft에서 가끔 Go-around 합니다. " +
                "약한 비 0.5%에서 강한 비 3%까지, 눈은 그 1.5배(최대 5%)입니다. 맑은 날에는 일어나지 않습니다."
            },
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
            { "Infoviews.INFOVIEW[" + AirspaceOverlaySystem.kInfoviewName + "]", "관제공역" },
            { "Infoviews.INFOMODE[" + AirspaceOverlaySystem.kInfomodeName + "]", "비행기 정류장" },
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
            { "RealisticAviation.LineUp.Label", "Line Up and Wait" },
            {
                "RealisticAviation.LineUp.Tooltip",
                "켜면 착륙기나 앞서 이륙하는 비행기가 진입 지점을 지나가는 즉시 출발기가 활주로에 들어가 정렬한 채 기다리다가, 활주로가 비는 순간 이륙합니다. " +
                "끄면 활주로가 빌 때까지 Hold Short로 기다립니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.EnablePushback)), "Pushback" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.EnablePushback)),
                "게이트에 기수를 대고 서 있던 비행기가 출발할 때, 제자리에서 돌지 않고 기수를 게이트 쪽에 둔 채 뒤로 밀려 나온 뒤 " +
                "택싱 방향으로 돌려집니다."
            },
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
