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

            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ContactDistanceNm)), "Tower contact distance (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ContactDistanceNm)),
                "How far before the runway (along its flight path) an arriving airplane asks the tower to land. " +
                "If the runway is not free by then, the airplane holds where it is (on its arrival path) until it is cleared. " +
                "A smaller distance puts the holds closer to the airport. Airplanes holding at the same runway are stacked 1,000 ft apart."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldingLegNm)), "Holding leg length (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldingLegNm)),
                "Length of each straight leg of the holding pattern, as in DME/RNAV holds. Shorter legs keep the holds compact."
            },
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

            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ContactDistanceNm)), "관제탑 교신 거리 (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.ContactDistanceNm)),
                "도착하는 비행기가 활주로까지 비행 경로상 이 거리만큼 남았을 때 관제탑에 착륙을 요청합니다. " +
                "그때 활주로가 비어 있지 않으면, 착륙 허가가 날 때까지 그 자리(도착 경로 위)에서 Holding합니다. " +
                "값을 줄이면 Holding 위치가 공항에 더 가까워집니다. 같은 활주로를 기다리는 비행기들은 1,000ft 간격으로 고도를 나눕니다."
            },
            { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HoldingLegNm)), "Holding 직선 구간 길이 (NM)" },
            {
                m_Setting.GetOptionDescLocaleID(nameof(Setting.HoldingLegNm)),
                "Holding 패턴 각 직선 구간의 길이입니다. 실제 DME/RNAV Holding처럼 거리로 지정합니다. 짧을수록 Holding이 작아집니다."
            },
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
