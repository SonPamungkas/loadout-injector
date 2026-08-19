using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
namespace LoadoutInjector
{
    [BepInPlugin(PluginGuid, "Loadout Injector", SchemaVersion)]
    public class LoadoutInjectorPlugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "neutral.loadoutinjector";
        internal const string SchemaVersion = "2.0.0";
        internal const string PresetSchemaStamp = "2.0.0-k2";
        public static LoadoutInjectorPlugin Instance;
        public static ManualLogSource ModLogger;
        public static ConfigEntry<bool> Cfg_Integration_LoadoutPresets_Enable;
        public static ConfigEntry<bool> Cfg_DebugLogging;
        public static ConfigEntry<bool> Cfg_WriteHardpointDictionary;
        public static ConfigEntry<bool> Cfg_StrictStationWhitelist;
        public static ConfigEntry<bool> Cfg_AIAlwaysRandomiseLoadout;
        private void Awake()
        {
            Instance = this;
            ModLogger = Logger;
            BindConfigs();
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();
            ModLogger.LogInfo("Loadout Injector " + SchemaVersion + " loaded.");
        }
        private void OnGUI()
        {
            if (Cfg_Integration_LoadoutPresets_Enable?.Value == true)
                LoadoutPresets.PresetMenuUI.Draw();
        }
        private void BindConfigs()
        {
            const string S_INTEGRATIONS = "Integrations";
            const string S_DEBUG = "Debug";
            Cfg_Integration_LoadoutPresets_Enable = Config.Bind(S_INTEGRATIONS, "Loadout Presets UI", true,
                "Enables the per-aircraft saved presets UI in the hangar.");
            Cfg_DebugLogging = Config.Bind(S_DEBUG, "Verbose Logging", false,
                "Enables extreme logging for troubleshooting. Keep OFF during normal gameplay for maximum performance.");
            Cfg_StrictStationWhitelist = Config.Bind("Enforcement", "Strict Station Whitelist", true,
                "The station json fully defines that weapon station: mounts listed are added, mounts "
                + "absent are removed and cannot be loaded by any route. Turn off to only ever add.");
            Cfg_AIAlwaysRandomiseLoadout = Config.Bind("Enforcement", "AI Always Randomises Loadout", false,
                "Makes every AI aircraft build its loadout from the station jsons instead of the "
                + "aircraft's authored standard loadouts, so AI actively flies injected weapons. "
                + "Discards those authored loadouts and their fuel ratio (fuel reverts to default).");
            Cfg_WriteHardpointDictionary = Config.Bind(S_DEBUG, "Write Hardpoint Dictionary", true,
                "Writes preset-loadout/hardpointdictionary.log listing every weapon mount and hardpoint set, "
                + "with real ammo counts and the mod that contributed them. Turn off to skip the disk write.");
        }
    }
}