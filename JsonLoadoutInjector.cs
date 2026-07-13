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

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
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

            var lines = new List<string>();
            lines.Add("=== Weapon Mount Dictionary ===");
            lines.Add("Use these exact names in your json files:");
            lines.Add("");

            foreach (var m in mounts.OrderBy(m => m.name))
            {
                string key = !string.IsNullOrEmpty(m.jsonKey) ? m.jsonKey : m.name;
                lines.Add(key);
            }

            lines = lines.Distinct().ToList();

            File.WriteAllLines(dictPath, lines);
            dictionaryGenerated = true;
            LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Generated dictionary at {dictPath} with {mounts.Count} mounts.");
        }

        internal static void Inject(WeaponManager wm)
        {
            if (wm == null || wm.hardpointSets == null) return;

            var aircraft = wm.GetComponentInParent<Aircraft>();
            if (aircraft == null) return;

            string acName = SanitizeFileName(aircraft.unitName ?? aircraft.name ?? "unknown");
            string acDir = Path.Combine(RootDir, acName);

            if (!Directory.Exists(acDir))
                Directory.CreateDirectory(acDir);

            for (int i = 0; i < wm.hardpointSets.Length; i++)
            {
                var hs = wm.hardpointSets[i];
                if (hs == null) continue;

                string stationDir = Path.Combine(acDir, "weaponstation" + i);
                if (!Directory.Exists(stationDir))
                    Directory.CreateDirectory(stationDir);

                string hsNameStr = !string.IsNullOrEmpty(hs.name) ? SanitizeFileName(hs.name) : ("weaponstation" + i);
                string jsonPath = Path.Combine(stationDir, hsNameStr + ".json");

                if (!File.Exists(jsonPath))
                {

                    var defaults = new WeaponStationOptions();
                    if (hs.weaponOptions != null)
                    {
                        foreach (var opt in hs.weaponOptions)
                        {
                            if (opt != null)
                            {
                                string key = !string.IsNullOrEmpty(opt.jsonKey) ? opt.jsonKey : opt.name;
                                defaults.allowedWeapons.Add(key);
                            }
                        }
                    }
                    string json = JsonUtility.ToJson(defaults, true);
                    File.WriteAllText(jsonPath, json);
                }
                else
                {

                    try
                    {
                        string json = File.ReadAllText(jsonPath);
                        var options = JsonUtility.FromJson<WeaponStationOptions>(json);

                        if (options != null && options.allowedWeapons != null)
                        {
                            var newOptions = new List<WeaponMount>();
                            var allMounts = Resources.FindObjectsOfTypeAll<WeaponMount>();

                            foreach (string wName in options.allowedWeapons)
                            {
                                WeaponMount mount = null;

                                if (Encyclopedia.WeaponLookup != null && Encyclopedia.WeaponLookup.TryGetValue(wName, out var encyMount))
                                {
                                    mount = encyMount;
                                }

                                if (mount == null)
                                {
                                    mount = allMounts.FirstOrDefault(m => m != null && (m.name == wName || m.jsonKey == wName));
                                }

                                if (mount != null)
                                    newOptions.Add(mount);
                                else
                                    LoadoutInjectorPlugin.ModLogger.LogWarning($"[JsonInjector] WeaponMount '{wName}' not found for {acName} station {i}.");
                            }

                            hs.weaponOptions = newOptions;
                        }
                    }
                    catch (Exception ex)
                    {
                        LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Failed to parse JSON at {jsonPath}: {ex.Message}");
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
