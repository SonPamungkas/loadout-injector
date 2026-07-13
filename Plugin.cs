using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace LoadoutInjector
{
    [BepInPlugin("neutral.loadoutinjector", "Loadout Injector", "1.0.0")]
    public class LoadoutInjectorPlugin : BaseUnityPlugin
    {
        public static LoadoutInjectorPlugin Instance;
        public static ManualLogSource ModLogger;

        public static ConfigEntry<bool> Cfg_Integration_LoadoutPresets_Enable;

        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;

            BindConfigs();

            var harmony = new Harmony("neutral.loadoutinjector");
            harmony.PatchAll();

            ModLogger.LogInfo("Loadout Injector (Universal JSON) loaded.");
        }

        private void OnGUI()
        {
            if (Cfg_Integration_LoadoutPresets_Enable?.Value == true)
                LoadoutPresets.PresetMenuUI.Draw();
        }

        private void BindConfigs()
        {
            const string S_INTEGRATIONS = "Integrations";

            Cfg_Integration_LoadoutPresets_Enable = Config.Bind(S_INTEGRATIONS, "Loadout Presets UI", true,
                "Enables the per-aircraft saved presets UI in the hangar.");
        }
    }
}
