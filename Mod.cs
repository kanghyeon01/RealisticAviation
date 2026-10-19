using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;

namespace RealisticAviation
{
    public sealed class Mod : IMod
    {
        public const string kName = "RealisticAviation";

        public static readonly ILog Log = LogManager.GetLogger(kName).SetShowsErrorsInUI(false);

        public static Setting Setting { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info(nameof(OnLoad));
            // After a game patch, the log shows which game and mod versions ran together.
            Log.Info("Game " + Game.Version.current.fullVersion + ", Realistic Aviation " + typeof(Mod).Assembly.GetName().Version);

            // ModSetting registers each instance under the mod's id, so build the defaults first
            // and let the live instance be the one that stays registered.
            Setting defaults = new Setting(this);
            Setting = new Setting(this);
            Setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Setting));
            GameManager.instance.localizationManager.AddSource("ko-KR", new LocaleKO(Setting));
            AssetDatabase.global.LoadSettings(kName, Setting, defaults);

            // Both systems share AircraftNavigationSystem's 16-frame interval and offset, so they wrap
            // every navigation update: one right before it, one between it and AircraftMoveSystem.
            updateSystem.UpdateBefore<AtcPreNavigationSystem, AircraftNavigationSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAfter<AtcSystem, AircraftNavigationSystem>(SystemUpdatePhase.GameSimulation);
            // VFR chart infoview (phase 4-3): the button in the infoview menu, what it draws on the map, and the airport
            // buildings recoloured right after the game colours buildings for the infoview.
            AirspaceOverlaySystem.CreateInfoview(updateSystem.World.GetOrCreateSystemManaged<PrefabSystem>());
            updateSystem.UpdateAt<AirspaceOverlaySystem>(SystemUpdatePhase.Rendering);
            updateSystem.UpdateAfter<AirportHighlightSystem, ObjectColorSystem>(SystemUpdatePhase.Rendering);

            // Info panel sections aren't scheduled; the selected-info system updates the ones it knows about.
            updateSystem.World.GetOrCreateSystemManaged<AirportSection>();
            updateSystem.World.GetOrCreateSystemManaged<AirportClosedSection>();
            updateSystem.World.GetOrCreateSystemManaged<AirplaneAtcSection>();
        }

        public void OnDispose()
        {
            Log.Info(nameof(OnDispose));
            if (Setting != null)
            {
                Setting.UnregisterInOptionsUI();
                Setting = null;
            }
        }
    }
}
