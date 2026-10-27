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
// Under the pointer: a white wash, or the newest exchange's blue a step brighter.
const HOVER_BACKGROUND = "rgba(255, 255, 255, 0.1)";
const NEWEST_HOVER_BACKGROUND = "rgba(130, 200, 255, 0.3)";
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

// One exchange. Clicking it selects its airplane and follows it (the game's own follow), or for a broadcast ("attention
// all aircraft") the airport that spoke, the same way; under the pointer it lights up (white, or a brighter blue for the newest one, which is
// already blue), so it reads as something to click.
function RadioExchange(props) {
    const [hover, setHover] = React.useState(false);
    const exchange = props.exchange;
    let style = exchange.own ? OWN_EXCHANGE_STYLE : EXCHANGE_STYLE;
    if (props.newest || hover) {
        style = Object.assign({}, style, { borderRadius: "3rem",
            backgroundColor: props.newest ? (hover ? NEWEST_HOVER_BACKGROUND : NEWEST_BACKGROUND) : HOVER_BACKGROUND });
    }
    style = Object.assign({}, style, { cursor: "pointer" });
    const onClick = () => exchange.airplane > 0
        ? api.trigger(GROUP, "SelectAirplane", exchange.airplane, exchange.airplaneVersion)
        : api.trigger(GROUP, "SelectAirport", exchange.airport, exchange.airportVersion);
    return React.createElement("div", { className: RADIO_ANIMATION_CLASS, style: style, onClick: onClick,
            onMouseEnter: () => setHover(true), onMouseLeave: () => setHover(false) },
        // "tower" and "pilot" are the first and second line; on a pilot's own call (pilotFirst) the pilot speaks first.
        React.createElement("div", { style: exchange.pilotFirst ? PILOT_LINE_STYLE : TOWER_LINE_STYLE }, exchange.tower),
        exchange.pilot ? React.createElement("div", { style: exchange.pilotFirst ? TOWER_LINE_STYLE : PILOT_LINE_STYLE }, exchange.pilot) : null);
}

