/*!
 * Cities: Skylines II UI Module
 *
 * Id: RealisticAviation
 * Author: Boing787(kanghyeon01)
 * Version: 0.1.0
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

function checkboxRow(key, label, checked, onChange, tooltip) {
    return React.createElement(InfoRow, {
        key: key,
        left: label,
        right: React.createElement(Checkbox, { checked: !!checked, onChange: onChange }),
        uppercase: true,
        tooltip: tooltip,
    });
}

// Airport info panel: line up and wait for the airport, and right-hand traffic pattern for each runway end.
function AirportSection(props) {
    const { translate } = l10n.useLocalization();
    const rows = [
        checkboxRow("lineUp", translate("RealisticAviation.LineUp.Label", "Line up and wait"), props.lineUp,
            () => api.trigger(GROUP, "ToggleLineUpAndWait"), translate("RealisticAviation.LineUp.Tooltip", "")),
    ];
    const runways = props.runways || [];
    const rightHand = translate("RealisticAviation.RightHand.Label", "right-hand pattern");
    const rightHandTooltip = translate("RealisticAviation.RightHand.Tooltip", "");
    for (let i = 0; i < runways.length; i++) {
        const index = i;
        rows.push(checkboxRow("rwy" + i, "RWY " + runways[i].name + " " + rightHand, runways[i].rightHand,
            () => api.trigger(GROUP, "ToggleRunwayRightHand", index), rightHandTooltip));
    }
    return React.createElement(InfoSection, { disableFocus: true }, ...rows);
}

export default function register(moduleRegistry) {
    if (!InfoSection || !InfoRow || !Checkbox) {
        console.error("RealisticAviation: info panel components not found; the airport options are unavailable.");
        return;
    }
    moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
        components[SECTION_TYPE] = AirportSection;
        return components;
    });
}

export const hasCSS = false;
