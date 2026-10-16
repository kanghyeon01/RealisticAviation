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
const SECTION_TYPE = "RealisticAviation.LineUpSection";
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

// Airport info panel: the per-airport "line up and wait" switch.
function LineUpSection(props) {
    const { translate } = l10n.useLocalization();
    const checkbox = React.createElement(Checkbox, {
        checked: !!props.enabled,
        onChange: () => api.trigger(GROUP, "ToggleLineUpAndWait"),
    });
    const row = React.createElement(InfoRow, {
        left: translate("RealisticAviation.LineUp.Label", "Line up and wait"),
        right: checkbox,
        uppercase: true,
        tooltip: translate("RealisticAviation.LineUp.Tooltip", ""),
    });
    return React.createElement(InfoSection, { disableFocus: true }, row);
}

export default function register(moduleRegistry) {
    if (!InfoSection || !InfoRow || !Checkbox) {
        console.error("RealisticAviation: info panel components not found; the line up and wait checkbox is unavailable.");
        return;
    }
    moduleRegistry.extend(SECTIONS_MODULE, "selectedInfoSectionComponents", (components) => {
        components[SECTION_TYPE] = LineUpSection;
        return components;
    });
}

export const hasCSS = false;
