/*!
 * Cities: Skylines II UI Module
 *
 * Id: RealisticAviation
 * Author: Boing787(kanghyeon01)
 * Version: 1.2.4
 * Dependencies:
 */
// Hand-written (no bundler): uses only the globals the game exposes to UI mods.
const React = window.React;
const api = window["cs2/api"];
const modding = window["cs2/modding"];
const l10n = window["cs2/l10n"];

const GROUP = "RealisticAviation";
const SECTION_TYPE = "RealisticAviation.AirportSection";
const SECTIONS_MODULE = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const CLOSED_SECTION_TYPE = "RealisticAviation.AirportClosedSection";

// Closure banner: red, in the top area of the panel next to the notifications.
const BANNER_STYLE = {
    backgroundColor: "rgba(200, 40, 30, 0.85)",
    borderRadius: "4rem",
    padding: "10rem 12rem",
    textAlign: "center",
    color: "white",
};
// Ground delay banner (phase 4-5): amber instead of red, and below the closure banner when both apply.
const DELAY_BANNER_STYLE = Object.assign({}, BANNER_STYLE, { backgroundColor: "rgba(215, 140, 20, 0.85)", marginTop: "6rem" });
const BANNER_TITLE_STYLE = { fontSize: "18rem", fontWeight: "bold", textTransform: "uppercase" };
const BANNER_REASON_STYLE = { fontSize: "14rem", marginTop: "4rem" };

const ATC_SECTION_TYPE = "RealisticAviation.AirplaneAtcSection";

// Radio box (phase 4-2): a dark panel with the tower's words in blue and the pilot's readback in white; the selected
// airplane's own exchanges are marked with a yellow bar.
const RADIO_BOX_STYLE = {
    backgroundColor: "rgba(0, 0, 0, 0.35)",
    border: "1rem solid rgba(255, 255, 255, 0.2)",
    borderRadius: "4rem",
    padding: "8rem 10rem",
    fontSize: "13rem",
    lineHeight: 1.35,
};
const TOWER_LINE_STYLE = { color: "rgb(130, 200, 255)" };
const PILOT_LINE_STYLE = { color: "white" };
const EXCHANGE_STYLE = { marginBottom: "6rem", paddingLeft: "6rem", borderLeft: "3rem solid transparent" };
const OWN_EXCHANGE_STYLE = { marginBottom: "6rem", paddingLeft: "6rem", borderLeft: "3rem solid rgb(255, 200, 60)" };
const EMPTY_STYLE = { color: "rgba(255, 255, 255, 0.5)" };

// The newest exchange (at the bottom) is lit up, and each new one slides up into place, so the eye goes to the bottom.
const NEWEST_BACKGROUND = "rgba(130, 200, 255, 0.16)";
const RADIO_ANIMATION_CLASS = "realistic-aviation-radio-in";
const RADIO_STYLE_ID = "realistic-aviation-radio-style";
const RADIO_CSS = "@keyframes realistic-aviation-radio-in { from { opacity: 0; transform: translateY(10rem); } to { opacity: 1; transform: translateY(0); } }"
    + " ." + RADIO_ANIMATION_CLASS + " { animation: realistic-aviation-radio-in 0.45s ease-out; }";

function injectRadioStyle() {
    if (typeof document === "undefined" || document.getElementById(RADIO_STYLE_ID)) {
        return;
    }
    const style = document.createElement("style");
    style.id = RADIO_STYLE_ID;
    style.textContent = RADIO_CSS;
    document.head.appendChild(style);
}

// A frequency's recent exchanges, oldest first, under a "Tower radio  TWR 118.300" row. A broadcast to all aircraft has
// no readback (empty pilot line).
function radioRows(key, box, translate) {
    const exchanges = (box && box.exchanges) || [];
    const children = [];
    for (let i = 0; i < exchanges.length; i++) {
        const exchange = exchanges[i];
        let style = exchange.own ? OWN_EXCHANGE_STYLE : EXCHANGE_STYLE;
        if (i === exchanges.length - 1) {
            style = Object.assign({}, style, { backgroundColor: NEWEST_BACKGROUND, borderRadius: "3rem" });
        }
        // Keyed by the exchange itself: only a newly arrived one mounts, and so only it plays the slide-in animation.
        children.push(React.createElement("div", { key: exchange.id || i, className: RADIO_ANIMATION_CLASS, style: style },
            // "tower" and "pilot" are the first and second line; on a pilot's own call (pilotFirst) the pilot speaks first.
            React.createElement("div", { style: exchange.pilotFirst ? PILOT_LINE_STYLE : TOWER_LINE_STYLE }, exchange.tower),
            exchange.pilot ? React.createElement("div", { style: exchange.pilotFirst ? TOWER_LINE_STYLE : PILOT_LINE_STYLE }, exchange.pilot) : null));
    }
    if (children.length === 0) {
        children.push(React.createElement("div", { key: "empty", style: EMPTY_STYLE }, translate("RealisticAviation.Radio.Empty", "No recent transmissions")));
    }
    return [
        React.createElement(InfoRow, { key: key + "Title", left: translate("RealisticAviation.Radio.Label", "Tower radio"), right: (box && box.label) || "", uppercase: true }),
        React.createElement("div", { key: key, style: RADIO_BOX_STYLE }, ...children),
    ];
}

