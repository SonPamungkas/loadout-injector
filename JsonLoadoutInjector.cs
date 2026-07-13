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

        private static bool dictionaryGenerated = false;
        internal static string RootDir => Path.Combine(Paths.PluginPath, "loadout-preset");

        public static readonly Dictionary<string, Dictionary<int, List<WeaponMount>>> CachedJsonWeapons = 
            new Dictionary<string, Dictionary<int, List<WeaponMount>>>();

        public static bool IsCached = false;

        internal static string GetAircraftName(Aircraft aircraft)
        {
            if (aircraft == null) return "unknown";
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

        internal static void GenerateDictionary()
        {
            if (dictionaryGenerated) return;
            var mounts = Resources.FindObjectsOfTypeAll<WeaponMount>().Where(m => m != null).ToList();
            if (mounts.Count == 0) return;
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
            dictionaryGenerated = true;

            if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Generated dictionary at {dictPath} with {mounts.Count} mounts.");
        }

        private static Dictionary<string, WeaponMount> _allMountsByName = null;

        private static WeaponMount ResolveMount(string name)
        {
            if (_allMountsByName == null)
            {
                _allMountsByName = new Dictionary<string, WeaponMount>();
                foreach (var m in Resources.FindObjectsOfTypeAll<WeaponMount>())
                {
                    if (m != null)
                    {
                        _allMountsByName[m.name] = m;
                        if (!string.IsNullOrEmpty(m.jsonKey))
                            _allMountsByName[m.jsonKey] = m;
                    }
                }
            }
            if (Encyclopedia.WeaponLookup != null && Encyclopedia.WeaponLookup.TryGetValue(name, out var encyMount))
                return encyMount;

            _allMountsByName.TryGetValue(name, out var found);
            return found;
        }

        public static Dictionary<int, List<WeaponMount>> GetLoadout(string acName, Aircraft aircraft)
        {
            if (CachedJsonWeapons.TryGetValue(acName, out var stationDict))
                return stationDict;

            stationDict = new Dictionary<int, List<WeaponMount>>();
            CachedJsonWeapons[acName] = stationDict; 

            string acDir = Path.Combine(RootDir, acName);
            if (!Directory.Exists(acDir)) return stationDict;

            var wm = aircraft.GetComponentInChildren<WeaponManager>(true);
            if (wm == null || wm.hardpointSets == null) return stationDict;

            for (int i = 0; i < wm.hardpointSets.Length; i++)
            {
                string hsNameStr = !string.IsNullOrEmpty(wm.hardpointSets[i].name) ? SanitizeFileName(wm.hardpointSets[i].name) : ("weaponstation" + i);
                string stationDir = Path.Combine(acDir, "weaponstation" + i);
                string jsonPath = Path.Combine(stationDir, hsNameStr + ".json");

                if (File.Exists(jsonPath))
                {
                    try
                    {
                        string json = File.ReadAllText(jsonPath);
                        var options = JsonUtility.FromJson<WeaponStationOptions>(json);
                        if (options != null && options.allowedWeapons != null)
                        {
                            var newOptions = new List<WeaponMount>();
                            foreach (string wName in options.allowedWeapons)
                            {
                                var mount = ResolveMount(wName);
                                if (mount != null) newOptions.Add(mount);
                            }
                            if (newOptions.Count > 0)
                                stationDict[i] = newOptions;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Failed to parse JSON at {jsonPath}: {ex.Message}");
                    }
                }
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

    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    internal static class Patch_Encyclopedia_AfterLoad_Dictionary
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
}
