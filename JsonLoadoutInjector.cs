using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using NuclearOption.SavedMission;
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
        private static string SchemaMarkerPath => Path.Combine(RootDir, ".schema-version");
        public static readonly Dictionary<string, Dictionary<int, List<WeaponMount>>> CachedJsonWeapons =
            new Dictionary<string, Dictionary<int, List<WeaponMount>>>();
        private struct StationRow
        {
            public string Aircraft;
            public int Index;
            public string Name;
            public string SymmetryName;
            public int HardpointCount;
            public string Precluding;
            public string Owner;
        }
        private static readonly List<StationRow> _stationRows = new List<StationRow>();
        private static int _stationRowsVersion;
        private static bool _schemaChecked;
        private static bool _forceRegen;
        private static readonly HashSet<string> _regenerated = new HashSet<string>(StringComparer.Ordinal);
        internal static bool EncyclopediaReady;
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
        internal static string GetAircraftName(UnitDefinition def)
        {
            if (def == null) return "unknown";
            if (!string.IsNullOrEmpty(def.jsonKey)) return SanitizeFileName(def.jsonKey);
            if (!string.IsNullOrEmpty(def.unitName)) return SanitizeFileName(def.unitName);
            return "unknown";
        }
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            bool needsReplacement = false;
            for (int i = 0; i < name.Length && !needsReplacement; i++)
            {
                for (int j = 0; j < invalidChars.Length; j++)
                {
                    if (name[i] == invalidChars[j])
                    {
                        needsReplacement = true;
                        break;
                    }
                }
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
        private static int _lastDictionaryStationVersion = -1;
        private static Dictionary<string, WeaponMount> _mountIndex;
        public static void ClearCache()
        {
            _mountIndex = null;
            CachedJsonWeapons.Clear();
            _stationRows.Clear();
            _stationRowsVersion++;
            _lastDictionaryMountCount = -1;
            _lastDictionaryStationVersion = -1;
            InjectionPipeline.ClearCache();
            LoadoutPresets.AiLoadoutPool.ClearCache();
        }
        private static void BuildMountIndex(List<WeaponMount> mounts)
        {
            var index = new Dictionary<string, WeaponMount>(mounts.Count * 2, StringComparer.Ordinal);
            foreach (var m in mounts)
            {
                if (m == null) continue;
                if (!string.IsNullOrEmpty(m.name)) index[m.name] = m;
                if (!string.IsNullOrEmpty(m.jsonKey)) index[m.jsonKey] = m;
            }
            _mountIndex = index;
        }
        public static void GenerateDictionary(bool force = false)
        {
            if (Encyclopedia.i == null) return;
            var mounts = GetAvailableWeaponMounts();
            if (mounts.Count == 0) return;
            if (_mountIndex == null || mounts.Count != _lastDictionaryMountCount)
                BuildMountIndex(mounts);
            if (!force
                && mounts.Count == _lastDictionaryMountCount
                && _stationRowsVersion == _lastDictionaryStationVersion) return;
            _lastDictionaryMountCount = mounts.Count;
            _lastDictionaryStationVersion = _stationRowsVersion;
            if (LoadoutInjectorPlugin.Cfg_WriteHardpointDictionary?.Value == false) return;
            if (!EncyclopediaReady) return;
            WriteDictionary(mounts);
        }
        private static void WriteDictionary(List<WeaponMount> mounts)
        {
            string dictPath = Path.Combine(RootDir, "hardpointdictionary.log");
            var sb = new StringBuilder(mounts.Count * 96 + 2048);
            sb.Append("=== Loadout Injector ").Append(LoadoutInjectorPlugin.SchemaVersion)
              .Append(" - hardpoint dictionary ===\n\n")
              .Append("owner       which mod shipped the asset, from the loaded asset bundle that contains it\n")
              .Append("jsonKey     the id saved in mission json. Won't always truthful when owner updates.\n")
              .Append("displayName the in-game label, from WeaponInfo.weaponName - (AShM2) stands for (AGM-99).\n")
              .Append("assetName   the current name of the asset. Tracks updates; not portable to vanilla mission json.\n")
              .Append("ammo        rounds per hardpoint, recounted from the prefab by\n")
              .Append("note        STALE-KEY = jsonKey disagrees with ammo and assetName, update happened.\n\n")
              .Append("Paste jsonKey into a weaponstation json's allowedWeapons\n")
              .Append("except on STALE-KEY rows, where assetName is the accurate spelling. Both resolve either way.\n\n");
            sb.Append("[A] WEAPON MOUNTS\n")
              .Append("owner\tjsonKey\tdisplayName\tassetName\tammo\tnote\n");
            foreach (var m in mounts
                .Where(m => m != null)
                .OrderBy(m => string.IsNullOrEmpty(m.jsonKey) ? m.name : m.jsonKey, StringComparer.Ordinal))
            {
                sb.Append(ModAttribution.OwnerOf(m)).Append('\t')
                  .Append(string.IsNullOrEmpty(m.jsonKey) ? m.name : m.jsonKey).Append('\t')
                  .Append(string.IsNullOrEmpty(m.mountName) ? "-" : m.mountName).Append('\t')
                  .Append(m.name).Append('\t')
                  .Append(m.ammo).Append('\t')
                  .Append(SuffixNote(m))
                  .Append('\n');
            }
            sb.Append("\n[B] HARDPOINT SETS (weapon stations)\n");
            if (_stationRows.Count == 0)
            {
                sb.Append("(no aircraft scanned yet - spawn one or open the loadout screen)\n");
            }
            else
            {
                foreach (var r in _stationRows.OrderBy(r => r.Aircraft, StringComparer.Ordinal).ThenBy(r => r.Index))
                {
                    sb.Append(r.Aircraft).Append('\t')
                      .Append(r.Index).Append('\t')
                      .Append(string.IsNullOrEmpty(r.Name) ? "-" : r.Name).Append('\t')
                      .Append(string.IsNullOrEmpty(r.SymmetryName) ? "-" : r.SymmetryName).Append('\t')
                      .Append(r.HardpointCount).Append('\t')
                      .Append(string.IsNullOrEmpty(r.Precluding) ? "-" : r.Precluding).Append('\t')
                      .Append(r.Owner)
                      .Append('\n');
                }
            }
            try
            {
                Directory.CreateDirectory(RootDir);
                File.WriteAllText(dictPath, sb.ToString());
                if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                    LoadoutInjectorPlugin.ModLogger.LogInfo(
                        $"[JsonInjector] Dictionary written to {dictPath}: {mounts.Count} mounts, {_stationRows.Count} stations.");
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Could not write dictionary: {ex.Message}");
            }
        }
        private static int TrailingCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            for (int i = s.Length - 1; i >= 1; i--)
            {
                if (s[i] < '0' || s[i] > '9') continue;
                int end = i;
                int start = i;
                while (start >= 1 && s[start - 1] >= '0' && s[start - 1] <= '9') start--;
                if (start == 0 || (s[start - 1] != 'x' && s[start - 1] != 'X'))
                {
                    i = start; 
                    continue;
                }
                int value = 0;
                for (int j = start; j <= end; j++) value = value * 10 + (s[j] - '0');
                return value;
            }
            return -1;
        }
        internal static string PreferredKey(WeaponMount m)
        {
            if (m == null) return null;
            string jsonKey = !string.IsNullOrEmpty(m.jsonKey) ? m.jsonKey : m.name;
            if (m.ammo <= 0 || string.IsNullOrEmpty(m.name)) return jsonKey;
            int fromKey = TrailingCount(jsonKey);
            if (fromKey < 0 || fromKey == m.ammo) return jsonKey;
            return TrailingCount(m.name) == m.ammo ? m.name : jsonKey;
        }
        private static string SuffixNote(WeaponMount m)
        {
            if (m == null || m.ammo <= 0) return "";
            string jsonKey = !string.IsNullOrEmpty(m.jsonKey) ? m.jsonKey : m.name;
            int fromKey = TrailingCount(jsonKey);
            if (fromKey < 0 || fromKey == m.ammo) return "";
            if (TrailingCount(m.name) == m.ammo)
                return $"STALE-KEY(jsonKey says {fromKey}, real {m.ammo} - using assetName)";
            return $"CHECK(suffix is not a round count; ammo {m.ammo} is authoritative)";
        }
        internal static WeaponMount ResolveMount(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_mountIndex == null)
                BuildMountIndex(GetAvailableWeaponMounts());
            if (_mountIndex.TryGetValue(name, out var found) && found != null)
                return found;
            if (Encyclopedia.WeaponLookup != null && Encyclopedia.WeaponLookup.TryGetValue(name, out var encyMount))
                return encyMount;
            return null;
        }
        private static void EnsureSchemaChecked()
        {
            if (_schemaChecked) return;
            _schemaChecked = true;
            try
            {
                string stamp = File.Exists(SchemaMarkerPath) ? File.ReadAllText(SchemaMarkerPath).Trim() : null;
                if (stamp == LoadoutInjectorPlugin.PresetSchemaStamp) return;
                _forceRegen = true;
                LoadoutInjectorPlugin.ModLogger.LogInfo(
                    $"[JsonInjector] Schema {stamp ?? "(none)"} -> {LoadoutInjectorPlugin.PresetSchemaStamp}: "
                    + "regenerating preset-loadout in place.");
                Directory.CreateDirectory(RootDir);
                File.WriteAllText(SchemaMarkerPath, LoadoutInjectorPlugin.PresetSchemaStamp);
            }
            catch (Exception ex)
            {
                _forceRegen = false;
                LoadoutInjectorPlugin.ModLogger.LogError($"[JsonInjector] Schema check failed, keeping existing jsons: {ex.Message}");
            }
        }
        public static Dictionary<int, List<WeaponMount>> GetLoadout(string acName, Aircraft aircraft)
        {
            EnsureSchemaChecked(); 
            GenerateDictionary();
            if (CachedJsonWeapons.TryGetValue(acName, out var stationDict))
                return stationDict;
            stationDict = new Dictionary<int, List<WeaponMount>>();
            CachedJsonWeapons[acName] = stationDict; 
            var wm = aircraft == null ? null : aircraft.GetComponentInChildren<WeaponManager>(true);
            if (wm == null || wm.hardpointSets == null) return stationDict;
            bool regen = _forceRegen && EncyclopediaReady && _regenerated.Add(acName);
            string acDir = Path.Combine(RootDir, acName);
            Directory.CreateDirectory(acDir);
            for (int i = 0; i < wm.hardpointSets.Length; i++)
            {
                var hs = wm.hardpointSets[i];
                if (hs == null) continue;
                string stationDir = Path.Combine(acDir, "weaponstation" + i);
                Directory.CreateDirectory(stationDir);
                RecordStation(acName, i, hs, aircraft);
                string[] existingJsons = Directory.GetFiles(stationDir, "*.json");
                if (existingJsons.Length == 0 || regen)
                {
                    string hsName = !string.IsNullOrEmpty(hs.name) ? SanitizeFileName(hs.name) : ("weaponstation" + i);
                    string jsonPath = Path.Combine(stationDir, hsName + ".json");
                    var defaultOptions = new WeaponStationOptions();
                    var sourceOptions = PristineOptions(hs);
                    if (sourceOptions != null)
                    {
                        foreach (var mount in sourceOptions)
                        {
                            if (mount == null) continue;
                            string wKey = PreferredKey(mount);
                            if (!defaultOptions.allowedWeapons.Contains(wKey))
                                defaultOptions.allowedWeapons.Add(wKey);
                        }
                    }
                    try
                    {
                        foreach (string old in existingJsons)
                        {
                            if (!string.Equals(old, jsonPath, StringComparison.OrdinalIgnoreCase))
                                File.Delete(old);
                        }
                        File.WriteAllText(jsonPath, JsonUtility.ToJson(defaultOptions, true));
                        if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                            LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Wrote JSON for {acName} station {i} ({hsName}): {jsonPath}");
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
                        var options = JsonUtility.FromJson<WeaponStationOptions>(File.ReadAllText(jsonPath));
                        if (options?.allowedWeapons == null) continue;
                        foreach (string wName in options.allowedWeapons)
                        {
                            var mount = ResolveMount(wName);
                            if (mount != null)
                            {
                                if (!newOptions.Contains(mount)) newOptions.Add(mount);
                            }
                            else if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                            {
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
            if (aircraft.definition != null)
            {
                try { LoadoutPresets.PresetIO.DumpVanillaForAircraft(aircraft.definition, false); }
                catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] Vanilla preset seed failed: " + ex); }
            }
            if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true && stationDict.Count > 0)
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[JsonInjector] Lazy-cached {stationDict.Count} stations for aircraft {acName}");
            GenerateDictionary(); 
            return stationDict;
        }
        private static void RecordStation(string acName, int index, HardpointSet hs, Aircraft aircraft)
        {
            for (int r = 0; r < _stationRows.Count; r++)
            {
                if (_stationRows[r].Index == index && _stationRows[r].Aircraft == acName) return;
            }
            _stationRows.Add(new StationRow
            {
                Aircraft = acName,
                Index = index,
                Name = hs.name,
                SymmetryName = hs.SymmetryName,
                HardpointCount = hs.hardpoints == null ? 0 : hs.hardpoints.Count,
                Precluding = (hs.precludingHardpointSets == null || hs.precludingHardpointSets.Count == 0)
                    ? null
                    : string.Join(",", hs.precludingHardpointSets.Select(b => b.ToString()).ToArray()),
                Owner = ModAttribution.OwnerOfStation(aircraft, index, hs)
            });
            _stationRowsVersion++;
        }
        private static readonly ConditionalWeakTable<HardpointSet, List<WeaponMount>> _pristineOptions =
            new ConditionalWeakTable<HardpointSet, List<WeaponMount>>();
        internal static List<WeaponMount> PristineOptions(HardpointSet hs)
        {
            if (hs == null) return null;
            if (_pristineOptions.TryGetValue(hs, out var snapshot)) return snapshot;
            snapshot = new List<WeaponMount>(hs.weaponOptions ?? new List<WeaponMount>());
            _pristineOptions.Add(hs, snapshot);
            return snapshot;
        }
        internal static List<WeaponMount> BuildWhitelist(HardpointSet hs, List<WeaponMount> allowed)
        {
            if (hs == null) return null;
            if (LoadoutInjectorPlugin.Cfg_StrictStationWhitelist?.Value == false) return null;
            if (allowed == null || allowed.Count == 0) return null;
            var pristine = PristineOptions(hs);
            bool hadEmptyOption = pristine != null && pristine.Count > 0 && pristine[0] == null;
            var list = new List<WeaponMount>(allowed.Count + 1);
            if (hadEmptyOption) list.Add(null);
            var seen = new HashSet<int>();
            foreach (var mount in allowed)
            {
                if (mount != null && seen.Add(mount.GetInstanceID()))
                    list.Add(mount);
            }
            return list.Count == (hadEmptyOption ? 1 : 0) ? null : list;
        }
        internal static bool IsMountAllowed(HardpointSet hs, List<WeaponMount> allowed, WeaponMount mount)
        {
            if (mount == null) return true; 
            if (hs == null) return true;
            if (LoadoutInjectorPlugin.Cfg_StrictStationWhitelist?.Value == false) return true;
            if (allowed == null) return true;
            bool found = false;
            int real = 0;
            for (int i = 0; i < allowed.Count; i++)
            {
                if (allowed[i] == null) continue;
                real++;
                if (allowed[i] == mount) found = true;
            }
            return real == 0 || found;
        }
        internal static void Inject(WeaponManager wm)
        {
            if (wm == null || wm.hardpointSets == null) return;
            var aircraft = wm.GetComponentInParent<Aircraft>();
            if (aircraft == null) return;
            string acName = GetAircraftName(aircraft);
            var dict = GetLoadout(acName, aircraft);
            if (dict == null || dict.Count == 0) return;
            for (int i = 0; i < wm.hardpointSets.Length; i++)
            {
                var hs = wm.hardpointSets[i];
                if (hs == null) continue;
                if (!dict.TryGetValue(i, out var customMounts)) continue;
                PristineOptions(hs); 
                var whitelist = BuildWhitelist(hs, customMounts);
                if (whitelist != null) hs.weaponOptions = whitelist;
            }
        }
    }
    [HarmonyPatch(typeof(GameManager), "SetupGame")]
    internal static class Patch_GameManager_SetupGame_Dictionary
    {
        static void Postfix()
        {
            try { JsonLoadoutInjector.GenerateDictionary(); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] SetupGame patch failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { typeof(Encyclopedia) })]
    internal static class Patch_Encyclopedia_AfterLoad_Dictionary1
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(Encyclopedia instance)
        {
            try { ModAttribution.CaptureBaseline(instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[Attribution] Baseline capture failed: " + ex); }
        }
        [HarmonyPriority(Priority.Last)]
        static void Postfix()
        {
            try
            {
                JsonLoadoutInjector.EncyclopediaReady = true;
                JsonLoadoutInjector.ClearCache();
            }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] AfterLoad postfix failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    internal static class Patch_Encyclopedia_AfterLoad_Dictionary2
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(Encyclopedia __instance)
        {
            try { ModAttribution.CaptureBaseline(__instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[Attribution] Baseline capture failed: " + ex); }
        }
        [HarmonyPriority(Priority.Last)]
        static void Postfix()
        {
            try
            {
                JsonLoadoutInjector.EncyclopediaReady = true;
                JsonLoadoutInjector.ClearCache();
            }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] AfterLoad postfix failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(AircraftSelectionMenu), "OnEnable")]
    internal static class Patch_AircraftSelectionMenu_OnEnable_Dictionary
    {
        static void Postfix()
        {
            try { JsonLoadoutInjector.GenerateDictionary(); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] AircraftSelectionMenu patch failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "Awake")]
    internal static class Patch_WeaponManager_Awake_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            try { JsonLoadoutInjector.Inject(__instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager Awake patch failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "InitializeWeaponManager")]
    internal static class Patch_WeaponManager_Initialize_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            try { JsonLoadoutInjector.Inject(__instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager InitializeWeaponManager patch failed: " + ex); }
        }
    }
    [HarmonyPatch(typeof(AircraftParameters), "GetRandomStandardLoadout")]
    internal static class Patch_AircraftParameters_GetRandomStandardLoadout
    {
        static void Postfix(AircraftDefinition definition, FactionHQ hq, ref StandardLoadout __result)
        {
            try
            {
                if (LoadoutInjectorPlugin.Cfg_AIAlwaysRandomiseLoadout?.Value == true)
                {
                    __result = null; 
                    return;
                }
                if (definition?.unitPrefab == null) return;
                var aircraft = definition.unitPrefab.GetComponent<Aircraft>();
                var wm = aircraft?.weaponManager;
                if (wm?.hardpointSets == null) return;
                var candidates = new List<StandardLoadout>();
                var stock = definition.aircraftParameters?.StandardLoadouts;
                if (stock != null)
                {
                    foreach (var sl in stock)
                    {
                        if (sl == null || sl.disabled || sl.loadout == null) continue;
                        if (hq != null && !sl.AllowedByHQ(wm, hq)) continue;
                        if (!PassesStationJson(definition, aircraft, wm, sl.loadout)) continue;
                        candidates.Add(sl);
                    }
                }
                foreach (var preset in LoadoutPresets.AiLoadoutPool.Compose(definition))
                {
                    if (preset?.loadout == null) continue;
                    if (hq != null && !preset.AllowedByHQ(wm, hq)) continue;
                    bool duplicate = false;
                    foreach (var existing in candidates)
                    {
                        if (LoadoutPresets.AiLoadoutPool.SameMounts(existing.loadout, preset.loadout))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate) candidates.Add(preset);
                }
                if (candidates.Count == 0)
                {
                    __result = null; 
                    return;
                }
                __result = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                {
                    LoadoutInjectorPlugin.ModLogger.LogInfo(
                        $"[JsonInjector] AI pool for {JsonLoadoutInjector.GetAircraftName(definition)}: {candidates.Count} candidate(s), picked '{__result.Name}'.");
                }
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] GetRandomStandardLoadout patch failed: " + ex);
            }
        }
        private static bool PassesStationJson(AircraftDefinition definition, Aircraft aircraft,
                                              WeaponManager wm, Loadout loadout)
        {
            if (loadout?.weapons == null) return true;
            var dict = JsonLoadoutInjector.GetLoadout(JsonLoadoutInjector.GetAircraftName(definition), aircraft);
            if (dict == null || dict.Count == 0) return true;
            int count = Math.Min(loadout.weapons.Count, wm.hardpointSets.Length);
            for (int i = 0; i < count; i++)
            {
                var mount = loadout.weapons[i];
                if (mount == null) continue;
                if (!dict.TryGetValue(i, out var allowed)) continue;
                if (!JsonLoadoutInjector.IsMountAllowed(wm.hardpointSets[i], allowed, mount)) return false;
            }
            return true;
        }
    }
    [HarmonyPatch]
    internal static class Patch_WeaponManager_LoadHardpointSet
    {
        static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(WeaponManager), "LoadHardpointSet",
                new Type[] { typeof(HardpointSet), typeof(WeaponMount) });
        }
        static void Prefix(WeaponManager __instance, HardpointSet hardpointSet, ref WeaponMount weaponMount)
        {
            try
            {
                if (weaponMount == null || hardpointSet == null || __instance?.hardpointSets == null) return;
                var aircraft = __instance.GetComponentInParent<Aircraft>();
                if (aircraft == null) return;
                int stationIndex = -1;
                for (int i = 0; i < __instance.hardpointSets.Length; i++)
                {
                    if (__instance.hardpointSets[i] == hardpointSet) { stationIndex = i; break; }
                }
                if (stationIndex < 0) return;
                var dict = JsonLoadoutInjector.GetLoadout(JsonLoadoutInjector.GetAircraftName(aircraft), aircraft);
                if (dict == null || !dict.TryGetValue(stationIndex, out var allowed)) return;
                if (JsonLoadoutInjector.IsMountAllowed(hardpointSet, allowed, weaponMount)) return;
                if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                {
                    LoadoutInjectorPlugin.ModLogger.LogInfo(
                        $"[JsonInjector] Blocked '{weaponMount.name}' on station {stationIndex} ({hardpointSet.name}): not in its json.");
                }
                weaponMount = null; 
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] LoadHardpointSet patch failed: " + ex);
            }
        }
    }
    [HarmonyPatch(typeof(WeaponManager), "SpawnWeapons")]
    internal static class Patch_WeaponManager_SpawnWeapons_JsonInject
    {
        static void Prefix(WeaponManager __instance)
        {
            try { JsonLoadoutInjector.Inject(__instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[JsonInjector] WeaponManager SpawnWeapons patch failed: " + ex); }
        }
    }
}