function getGameModule(path, name) {
    try {
        return modding.getModule(path, name);
    } catch (e) {
        return undefined;
    }
}

const InfoSection = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx", "InfoSection");
const InfoRow = getGameModule("game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx", "InfoRow");
const Checkbox = getGameModule("game-ui/common/input/toggle/checkbox/checkbox.tsx", "Checkbox");
const TextInput = getGameModule("game-ui/common/input/text/text-input.tsx", "TextInput");

const CODE_INPUT_STYLE = {
    width: "70rem",
    padding: "2rem 6rem",
    borderRadius: "3rem",
    backgroundColor: "rgba(255, 255, 255, 0.15)",
    color: "white",
    textAlign: "center",
    fontSize: "14rem",
};

// The airport code: the entered one, or the automatic one shown greyed as the placeholder. Sent when the field loses focus
// (Enter and Esc do that too); an empty field brings back the automatic code.
function CodeField(props) {
    const [draft, setDraft] = React.useState(null);
    return React.createElement(TextInput, {
        value: draft !== null ? draft : props.code,
        placeholder: props.automaticCode,
        maxLength: 4,
        style: CODE_INPUT_STYLE,
        onFocus: () => setDraft(props.code),
        onChange: (e) => setDraft(String(e.target.value).toUpperCase().replace(/[^A-Z0-9]/g, "").slice(0, 4)),
        onBlur: () => {
            if (draft !== null && draft !== props.code) {
                api.trigger(GROUP, "SetAirportCode", draft);
            }
            setDraft(null);
        },
    });
}

// "[-] value [+]" for a small number setting; dimmed (still clickable) while the setting is off.
const STEPPER_BUTTON_STYLE = {
    display: "flex",
    justifyContent: "center",
    width: "22rem",
    textAlign: "center",
    margin: "0 6rem",
    borderRadius: "3rem",
    backgroundColor: "rgba(255, 255, 255, 0.15)",
    cursor: "pointer",
};

const AIRSPACE_CLASS_VALUES = { D: 0, C: 1, B: 2 };

// A row of small buttons, one per option; the selected one is lit.
function segmented(options, selected, onSelect) {
    return React.createElement("div", { style: { display: "flex", alignItems: "center" } },
        ...options.map((option) => React.createElement("div", {
            key: option.value,
            style: Object.assign({}, STEPPER_BUTTON_STYLE, { width: "auto", padding: "0 6rem", margin: "0 2rem" },
                option.value === selected ? { backgroundColor: "rgba(130, 200, 255, 0.6)" } : null),
            onClick: () => onSelect(option.value),
        }, option.label)));
}

function stepper(value, onDown, onUp, dimmed) {
    return React.createElement("div", { style: { display: "flex", alignItems: "center", opacity: dimmed ? 0.5 : 1 } },
        React.createElement("div", { style: STEPPER_BUTTON_STYLE, onClick: onDown }, "−"),
        React.createElement("div", null, value),
        React.createElement("div", { style: STEPPER_BUTTON_STYLE, onClick: onUp }, "+"));
}

function checkboxRow(key, label, checked, onChange, tooltip) {
    return React.createElement(InfoRow, {
        key: key,
        left: label,
        right: React.createElement(Checkbox, { checked: !!checked, onChange: onChange }),
        uppercase: true,
        tooltip: tooltip,
    });
}

