using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
namespace LoadoutInjector
{
    internal static class JsonLoadoutInjector
    {
        [Serializable]
        public class WeaponStationOptions
        {
            public List<string> allowedWeapons = new List<string>();
        }
        internal static string RootDir => Path.Combine(Paths.PluginPath, "preset-loadout");
        public static readonly Dictionary<string, Dictionary<int, List<WeaponMount>>> CachedJsonWeapons = 
            new Dictionary<string, Dictionary<int, List<WeaponMount>>>();
        public static bool IsCached = false;
        internal static string GetAircraftName(Aircraft aircraft)
        {
            if (aircraft == null) return "unknown";
            var def = aircraft.definition;
            if (def != null)
            {
                if (!string.IsNullOrEmpty(def.jsonKey))
                    return SanitizeFileName(def.jsonKey);
                if (!string.IsNullOrEmpty(def.unitName))
                    return SanitizeFileName(def.unitName);
            }
            string rawName = aircraft.gameObject.name;
            if (rawName.EndsWith("(Clone)", StringComparison.Ordinal)) 
                rawName = rawName.Substring(0, rawName.Length - 7);
            string nameToSanitize = string.IsNullOrEmpty(aircraft.unitName) ? rawName : aircraft.unitName;
            return SanitizeFileName(nameToSanitize);
        }
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            bool needsReplacement = false;
            for (int i = 0; i < name.Length; i++)
            {
                for (int j = 0; j < invalidChars.Length; j++)
                {
                    if (name[i] == invalidChars[j])
                    {
                        needsReplacement = true;
                        break;
                    }
                }
                if (needsReplacement) break;
            }
            if (!needsReplacement) return name;
            foreach (char c in invalidChars)
                name = name.Replace(c, '_');
            return name;
        }
        internal static List<WeaponMount> GetAvailableWeaponMounts()
        {
            var mounts = new List<WeaponMount>();
            var seen = new HashSet<int>();
            if (Encyclopedia.i != null && Encyclopedia.i.weaponMounts != null)
            {
                foreach (var m in Encyclopedia.i.weaponMounts)
                {
                    if (m != null && seen.Add(m.GetInstanceID()))
                        mounts.Add(m);
                }
            }
            if (Encyclopedia.WeaponLookup != null)
            {
                foreach (var m in Encyclopedia.WeaponLookup.Values)
                {
                    if (m != null && seen.Add(m.GetInstanceID()))
                        mounts.Add(m);
                }
            }
            foreach (var m in Resources.FindObjectsOfTypeAll<WeaponMount>())
            {
                if (m != null && seen.Add(m.GetInstanceID()))
                    mounts.Add(m);
            }
            return mounts;
        }
        private static int _lastDictionaryMountCount = -1;
        public static void ClearCache()
        {
            _allMountsByName = null;
            CachedJsonWeapons.Clear();
            _lastDictionaryMountCount = -1;
            InjectionPipeline.ClearCache();
        }
        public static void GenerateDictionary(bool force = false)
        {
            var mounts = GetAvailableWeaponMounts();
            if (mounts.Count == 0) return;
            if (!force && mounts.Count == _lastDictionaryMountCount) return;
            _lastDictionaryMountCount = mounts.Count;
            if (Encyclopedia.i != null && Encyclopedia.i.weaponMounts != null && Encyclopedia.WeaponLookup != null)
            {
                foreach (var m in Encyclopedia.i.weaponMounts)
                {
                    if (m != null && !Encyclopedia.WeaponLookup.ContainsKey(m.name))
                    {
                        Encyclopedia.WeaponLookup[m.name] = m;
                        if (!string.IsNullOrEmpty(m.jsonKey) && !Encyclopedia.WeaponLookup.ContainsKey(m.jsonKey))
                            Encyclopedia.WeaponLookup[m.jsonKey] = m;
                    }
                }
            }
            if (Encyclopedia.WeaponLookup != null)
            {
                foreach (var m in mounts)
                {
                    if (m != null)
                    {
                        if (!Encyclopedia.WeaponLookup.ContainsKey(m.name))
                            Encyclopedia.WeaponLookup[m.name] = m;
                        if (!string.IsNullOrEmpty(m.jsonKey) && !Encyclopedia.WeaponLookup.ContainsKey(m.jsonKey))
                            Encyclopedia.WeaponLookup[m.jsonKey] = m;
                    }
                }
            }
            string dictPath = Path.Combine(RootDir, "hardpointdictionary.log");
            Directory.CreateDirectory(RootDir);
            var lines = new List<string>
            {
                "=== Weapon Mount Dictionary ===",
                "Use these exact names in your json files:",
                ""
            };
            foreach (var m in mounts.OrderBy(m => m.name))
            {
                string key = !string.IsNullOrEmpty(m.jsonKey) ? m.jsonKey : m.name;
                lines.Add(key);
            }
            lines = lines.Distinct().ToList();
            File.WriteAllLines(dictPath, lines);
            if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Generated dictionary at {dictPath} with {mounts.Count} mounts.");
        }
        private static Dictionary<string, WeaponMount> _allMountsByName = null;
        private static WeaponMount ResolveMount(string name)
        {
            if (Encyclopedia.WeaponLookup != null && Encyclopedia.WeaponLookup.TryGetValue(name, out var encyMount) && encyMount != null)
                return encyMount;
            if (_allMountsByName == null || _allMountsByName.Count == 0)
            {
                _allMountsByName = new Dictionary<string, WeaponMount>();
                foreach (var m in GetAvailableWeaponMounts())
                {
                    if (m != null)
                    {
                        _allMountsByName[m.name] = m;
                        if (!string.IsNullOrEmpty(m.jsonKey))
                            _allMountsByName[m.jsonKey] = m;
                    }
                }
            }
            _allMountsByName.TryGetValue(name, out var found);
            return found;
        }
        public static Dictionary<int, List<WeaponMount>> GetLoadout(string acName, Aircraft aircraft)
        {
            GenerateDictionary();
            if (CachedJsonWeapons.TryGetValue(acName, out var stationDict))
                return stationDict;
            stationDict = new Dictionary<int, List<WeaponMount>>();
            CachedJsonWeapons[acName] = stationDict; 
            string acDir = Path.Combine(RootDir, acName);
            Directory.CreateDirectory(acDir);
            var wm = aircraft.GetComponentInChildren<WeaponManager>(true);
            if (wm == null || wm.hardpointSets == null) return stationDict;
            for (int i = 0; i < wm.hardpointSets.Length; i++)
            {
                var hs = wm.hardpointSets[i];
                if (hs == null) continue;
                string stationDir = Path.Combine(acDir, "weaponstation" + i);
                Directory.CreateDirectory(stationDir);
                string[] existingJsons = Directory.GetFiles(stationDir, "*.json");
                if (existingJsons.Length == 0)
                {
                    string hsName = !string.IsNullOrEmpty(hs.name) ? SanitizeFileName(hs.name) : ("weaponstation" + i);
                    string jsonPath = Path.Combine(stationDir, hsName + ".json");
                    var defaultOptions = new WeaponStationOptions();
                    if (hs.weaponOptions != null)
                    {
                        foreach (var mount in hs.weaponOptions)
                        {
                            if (mount != null)
                            {
                                string wKey = !string.IsNullOrEmpty(mount.jsonKey) ? mount.jsonKey : mount.name;
                                if (!defaultOptions.allowedWeapons.Contains(wKey))
                                    defaultOptions.allowedWeapons.Add(wKey);
                            }
                        }
                    }
                    try
                    {
                        File.WriteAllText(jsonPath, JsonUtility.ToJson(defaultOptions, true));
                        if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                            LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Auto-generated JSON for {acName} station {i} ({hsName}): {jsonPath}");
                    }
                    catch (Exception ex)
                    {
                        LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Error generating JSON at {jsonPath}: {ex.Message}");
                    }
                }
                var newOptions = new List<WeaponMount>();
                foreach (string jsonPath in Directory.GetFiles(stationDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(jsonPath);
                        var options = JsonUtility.FromJson<WeaponStationOptions>(json);
                        if (options != null && options.allowedWeapons != null)
                        {
                            foreach (string wName in options.allowedWeapons)
                            {
                                var mount = ResolveMount(wName);
                                if (mount != null && !newOptions.Contains(mount))
                                    newOptions.Add(mount);
                                else if (mount == null && LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                                    LoadoutInjectorPlugin.ModLogger.LogWarning($"[JsonInjector] Could not resolve mount '{wName}' in {jsonPath}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Failed to parse JSON at {jsonPath}: {ex.Message}");
                    }
                }
                if (newOptions.Count > 0)
                    stationDict[i] = newOptions;
            }
            if (stationDict.Count > 0 && LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
            {
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Lazy-cached {stationDict.Count} stations for aircraft {acName}");
            }
            return stationDict;
        }
        internal static void Inject(WeaponManager wm)
        {
            if (wm == null || wm.hardpointSets == null) return;
            var aircraft = wm.GetComponentInParent<Aircraft>();
            if (aircraft == null) return;
            string acName = GetAircraftName(aircraft);
            var dict = GetLoadout(acName, aircraft);
            if (dict != null && dict.Count > 0)
            {
                for (int i = 0; i < wm.hardpointSets.Length; i++)
                {
                    var hs = wm.hardpointSets[i];
                    if (hs == null) continue;
                    if (dict.TryGetValue(i, out var customMounts))
                    {
                        var expanded = new List<WeaponMount>(hs.weaponOptions ?? new List<WeaponMount>());
                        var seen = new HashSet<int>(expanded.Where(x => x != null).Select(x => x.GetInstanceID()));
                        foreach(var mount in customMounts)
                        {
                            if (mount != null && seen.Add(mount.GetInstanceID()))
                                expanded.Add(mount);
                        }
                        hs.weaponOptions = expanded;
                    }
                }
            }
        }
    }
    [HarmonyPatch(typeof(GameManager), "SetupGame")]
    internal static class Patch_GameManager_SetupGame_Dictionary
    {
        static void Postfix()
        {
            JsonLoadoutInjector.GenerateDictionary();
        }
    }
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { typeof(Encyclopedia) })]
    internal static class Patch_Encyclopedia_AfterLoad_Dictionary1
    {
        static void Postfix()
        {
            JsonLoadoutInjector.ClearCache();
            JsonLoadoutInjector.GenerateDictionary();
        }
    }
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    internal static class Patch_Encyclopedia_AfterLoad_Dictionary2
    {
        static void Postfix()
        {
            JsonLoadoutInjector.ClearCache();
            JsonLoadoutInjector.GenerateDictionary();
        }
    }
    [HarmonyPatch(typeof(AircraftSelectionMenu), "OnEnable")]
    internal static class Patch_AircraftSelectionMenu_OnEnable_Dictionary
    {
        static void Postfix()
        {
            JsonLoadoutInjector.GenerateDictionary();
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "Awake")]
    internal static class Patch_WeaponManager_Awake_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            JsonLoadoutInjector.GenerateDictionary();
            try
            {
                JsonLoadoutInjector.Inject(__instance);
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager Awake patch failed: " + ex);
            }
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "InitializeWeaponManager")]
    internal static class Patch_WeaponManager_Initialize_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            JsonLoadoutInjector.GenerateDictionary();
            try
            {
                JsonLoadoutInjector.Inject(__instance);
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager InitializeWeaponManager patch failed: " + ex);
            }
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "SpawnWeapons")]
    internal static class Patch_WeaponManager_SpawnWeapons_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            try
            {
                JsonLoadoutInjector.Inject(__instance);
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager SpawnWeapons patch failed: " + ex);
            }
        }
    }
}