// A frequency's recent exchanges, oldest first, under a "Tower radio  TWR 118.300" row. A broadcast to all aircraft has
// no readback (empty pilot line).
function radioRows(key, box, translate) {
    const exchanges = (box && box.exchanges) || [];
    const children = [];
    for (let i = 0; i < exchanges.length; i++) {
        // Keyed by the exchange itself: only a newly arrived one mounts, and so only it plays the slide-in animation.
        children.push(React.createElement(RadioExchange, { key: exchanges[i].id || i, exchange: exchanges[i], newest: i === exchanges.length - 1 }));
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
        props.pushback ? checkboxRow("longPushback", translate("RealisticAviation.LongPushback.Label", "Long pushback"), props.longPushback,
            () => api.trigger(GROUP, "ToggleLongPushback"), translate("RealisticAviation.LongPushback.Tooltip", "")) : null,
        checkboxRow("vor", translate("RealisticAviation.Vor.Label", "VOR"), props.vor,
            () => api.trigger(GROUP, "ToggleVor"), translate("RealisticAviation.Vor.Tooltip", "")),
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
    if (props.instrumentApproach && runways.length > 0) {
        // IFR chart: the approach chart of each runway end, opened in its own window.
        rows.push(React.createElement(InfoRow, {
            key: "approachCharts",
            left: translate("RealisticAviation.Charts.Label", "Approach charts"),
            right: React.createElement("div", { style: { display: "flex", flexWrap: "wrap", justifyContent: "flex-end" } },
                ...runways.map((runway, index) => React.createElement("div", {
                    key: "chart" + index,
                    style: Object.assign({}, STEPPER_BUTTON_STYLE, { width: "auto", padding: "0 6rem", margin: "2rem" }),
                    onClick: () => api.trigger(GROUP, "OpenApproachChart", index),
                }, "RNP RWY " + runway.name))),
            uppercase: true,
            tooltip: translate("RealisticAviation.Charts.Tooltip", ""),
        }));
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

// ---------------------------------------------------------------- approach chart window (IFR chart, step 3)
// The lower half of an AIP instrument approach chart (docs/ifr-symbols-preview.html, parts 9-11), worked out in C#
// (ApproachChartWindowSystem) and drawn here: header, missed approach text, transition altitude, MSA, the profile (SVG),
// the minima and rate of descent tables, and the coding tables in a second tab. Paper colours, as a printed chart.
// The UI engine has no table layout, so tables are rows of fixed-width cells.
const approachChart$ = api.bindValue(GROUP, "ApproachChart", null);
const INK = "#111";
const PAPER = "#fdfcf8";
const CHART_WINDOW_STYLE = {
    position: "absolute",
    top: "90rem",
    left: "320rem",
    width: "920rem",
    backgroundColor: PAPER,
    color: "#222",
    border: "1rem solid #888",
    borderRadius: "6rem",
    fontSize: "12rem",
    pointerEvents: "auto",
};
const CHART_HEAD_STYLE = { display: "flex", justifyContent: "space-between", alignItems: "flex-start", padding: "8rem 10rem", borderBottom: "2rem solid #e8813a" };
const CHART_TITLE_STYLE = { fontWeight: "bold", fontSize: "14rem", lineHeight: 1.3 };
const CHART_BOX_STYLE = { border: "1rem solid #333", padding: "2rem 6rem", fontSize: "11rem", lineHeight: 1.35, marginLeft: "6rem" };
const CHART_CLOSE_STYLE = { marginLeft: "10rem", padding: "0 6rem", border: "1rem solid #888", borderRadius: "3rem", cursor: "pointer", fontWeight: "bold" };
const CHART_TAB_STYLE = { border: "1rem solid #888", borderBottomWidth: "0", borderRadius: "4rem 4rem 0 0", padding: "2rem 10rem", marginRight: "4rem", cursor: "pointer", backgroundColor: "#eee" };
const CELL_STYLE = { border: "1rem solid #777", padding: "2rem 4rem", textAlign: "center", marginLeft: "-1rem", marginTop: "-1rem" };

const spaced = (n) => String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, " ");

// A row of table cells: [text, width in rem] pairs.
function tableRow(key, cells, bold) {
    return React.createElement("div", { key: key, style: { display: "flex" } },
        ...cells.map((cell, i) => React.createElement("div", {
            key: i,
            style: Object.assign({}, CELL_STYLE, { width: cell[1] + "rem", fontWeight: bold ? "bold" : "normal" }),
        }, cell[0])));
}

// SVG helpers: elements in the profile's own units (its viewBox).
const svgLine = (key, x1, y1, x2, y2, width, dash, color) => React.createElement("line", {
    key: key, x1: x1, y1: y1, x2: x2, y2: y2, stroke: color || INK, strokeWidth: width, strokeDasharray: dash || undefined,
});
const svgText = (key, x, y, text, size, anchor, extra) => React.createElement("text", Object.assign({
    key: key, x: x, y: y, fontSize: size, textAnchor: anchor || "middle", fill: INK,
}, extra || {}), text);
function svgHead(key, x, y, dx, dy, size) {
    const nx = -dy, ny = dx;
    const points = [x, y, x - dx * size + nx * size * 0.45, y - dy * size + ny * size * 0.45, x - dx * size - nx * size * 0.45, y - dy * size - ny * size * 0.45];
    return React.createElement("polygon", { key: key, points: points.map((v) => v.toFixed(1)).join(" "), fill: INK });
}
// Hatching at 45 degrees inside a rectangle, as clipped lines (no SVG patterns needed).
function svgHatch(key, x, y, w, h, out) {
    out.push(React.createElement("rect", { key: key + "r", x: x, y: y, width: Math.max(0, w), height: Math.max(0, h), fill: "none", stroke: "#444", strokeWidth: 0.8 }));
    for (let t = -h, i = 0; t < w; t += 6, i++) {
        const s0 = Math.max(0, -t), s1 = Math.min(h, w - t);
        if (s1 > s0) {
            out.push(svgLine(key + i, x + t + s0, y + h - s0, x + t + s1, y + h - s1, 0.8, null, "#444"));
        }
    }
}
// A line with its label in the line itself (the line breaks round it, the label turned along it), as the AIP profile.
function svgInline(key, x1, y1, x2, y2, label, width, dash, size, out) {
    if (!label) {
        out.push(svgLine(key, x1, y1, x2, y2, width, dash));
        return;
    }
    const l = Math.hypot(x2 - x1, y2 - y1), ux = (x2 - x1) / l, uy = (y2 - y1) / l, mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
    const gap = label.length * size * 0.33 + 4;
    let angle = Math.atan2(uy, ux) * 180 / Math.PI;
    if (angle > 90) angle -= 180; else if (angle < -90) angle += 180;
    out.push(svgLine(key + "a", x1, y1, mx - ux * gap, my - uy * gap, width, dash));
    out.push(svgLine(key + "b", mx + ux * gap, my + uy * gap, x2, y2, width, dash));
    out.push(svgText(key + "t", mx, my + size * 0.35, label, size, "middle",
        { fontWeight: "bold", fontStyle: "italic", transform: "rotate(" + angle.toFixed(1) + " " + mx + " " + my + ")" }));
}

// The profile, as the AIP's: IAF to threshold along the track, altitudes AMSL underlined with heights in brackets, the
// FAF cross, the gradient, hatched minimum altitude blocks (never above the path), the final down to the DA(H) with the
// arrowhead, dashed on to the runway, and the missed approach sketched beside the threshold.
function ChartProfile(props) {
    const c = props.chart, elev = c.elevation, out = [];
    const left = 60, rightX = 700, base = 205;
    const h = (alt) => alt - elev;
    const maxFt = Math.max(1000, Math.ceil(Math.max(h(c.iafAltitude), h(c.ifAltitude), h(c.initialMinimum)) * 1.2 / 500) * 500);
    const sxp = (n) => rightX - n / (c.iafNm + 0.3) * (rightX - left), syp = (f) => base - f / maxFt * 165;
    const blocks = [[c.iafNm, c.ifNm, c.initialMinimum, c.ifAltitude], [c.ifNm, c.fafNm, c.intermediateMinimum, c.fafAltitude]];
    blocks.forEach(([a, b, alt, endAlt], i) => {
        const top = Math.max(0, h(Math.min(alt, endAlt)));
        svgHatch("block" + i, sxp(a), syp(top), sxp(b) - sxp(a), base - syp(top), out);
        const mx = (sxp(a) + sxp(b)) / 2;
        out.push(React.createElement("rect", { key: "blockBox" + i, x: mx - 22, y: syp(top) + 6, width: 44, height: 14, fill: PAPER }));
        out.push(svgText("blockText" + i, mx, syp(top) + 17, spaced(alt), 11));
    });
    out.push(svgLine("ground", left, base, rightX + 150, base, 1, null, "#333"));
    // Final: its minimum, the DA(H), hatched from the FAF up to the runway.
    svgHatch("final", sxp(c.fafNm), syp(c.dh), sxp(0) - sxp(c.fafNm), base - syp(c.dh), out);
    const fm = (sxp(c.fafNm) + sxp(0)) / 2;
    out.push(React.createElement("rect", { key: "daBox", x: fm - 16, y: syp(c.dh) - 1, width: 32, height: 13, fill: PAPER, stroke: "#333", strokeWidth: 0.8 }));
    out.push(svgText("daText", fm, syp(c.dh) + 9, String(c.da), 10));

    // The path: IAF -> IF -> FAF solid, final solid to the minimum with the arrowhead, then dashed to the runway.
    const pts = [[c.iafNm, h(c.iafAltitude), c.iafName, "IAF"], [c.ifNm, h(c.ifAltitude), c.ifName, "IF"], [c.fafNm, h(c.fafAltitude), c.fafName, "FAF"]];
    const courses = c.pathCourses || [];
    for (let i = 0; i < 2; i++) {
        svgInline("path" + i, sxp(pts[i][0]), syp(pts[i][1]), sxp(pts[i + 1][0]), syp(pts[i + 1][1]), courses[i], 2.5, null, 12, out);
    }
    const daX = sxp(c.daNm), daY = syp(c.dh), fx = sxp(c.fafNm), fy = syp(h(c.fafAltitude));
    svgInline("final", fx, fy, daX, daY, courses[2], 2.5, null, 12, out);
    const fl = Math.hypot(daX - fx, daY - fy) || 1;
    out.push(svgHead("finalHead", daX, daY, (daX - fx) / fl, (daY - fy) / fl, 10));
    out.push(svgLine("visual", daX, daY, sxp(0), syp(c.rdh), 2, "5 4"));
    pts.forEach(([n, f, name, role], i) => {
        const x = sxp(n), y = syp(f);
        out.push(svgLine("fixLine" + i, x, y, x, base, 1, "3 3", "#555"));
        out.push(svgText("role" + i, x, y - 30, role, 11));
        out.push(svgText("name" + i, x, y - 17, name, 11, "middle", { fontWeight: "bold" }));
        // Altitude and height below the path, on the side of the fix line with room above the hatching.
        const blockLeft = i > 0 ? blocks[i - 1][2] : null, blockRight = i < blocks.length ? blocks[i][2] : null;
        const room = (alt) => (alt === null ? 999 : syp(Math.max(0, h(Math.min(alt, elev + f)))) - y);
        const leftSide = room(blockLeft) >= 32 || room(blockLeft) >= room(blockRight);
        const tx = leftSide ? x - 4 : x + 4, anchor = leftSide ? "end" : "start";
        const altitude = spaced(elev + f), width = altitude.length * 6.2;
        out.push(svgText("alt" + i, tx, y + 18, altitude, 11, anchor));
        out.push(svgLine("altLine" + i, leftSide ? tx - width : tx, y + 21, leftSide ? tx : tx + width, y + 21, 0.9));
        out.push(svgText("height" + i, tx, y + 31, "(" + spaced(f) + ")", 10, anchor));
    });
    // The FAF's Maltese cross on the path.
    out.push(React.createElement("path", {
        key: "cross", transform: "translate(" + fx + "," + fy + ")", fill: INK,
        d: "M0,0 L-5,-7 L5,-7 Z M0,0 L-5,7 L5,7 Z M0,0 L-7,-5 L-7,5 Z M0,0 L7,-5 L7,5 Z",
    }));
    const tan = Math.tan(c.glideDegrees * Math.PI / 180);
    out.push(svgText("gradient", (fx + sxp(0)) / 2 + 10, syp(h(c.fafAltitude) / 2) - 10, (tan * 100).toFixed(1) + "% (" + c.glideDegrees.toFixed(1) + "°)", 11));

    // MAPt at the threshold, then the missed approach sketch: level at the minimum past the threshold, climbing round the
    // pattern with rounded corners, each leg's course in the line, ending level with the arrowhead at the downwind fix.
    const tx = sxp(0), ty = syp(c.rdh);
    out.push(svgText("mapt", tx - 4, ty - 30, "MAPt", 10, "end"));
    out.push(svgText("thr", tx - 4, ty - 18, "RW" + c.runway + " THR", 10, "end"));
    const ma = [[daX, daY], [tx + 34, daY], [tx + 100, daY - 30], [tx + 136, daY - 92], [tx + 52, daY - 106]];
    const maNames = ["", "", ...(c.missedNames || [])], maCourses = ["", ...(c.missedCourses || [])];
    const corner = 20, dir = (a, b) => { const l = Math.hypot(b[0] - a[0], b[1] - a[1]); return [(b[0] - a[0]) / l, (b[1] - a[1]) / l]; };
    for (let i = 0; i < ma.length - 1; i++) {
        const [ux, uy] = dir(ma[i], ma[i + 1]);
        const cutA = i === 0 ? 0 : corner, cutB = i + 1 === ma.length - 1 ? 0 : corner;
        const a = [ma[i][0] + ux * cutA, ma[i][1] + uy * cutA], b = [ma[i + 1][0] - ux * cutB, ma[i + 1][1] - uy * cutB];
        svgInline("ma" + i, a[0], a[1], b[0], b[1], maCourses[i], 2, "5 4", 10, out);
        if (i + 1 < ma.length - 1) {
            const [vx, vy] = dir(ma[i + 1], ma[i + 2]);
            out.push(React.createElement("path", {
                key: "maTurn" + i, fill: "none", stroke: INK, strokeWidth: 2, strokeDasharray: "5 4",
                d: "M" + b[0] + "," + b[1] + " Q" + ma[i + 1][0] + "," + ma[i + 1][1] + " " + (ma[i + 1][0] + vx * corner) + "," + (ma[i + 1][1] + vy * corner),
            }));
        }
    }
    const last = ma[ma.length - 1], [ex, ey] = dir(ma[ma.length - 2], last);
    out.push(svgHead("maHead", last[0], last[1], ex, ey, 9));
    ma.forEach(([x, y], i) => {
        if (!maNames[i]) return;
        const [dx, dy, anchor] = i === 2 ? [6, 14, "start"] : i === 3 ? [8, 4, "start"] : [0, -8, "middle"];
        out.push(svgText("maName" + i, x + dx, y + dy, maNames[i], 10, anchor));
    });
    out.push(svgText("elev", tx + 48, base - 6, "ELEV " + elev, 9, "start"));
    out.push(React.createElement("rect", { key: "rdhBox", x: tx + 90, y: base - 15, width: 40, height: 12, fill: "none", stroke: "#333", strokeWidth: 0.8 }));
    out.push(svgText("rdh", tx + 110, base - 6, "RDH " + c.rdh, 9));
    out.push(React.createElement("rect", { key: "runway", x: tx, y: base - 3, width: 44, height: 6, fill: INK }));
    out.push(svgText("thrRunway", tx, base + 26, "THR RWY " + c.runway, 9));
    [c.iafNm, c.ifNm, c.fafNm, 0].forEach((n, i) => out.push(svgText("scale" + i, sxp(n), base + 14, n.toFixed(1), 10)));
    out.push(svgText("scaleTitle", left, base + 30, "NM FROM THR RWY " + c.runway, 10, "start"));
    return React.createElement("svg", { viewBox: "0 0 880 250", style: { width: "880rem", height: "250rem", display: "block", margin: "0 10rem" } }, ...out);
}

// MSA: four sectors round the airport, out to its airspace radius.
function ChartMsa(props) {
    const c = 60, r = 44, msa = props.chart.msa || [];
    const spots = [[c + 21, c - 15], [c + 21, c + 24], [c - 21, c + 24], [c - 21, c - 15]];
    return React.createElement("svg", { viewBox: "0 0 120 130", style: { width: "120rem", height: "130rem" } },
        React.createElement("circle", { key: "ring", cx: c, cy: c, r: r, fill: "none", stroke: INK, strokeWidth: 1.3 }),
        svgLine("we", c - r, c, c + r, c, 0.9), svgLine("ns", c, c - r, c, c + r, 0.9),
        ...spots.map(([x, y], i) => svgText("msa" + i, x, y, msa[i] || "-", 10)),
        svgText("msaTitle", c, c + r + 14, "MSA " + props.chart.msaRadius + " NM ARP", 9));
}

const CODING_COLUMNS = [["Serial Number", 52], ["Path Descriptor", 64], ["Waypoint Identifier", 92], ["Fly-over", 52], ["Course/Track °T", 76],
    ["Distance (NM)", 70], ["Turn direction", 64], ["Altitude (ft)", 80], ["Speed (kt)", 56], ["VPA/RDH", 72], ["Navigation specification", 112]];

function ApproachChartWindow() {
    const chart = api.useValue(approachChart$);
    const { translate } = l10n.useLocalization();
    const [tab, setTab] = React.useState(0);
    if (!chart || !chart.open) {
        return null;
    }
    const tabStyle = (i) => Object.assign({}, CHART_TAB_STYLE, i === tab ? { backgroundColor: PAPER, fontWeight: "bold" } : null);
    const head = React.createElement("div", { style: CHART_HEAD_STYLE },
        React.createElement("div", { style: CHART_TITLE_STYLE }, "INSTRUMENT APPROACH", React.createElement("br"), chart.title),
        React.createElement("div", { style: { display: "flex" } },
            React.createElement("div", { style: CHART_BOX_STYLE }, chart.tower, chart.approach ? React.createElement("br") : null, chart.approach || null),
            React.createElement("div", { style: CHART_BOX_STYLE }, "AERODROME ELEV " + chart.elevation + " ft", React.createElement("br"),
                "HEIGHTS RELATED TO THR RWY " + chart.runway + " - ELEV " + chart.elevation + " ft")),
        React.createElement("div", { style: { display: "flex", alignItems: "flex-start" } },
            React.createElement("div", { style: Object.assign({}, CHART_TITLE_STYLE, { textAlign: "right" }) }, chart.airport + " (" + chart.code + ")"),
            React.createElement("div", { style: CHART_CLOSE_STYLE, onClick: () => api.trigger(GROUP, "CloseApproachChart") }, "×")));
    const tabs = React.createElement("div", { style: { display: "flex", padding: "6rem 10rem 0" } },
        React.createElement("div", { style: tabStyle(0), onClick: () => setTab(0) }, translate("RealisticAviation.Charts.ChartTab", "Approach chart")),
        React.createElement("div", { style: tabStyle(1), onClick: () => setTab(1) }, translate("RealisticAviation.Charts.CodingTab", "Coding tables")));
    let body;
    if (tab === 0) {
        const kts = [60, 90, 120, 150, 180];
        body = React.createElement("div", { style: { borderTop: "1rem solid #888" } },
            React.createElement("div", { style: { display: "flex", justifyContent: "space-between", padding: "8rem 10rem 0" } },
                React.createElement("div", { style: { width: "560rem", lineHeight: 1.45 } },
                    React.createElement("div", { style: { fontWeight: "bold" } }, "MISSED APPROACH"), chart.missed),
                React.createElement("div", { style: { display: "flex", alignItems: "flex-start" } },
                    React.createElement("div", { style: CHART_BOX_STYLE }, "TRANSITION ALT " + chart.transitionAltitude, React.createElement("br"),
                        "TRANSITION LVL " + chart.transitionLevel),
                    React.createElement(ChartMsa, { chart: chart }))),
            React.createElement(ChartProfile, { chart: chart }),
            React.createElement("div", { style: { display: "flex", alignItems: "flex-start", padding: "6rem 10rem 10rem" } },
                React.createElement("div", { style: { marginRight: "16rem" } },
                    tableRow("minimaHead", [["CATEGORY", 150], ["DA(H)", 90], ["A", 36], ["B", 36], ["C", 36], ["D", 36]], true),
                    tableRow("minima", [["LNAV/VNAV FULL", 150], [chart.da + " (" + chart.dh + ")", 90], ["—", 144]])),
                React.createElement("div", null,
                    tableRow("knots", [["Knots", 70], ...kts.map((k) => [String(k), 46])], true),
                    tableRow("fpm", [["V/V fpm", 70], ...(chart.rateOfDescent || []).map((v) => [String(v), 46])]),
                    React.createElement("div", { style: { fontSize: "11rem", marginTop: "4rem" } }, "* Circling not authorized."))));
    } else {
        body = React.createElement("div", { style: { borderTop: "1rem solid #888", padding: "0 10rem 10rem" } },
            ...(chart.tables || []).map((table, t) => React.createElement("div", { key: "table" + t },
                React.createElement("div", { style: { padding: "8rem 0 4rem", fontWeight: "bold" } }, table.title),
                tableRow("head", CODING_COLUMNS.map(([name, width]) => [name, width]), true),
                ...(table.rows || []).map((row, r) => tableRow("row" + r, row.map((cell, i) => [cell, CODING_COLUMNS[i][1]]))))));
    }
    return React.createElement("div", { style: CHART_WINDOW_STYLE }, head, tabs, body);
}

// IFR chart legend: the infoview has a fixed number of chart slots (the game's own infomode checkboxes, named per city
// through the localization); the slots past the city's chart count are hidden.
const INFOMODE_ITEM_MODULE = "game-ui/game/components/infoviews/active-infoview-panel/components/infomode-item/infomode-item.tsx";
const IFR_CHART_PREFIX = "RealisticAviationIfrChart";
// Entries that follow another and stay out of the legend: the VFR chart's elevation tints in three terrain colour entries
// (the legend shows the first, which switches all three) and the IFR chart's background on the water (follows "Background").
const VFR_HIDDEN_TINTS = ["RealisticAviationTerrain2", "RealisticAviationTerrain3", "RealisticAviationIfrPaperWater"];
const ifrChartCount$ = api.bindValue(GROUP, "IfrChartCount", 0);

function extendInfomodeItem(Item) {
    return function RealisticAviationInfomodeItem(props) {
        const count = api.useValue(ifrChartCount$);
        const id = props.infomode && props.infomode.id;
        if (id && VFR_HIDDEN_TINTS.indexOf(id) >= 0) {
            return null;
        }
        if (id && id.startsWith(IFR_CHART_PREFIX) && parseInt(id.substring(IFR_CHART_PREFIX.length), 10) >= count) {
            return null;
        }
        return React.createElement(Item, props);
    };
}

export default function register(moduleRegistry) {
    injectRadioStyle();
    moduleRegistry.extend(INFOMODE_ITEM_MODULE, "InfomodeItem", extendInfomodeItem);
    moduleRegistry.append("Game", ApproachChartWindow);
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