// Airport info panel: line up and wait for the airport, right-hand traffic pattern for each runway end, and the radio log.
function AirportSection(props) {
    const { translate } = l10n.useLocalization();
    const rows = [
        props.airspaceClass ? React.createElement(InfoRow, {
            key: "airspace",
            left: translate("RealisticAviation.Airspace.Label", "Airspace"),
            right: translate("RealisticAviation.Airspace.Value", "Class {CLASS}, {RADIUS} NM").replace("{CLASS}", props.airspaceClass).replace("{RADIUS}", Number(props.airspaceRadius).toFixed(1)),
            uppercase: true,
            tooltip: translate("RealisticAviation.Airspace.Tooltip", ""),
        }) : null,
        props.airspaceClass ? React.createElement(InfoRow, {
            key: "airspaceClass",
            left: translate("RealisticAviation.Airspace.Choose", "Airspace class"),
            right: segmented([
                { label: translate("RealisticAviation.Airspace.Auto", "Auto"), value: -1 },
                { label: "B", value: 2 }, { label: "C", value: 1 }, { label: "D", value: 0 },
            ], props.airspaceChosen ? AIRSPACE_CLASS_VALUES[props.airspaceClass] : -1, (value) => api.trigger(GROUP, "SetAirspaceClass", value)),
            uppercase: true,
        }) : null,
        TextInput ? React.createElement(InfoRow, {
            key: "code",
            left: translate("RealisticAviation.Code.Label", "ICAO code"),
            right: React.createElement(CodeField, { code: props.code, automaticCode: props.automaticCode }),
            uppercase: true,
            tooltip: translate("RealisticAviation.Code.Tooltip", ""),
        }) : null,
        checkboxRow("lineUp", translate("RealisticAviation.LineUp.Label", "Line up and wait"), props.lineUp,
            () => api.trigger(GROUP, "ToggleLineUpAndWait"), translate("RealisticAviation.LineUp.Tooltip", "")),
        checkboxRow("groundDelay", translate("RealisticAviation.GroundDelay.Toggle", "Ground delay"), props.groundDelay,
            () => api.trigger(GROUP, "ToggleGroundDelay"), translate("RealisticAviation.GroundDelay.Tooltip", "")),
        React.createElement(InfoRow, {
            key: "groundDelayLimit",
            left: translate("RealisticAviation.GroundDelay.Limit", "Ground delay limit"),
            right: stepper(translate("RealisticAviation.GroundDelay.LimitValue", "{COUNT} airplanes").replace("{COUNT}", String(props.groundDelayLimit)),
                () => api.trigger(GROUP, "AdjustGroundDelayLimit", -1), () => api.trigger(GROUP, "AdjustGroundDelayLimit", 1), !props.groundDelay),
            uppercase: true,
        }),
    ];
    const runways = props.runways || [];
    const rightHand = translate("RealisticAviation.RightHand.Label", "right-hand pattern");
    const rightHandTooltip = translate("RealisticAviation.RightHand.Tooltip", "");
    for (let i = 0; i < runways.length; i++) {
        const index = i;
        rows.push(checkboxRow("rwy" + i, "RWY " + runways[i].name + " " + rightHand, runways[i].rightHand,
            () => api.trigger(GROUP, "ToggleRunwayRightHand", index), rightHandTooltip));
    }
    if (props.hasApproach) {
        rows.push(...radioRows("approachRadio", props.approachRadio, translate));
    }
    if (props.hasDeparture) {
        rows.push(...radioRows("departureRadio", props.departureRadio, translate));
    }
    rows.push(...radioRows("towerRadio", props.towerRadio, translate));
    rows.push(...radioRows("groundRadio", props.groundRadio, translate));
    return React.createElement(InfoSection, { disableFocus: true }, ...rows);
}

const REMOVE_BUTTON_STYLE = {
    marginTop: "6rem",
    padding: "4rem 8rem",
    borderRadius: "3rem",
    textAlign: "center",
    backgroundColor: "rgba(255, 255, 255, 0.15)",
    cursor: "pointer",
};
// "Remove this airplane": one click, as the option that shows it is off by default and turned on on purpose.
function RemoveButton() {
    const { translate } = l10n.useLocalization();
    return React.createElement("div", { style: REMOVE_BUTTON_STYLE, onClick: () => api.trigger(GROUP, "RemoveAirplane") },
        translate("RealisticAviation.RemoveAirplane", "Remove this airplane"));
}

// Airplane info panel: the recent exchanges on the frequency it is on, its own marked; and the remove button (option).
function AirplaneAtcSection(props) {
    const { translate } = l10n.useLocalization();
    const children = props.showRadio ? radioRows("radio", props.radio, translate) : [];
    if (props.canRemove) {
        children.push(React.createElement(RemoveButton, { key: "remove" }));
    }
    return React.createElement(InfoSection, { disableFocus: true }, ...children);
}

// "Airport closed" banner, with the event that closed it in the game's own words (e.g. "Hail Storm").
function AirportClosedSection(props) {
    const { translate } = l10n.useLocalization();
    const banners = [];
    if (props.closed) {
        const reason = props.closedBy ? translate("Assets.NAME[" + props.closedBy + "]", props.closedBy) : "";
        banners.push(React.createElement("div", { key: "closed", style: BANNER_STYLE },
            React.createElement("div", { style: BANNER_TITLE_STYLE }, translate("RealisticAviation.Closed.Label", "Airport closed")),
            reason ? React.createElement("div", { style: BANNER_REASON_STYLE }, reason) : null));
    }
    if (props.groundDelay) {
        const count = translate("RealisticAviation.GroundDelay.Count", "{COUNT} airplanes moving on the ground").replace("{COUNT}", String(props.groundCount));
        banners.push(React.createElement("div", { key: "delay", style: DELAY_BANNER_STYLE },
            React.createElement("div", { style: BANNER_TITLE_STYLE }, translate("RealisticAviation.GroundDelay.Label", "Ground delay")),
            React.createElement("div", { style: BANNER_REASON_STYLE }, count)));
    }
    return React.createElement(InfoSection, { disableFocus: true }, ...banners);
}

export default function register(moduleRegistry) {
    injectRadioStyle();
    if (!InfoSection || !InfoRow || !Checkbox) {
        console.error("RealisticAviation: info panel components not found; the airport options are unavailable.");
        return;
    }
    moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
        components[SECTION_TYPE] = AirportSection;
        components[CLOSED_SECTION_TYPE] = AirportClosedSection;
        components[ATC_SECTION_TYPE] = AirplaneAtcSection;
        return components;
    });
}

export const hasCSS = false;
