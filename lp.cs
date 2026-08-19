using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using NuclearOption.SavedMission;
using NuclearOption.Networking;
using LoadoutInjector;
namespace LoadoutPresets
{
    internal static class Plugin
    {
        internal const string DEFAULTPRESET = "DEFAULT";
    }
    internal static class MenuRefs
    {
        internal static readonly AccessTools.FieldRef<LoadoutSelector, List<WeaponSelector>> WeaponSelectors =
            AccessTools.FieldRefAccess<LoadoutSelector, List<WeaponSelector>>("weaponSelectors");
        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Aircraft> PreviewAircraft =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Aircraft>("previewAircraft");
        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, LoadoutSelector> LoadoutSelectorRef =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, LoadoutSelector>("loadoutSelector");
        internal static readonly AccessTools.FieldRef<AircraftSelectionMenu, Slider> MenuFuelSlider =
            AccessTools.FieldRefAccess<AircraftSelectionMenu, Slider>("fuelLevel");
        internal static readonly AccessTools.FieldRef<LoadoutSelector, Slider> LoadoutFuelSlider =
            AccessTools.FieldRefAccess<LoadoutSelector, Slider>("fuelLevel");
        internal static readonly AccessTools.FieldRef<LoadoutSelector, FactionHQ> SelectorHQ =
            AccessTools.FieldRefAccess<LoadoutSelector, FactionHQ>("hq");
        internal static readonly AccessTools.FieldRef<LoadoutSelector, Airbase> SelectorAirbase =
            AccessTools.FieldRefAccess<LoadoutSelector, Airbase>("airbase");
    }
    internal static class AiLoadoutPool
    {
        private static readonly Dictionary<string, List<StandardLoadout>> _cache =
            new Dictionary<string, List<StandardLoadout>>(StringComparer.Ordinal);
        internal static void ClearCache() { _cache.Clear(); }
        internal static List<StandardLoadout> Compose(AircraftDefinition def)
        {
            if (def == null) return null;
            string acName = JsonLoadoutInjector.GetAircraftName(def);
            List<StandardLoadout> cached;
            if (_cache.TryGetValue(acName, out cached)) return cached;
            var built = new List<StandardLoadout>();
            _cache[acName] = built; 
            if (def.unitPrefab == null) return built;
            var prefabAircraft = def.unitPrefab.GetComponent<Aircraft>();
            var wm = prefabAircraft != null ? prefabAircraft.weaponManager : null;
            if (wm == null || wm.hardpointSets == null) return built;
            string dir = PresetIO.AircraftDir(def);
            if (!Directory.Exists(dir)) return built;
            var stationAllow = JsonLoadoutInjector.GetLoadout(acName, prefabAircraft);
            var sets = wm.hardpointSets;
            foreach (string file in Directory.GetFiles(dir, "*.preset"))
            {
                try
                {
                    var parsed = PresetIO.ParseJson(File.ReadAllText(file));
                    var chosen = new WeaponMount[sets.Length];
                    string invalidReason = null;
                    for (int i = 0; i < sets.Length && invalidReason == null; i++)
                    {
                        if (sets[i] == null) continue;
                        string key = PresetIO.KeyForStation(parsed, sets, i);
                        if (string.IsNullOrEmpty(key)) continue;
                        WeaponMount mount = JsonLoadoutInjector.ResolveMount(key);
                        if (mount == null)
                        {
                            invalidReason = $"'{key}' on {PresetIO.StationKey(sets, i)} no longer exists";
                            break;
                        }
                        List<WeaponMount> allowed;
                        if (stationAllow != null && stationAllow.TryGetValue(i, out allowed)
                            && !JsonLoadoutInjector.IsMountAllowed(sets[i], allowed, mount))
                        {
                            invalidReason = $"'{key}' is not in {PresetIO.StationKey(sets, i)}'s json";
                            break;
                        }
                        chosen[i] = mount;
                    }
                    if (invalidReason != null)
                    {
                        if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                            LoadoutInjectorPlugin.ModLogger.LogInfo(
                                $"[Presets] AI pool: dropped '{Path.GetFileNameWithoutExtension(file)}' for {acName} - {invalidReason}.");
                        continue;
                    }
                    var precluded = new HashSet<int>();
                    for (int i = 0; i < sets.Length; i++)
                    {
                        if (chosen[i] == null || sets[i] == null) continue;
                        if (sets[i].precludingHardpointSets == null) continue;
                        foreach (byte idx in sets[i].precludingHardpointSets) precluded.Add(idx);
                    }
                    var loadout = new Loadout { weapons = new List<WeaponMount>(sets.Length) };
                    for (int i = 0; i < sets.Length; i++)
                        loadout.weapons.Add(precluded.Contains(i) ? null : chosen[i]);
                    built.Add(new StandardLoadout
                    {
                        disabled = false,
                        Name = Path.GetFileNameWithoutExtension(file),
                        loadout = loadout,
                        FuelRatio = PresetIO.ResolveFuel(parsed, def)
                    });
                }
                catch (Exception ex)
                {
                    LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Could not compose " + file + ": " + ex.Message);
                }
            }
            return built;
        }
        internal static bool SameMounts(Loadout a, Loadout b)
        {
            if (a == null || b == null || a.weapons == null || b.weapons == null) return false;
            if (a.weapons.Count != b.weapons.Count) return false;
            for (int i = 0; i < a.weapons.Count; i++)
                if (a.weapons[i] != b.weapons[i]) return false;
            return true;
        }
    }
    internal static class PresetIO
    {
        private const string ActiveKey = "ActivePreset";
        private static string Norm(string preset)
        {
            preset = Regex.Replace((preset ?? "").Trim(), @"[=\r\n\t\\\""'\[\]]", "_");
            return preset.Length == 0 ? Plugin.DEFAULTPRESET : preset;
        }
        internal static string RootDir => JsonLoadoutInjector.RootDir;
        private static string LegacyRootDir => Path.Combine(Paths.PluginPath, "loadout-preset");
        internal static string AircraftDir(AircraftDefinition def)
        {
            MigrateLegacyTree();
            return Path.Combine(RootDir, JsonLoadoutInjector.GetAircraftName(def));
        }
        internal static string PresetFilePath(AircraftDefinition def, string preset) =>
            Path.Combine(AircraftDir(def), SanitizeName(Norm(preset)) + ".preset");
        private static string SanitizeName(string name)
        {
            name = name ?? "";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
        private static bool _migrated;
        private static void MigrateLegacyTree()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string legacy = LegacyRootDir;
                if (!Directory.Exists(legacy)) return;
                int moved = 0;
                foreach (string acDir in Directory.GetDirectories(legacy))
                {
                    string target = Path.Combine(RootDir, Path.GetFileName(acDir));
                    foreach (string file in Directory.GetFiles(acDir, "*.preset"))
                    {
                        string dest = Path.Combine(target, Path.GetFileName(file));
                        if (File.Exists(dest)) continue; 
                        Directory.CreateDirectory(target);
                        File.Move(file, dest);
                        moved++;
                    }
                }
                if (moved > 0)
                    LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Migrated {moved} preset(s) from loadout-preset into preset-loadout.");
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Legacy preset migration failed: " + ex.Message);
            }
        }
        private static ConfigEntry<T> Entry<T>(string section, string key, T def) =>
            LoadoutInjectorPlugin.Instance.Config.Bind(section, key, def);
        private static T Get<T>(string section, string key, T def) => Entry(section, key, def).Value;
        private static void Set<T>(string section, string key, T def, T value) => Entry(section, key, def).Value = value;
        internal static string BaseSection(AircraftDefinition def) => $"Aircraft:{def.unitName}";
        internal static string GetActivePreset(AircraftDefinition def) =>
            Norm(Get(BaseSection(def), ActiveKey, Plugin.DEFAULTPRESET));
        internal static void SetActivePreset(AircraftDefinition def, string preset) =>
            Set(BaseSection(def), ActiveKey, Plugin.DEFAULTPRESET, Norm(preset));
        internal static bool IsSaved(AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            if (preset == Plugin.DEFAULTPRESET)
                return true;
            return File.Exists(PresetFilePath(def, preset));
        }
        private static bool _warnedLiverySerialize;
        private static bool _warnedLiveryRestore;
        private static string SerializeLiveryKey(object boxedKey)
        {
            if (boxedKey == null) return "";
            try
            {
                var fields = boxedKey.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var parts = new List<string>();
                foreach (var f in fields)
                    parts.Add($"{f.Name}={f.GetValue(boxedKey)}");
                return string.Join(";", parts);
            }
            catch (Exception ex)
            {
                if (!_warnedLiverySerialize)
                {
                    _warnedLiverySerialize = true;
                    LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Failed to serialize livery key: " + ex.Message);
                }
                return "";
            }
        }
        private static object DeserializeLiveryKey(Type liveryKeyType, string serialized)
        {
            if (string.IsNullOrEmpty(serialized) || liveryKeyType == null) return null;
            try
            {
                object boxed = Activator.CreateInstance(liveryKeyType);
                foreach (string part in serialized.Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq < 0) continue;
                    FieldInfo f = liveryKeyType.GetField(part.Substring(0, eq), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f == null) continue;
                    string fval = part.Substring(eq + 1);
                    object converted = f.FieldType.IsEnum ? Enum.Parse(f.FieldType, fval) : Convert.ChangeType(fval, f.FieldType);
                    f.SetValue(boxed, converted);
                }
                return boxed;
            }
            catch (Exception ex)
            {
                if (!_warnedLiveryRestore)
                {
                    _warnedLiveryRestore = true;
                    LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Failed to restore livery from preset: " + ex.Message);
                }
                return null;
            }
        }
        private static string EscapeJson(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string UnescapeJson(string s) => (s ?? "").Replace("\\\"", "\"").Replace("\\\\", "\\");
        internal static string StationKey(HardpointSet[] sets, int i)
        {
            if (sets == null || i < 0 || i >= sets.Length) return "#" + i;
            string name = sets[i]?.name;
            if (string.IsNullOrEmpty(name)) return "#" + i;
            int seen = 0;
            for (int j = 0; j < sets.Length; j++)
                if (sets[j] != null && sets[j].name == name) seen++;
            return seen > 1 ? name + "#" + i : name;
        }
        private static string BuildJson(float fuel, string livery, List<string> hardpoints,
                                        List<string> stations, bool vanilla)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"Fuel\":").Append(fuel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"Livery\":\"").Append(EscapeJson(livery)).Append("\"");
            if (vanilla) sb.Append(",\"Vanilla\":1");
            sb.Append(",\"Stations\":[");
            for (int i = 0; i < stations.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(stations[i])).Append("\"");
            }
            sb.Append("]");
            sb.Append(",\"Hardpoints\":[");
            for (int i = 0; i < hardpoints.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(hardpoints[i])).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }
        internal struct ParsedPreset
        {
            public float Fuel;
            public string Livery;
            public bool Vanilla;
            public List<string> Hardpoints;              
            public Dictionary<string, string> Stations;  
        }
        private static List<string> ReadStringArray(string json, string field)
        {
            var list = new List<string>();
            var m = Regex.Match(json, @"""" + field + @"""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match e in Regex.Matches(m.Groups[1].Value, @"""([^""]*)"""))
                list.Add(UnescapeJson(e.Groups[1].Value));
            return list;
        }
        internal static ParsedPreset ParseJson(string json)
        {
            var result = new ParsedPreset
            {
                Fuel = 1f,
                Livery = "",
                Vanilla = false,
                Hardpoints = new List<string>(),
                Stations = new Dictionary<string, string>(StringComparer.Ordinal)
            };
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var fuelMatch = Regex.Match(json, @"""Fuel""\s*:\s*([0-9.eE+-]+)");
                if (fuelMatch.Success)
                    result.Fuel = float.Parse(fuelMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var liveryMatch = Regex.Match(json, @"""Livery""\s*:\s*""([^""]*)""");
                if (liveryMatch.Success)
                    result.Livery = UnescapeJson(liveryMatch.Groups[1].Value);
                result.Vanilla = Regex.IsMatch(json, @"""Vanilla""\s*:\s*(1|true)", RegexOptions.IgnoreCase);
                result.Hardpoints = ReadStringArray(json, "Hardpoints");
                foreach (string entry in ReadStringArray(json, "Stations"))
                {
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    result.Stations[entry.Substring(0, eq)] = entry.Substring(eq + 1);
                }
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Failed to parse preset file: " + ex.Message);
            }
            return result;
        }
        internal static string KeyForStation(ParsedPreset parsed, HardpointSet[] sets, int i)
        {
            if (parsed.Stations != null && parsed.Stations.Count > 0)
            {
                string byName;
                if (parsed.Stations.TryGetValue(StationKey(sets, i), out byName)) return byName;
                if (sets != null && i < sets.Length && sets[i] != null
                    && parsed.Stations.TryGetValue(sets[i].name ?? "", out byName)) return byName;
                return "";
            }
            return (parsed.Hardpoints != null && i < parsed.Hardpoints.Count) ? parsed.Hardpoints[i] : "";
        }
        internal static float ResolveFuel(ParsedPreset parsed, AircraftDefinition def)
        {
            if (parsed.Fuel > 0f) return parsed.Fuel;
            var ap = def?.aircraftParameters;
            return ap != null ? ap.DefaultFuelLevel : 1f;
        }
        internal static void SaveCurrentToPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            if (string.IsNullOrWhiteSpace(preset)) return;
            var loadoutSelector = MenuRefs.LoadoutSelectorRef(menu);
            var weaponSelectors = MenuRefs.WeaponSelectors(loadoutSelector);
            var preview = MenuRefs.PreviewAircraft(menu);
            var sets = preview?.weaponManager?.hardpointSets;
            var hardpoints = new List<string>(weaponSelectors.Count);
            var stations = new List<string>(weaponSelectors.Count);
            for (int i = 0; i < weaponSelectors.Count; i++)
            {
                WeaponMount mount = weaponSelectors[i].GetValue();
                string key = mount != null ? (JsonLoadoutInjector.PreferredKey(mount) ?? "") : "";
                hardpoints.Add(key);
                stations.Add(StationKey(sets, i) + "=" + key);
            }
            float fuel = ReadFuel(menu, loadoutSelector);
            string livery = preview != null ? SerializeLiveryKey(preview.NetworkLiveryKey) : "";
            string path = PresetFilePath(def, preset);
            bool vanilla = File.Exists(path) && ParseJson(File.ReadAllText(path)).Vanilla;
            Directory.CreateDirectory(AircraftDir(def));
            File.WriteAllText(path, BuildJson(fuel, livery, hardpoints, stations, vanilla));
        }
        private static float ReadFuel(AircraftSelectionMenu menu, LoadoutSelector selector)
        {
            try
            {
                var slider = menu != null ? MenuRefs.MenuFuelSlider(menu) : null;
                if (slider != null) return slider.value;
                var alt = selector != null ? MenuRefs.LoadoutFuelSlider(selector) : null;
                if (alt != null) return alt.value;
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Could not read the fuel slider: " + ex.Message);
            }
            return 1f;
        }
        private static void WriteFuel(AircraftSelectionMenu menu, LoadoutSelector selector, float fuel)
        {
            try
            {
                var slider = menu != null ? MenuRefs.MenuFuelSlider(menu) : null;
                if (slider != null) slider.value = fuel;
                var alt = selector != null ? MenuRefs.LoadoutFuelSlider(selector) : null;
                if (alt != null && alt != slider) alt.value = fuel;
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Could not set the fuel slider: " + ex.Message);
            }
        }
        internal static List<string> LastSkipped = new List<string>();
        internal static bool LoadPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            LastSkipped = new List<string>();
            bool applied = ApplyPreset(menu, def, preset);
            SetActivePreset(def, preset);
            LoadoutInjectorPlugin.Instance.Config.Save();
            string note = LastSkipped.Count > 0 ? $" ({LastSkipped.Count} station(s) unavailable)" : "";
            LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Loaded {preset}:{def.unitName}{note} {(applied ? "" : "(no saved preset yet)")} ");
            return applied;
        }
        [ThreadStatic] private static List<WeaponMount> _availableCache;
        internal static WeaponMount ResolveForStation(HardpointSet hs, string key, LoadoutSelector selector,
                                                      out string reason)
        {
            reason = null;
            if (hs == null || string.IsNullOrEmpty(key)) return null;
            var mount = JsonLoadoutInjector.ResolveMount(key);
            if (mount == null && hs.weaponOptions != null)
                mount = hs.weaponOptions.Find(w => w != null && (w.jsonKey == key || w.name == key));
            if (mount == null)
            {
                reason = "no longer exists";
                return null;
            }
            if (hs.weaponOptions == null || !hs.weaponOptions.Contains(mount))
            {
                reason = "not in this station's json";
                return null;
            }
            if (_availableCache == null) _availableCache = new List<WeaponMount>();
            FactionHQ hq = null;
            Airbase airbase = null;
            if (selector != null)
            {
                try { hq = MenuRefs.SelectorHQ(selector); airbase = MenuRefs.SelectorAirbase(selector); }
                catch { }
            }
            Player localPlayer = null;
            if (GameManager.gameState != GameState.Editor)
                GameManager.GetLocalPlayer<Player>(out localPlayer);
            else
                hq = null; 
            WeaponChecker.GetAvailableWeaponsNonAlloc(localPlayer, hs, airbase, hq, false, _availableCache);
            if (_availableCache.Contains(mount)) return mount;
            reason = (hq != null && hq.restrictedWeapons != null && hq.restrictedWeapons.Contains(mount.name))
                ? "restricted this mission"
                : "unavailable here";
            return null;
        }
        internal static bool ApplyPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            var loadSelect = MenuRefs.LoadoutSelectorRef(menu);
            preset = Norm(preset);
            if (string.IsNullOrWhiteSpace(preset))
                return false;
            Aircraft preview = MenuRefs.PreviewAircraft(menu);
            if (preview == null || preview.weaponManager == null)
                return false;
            var sets = preview.weaponManager.hardpointSets;
            if (sets == null)
                return false;
            string path = PresetFilePath(def, preset);
            if (!File.Exists(path))
                return true;
            ParsedPreset parsed = ParseJson(File.ReadAllText(path));
            var weaponSelectors = MenuRefs.WeaponSelectors(loadSelect);
            int n = Math.Min(weaponSelectors.Count, sets.Length);
            var skipped = new List<string>();
            for (int i = 0; i < n; i++)
            {
                string key = KeyForStation(parsed, sets, i);
                WeaponMount mount = null;
                if (!string.IsNullOrEmpty(key))
                {
                    string reason;
                    mount = ResolveForStation(sets[i], key, loadSelect, out reason);
                    if (mount == null && reason != null)
                        skipped.Add($"{StationKey(sets, i)}: '{key}' {reason}");
                }
                weaponSelectors[i].SetValue(mount);
            }
            LastSkipped = skipped;
            if (skipped.Count > 0 && LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
            {
                foreach (string line in skipped)
                    LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] {preset}:{def.unitName} skipped {line}");
            }
            WriteFuel(menu, loadSelect, ResolveFuel(parsed, def));
            if (!string.IsNullOrEmpty(parsed.Livery))
            {
                object restored = DeserializeLiveryKey(preview.NetworkLiveryKey.GetType(), parsed.Livery);
                if (restored != null)
                {
                    try { preview.SetLiveryKey((LiveryKey)restored); }
                    catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Failed to apply livery: " + ex.Message); }
                }
            }
            loadSelect.UpdateWeapons(true);
            menu.StartCoroutine(RebuildWeaponsNextFrame(menu, preview));
            return true;
        }
        internal static System.Collections.IEnumerator RebuildWeaponsNextFrame(AircraftSelectionMenu menu, Aircraft aircraft)
        {
            yield return null;
            var wm = aircraft.weaponManager;
            if (wm == null)
                yield break;
            wm.RemoveWeapons();
            wm.SpawnWeapons();
            var method = typeof(AircraftSelectionMenu).GetMethod("AircraftSelectionMenu_OnChange",BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(menu, null);
        }
        internal static bool IsVanilla(AircraftDefinition def, string preset)
        {
            try
            {
                string path = PresetFilePath(def, Norm(preset));
                return File.Exists(path) && ParseJson(File.ReadAllText(path)).Vanilla;
            }
            catch { return false; }
        }
        internal static int DumpVanillaForAircraft(AircraftDefinition def, bool force)
        {
            if (def == null) return 0;
            bool verbose = LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true;
            string label = def.unitName ?? def.jsonKey ?? "unknown";
            var ap = def.aircraftParameters;
            if (ap == null || ap.StandardLoadouts == null || ap.StandardLoadouts.Length == 0)
            {
                if (verbose)
                    LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] {label}: no StandardLoadouts to dump.");
                return 0;
            }
            var acPrefab = def.unitPrefab != null ? def.unitPrefab.GetComponent<Aircraft>() : null;
            if (acPrefab == null) acPrefab = def.unitPrefab != null ? def.unitPrefab.GetComponentInChildren<Aircraft>(true) : null;
            var wm = acPrefab != null ? acPrefab.weaponManager : null;
            if (wm == null && acPrefab != null) wm = acPrefab.GetComponentInChildren<WeaponManager>(true);
            var sets = wm != null ? wm.hardpointSets : null;
            if (sets == null)
            {
                if (verbose)
                    LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] {label}: prefab has no Aircraft/WeaponManager; cannot dump {ap.StandardLoadouts.Length} loadout(s).");
                return 0;
            }
            int written = 0, skipped = 0;
            foreach (StandardLoadout sl in ap.StandardLoadouts)
            {
                if (sl == null || sl.loadout == null || sl.loadout.weapons == null) continue;
                string name = Norm(string.IsNullOrWhiteSpace(sl.Name) ? "Standard" : sl.Name);
                if (name == Plugin.DEFAULTPRESET) name = "Standard";
                string path = PresetFilePath(def, name);
                if (File.Exists(path))
                {
                    if (!ParseJson(File.ReadAllText(path)).Vanilla) { skipped++; continue; } 
                    if (!force) continue;                                                    
                }
                var hardpoints = new List<string>(sets.Length);
                var stations = new List<string>(sets.Length);
                for (int i = 0; i < sets.Length; i++)
                {
                    WeaponMount mount = i < sl.loadout.weapons.Count ? sl.loadout.weapons[i] : null;
                    string key = mount != null ? (JsonLoadoutInjector.PreferredKey(mount) ?? "") : "";
                    hardpoints.Add(key);
                    stations.Add(StationKey(sets, i) + "=" + key);
                }
                float fuel = sl.FuelRatio > 0f ? sl.FuelRatio
                           : (ap.DefaultFuelLevel > 0f ? ap.DefaultFuelLevel : 1f);
                Directory.CreateDirectory(AircraftDir(def));
                File.WriteAllText(path, BuildJson(fuel, "", hardpoints, stations, true));
                written++;
            }
            if (verbose && (written > 0 || skipped > 0))
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] {label}: {written} vanilla loadout(s) written, {skipped} skipped (player-owned).");
            return written;
        }
        internal static int DumpVanillaAILoadouts(bool force)
        {
            int written = 0;
            var list = Encyclopedia.i != null ? Encyclopedia.i.aircraft : null;
            if (list == null) return 0;
            foreach (AircraftDefinition def in list)
                written += DumpVanillaForAircraft(def, force);
            if (written > 0)
                LoadoutInjectorPlugin.ModLogger.LogInfo("[Presets] Dumped " + written + " vanilla AI loadout(s).");
            return written;
        }
        internal static void DeletePreset(AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            if (preset == Plugin.DEFAULTPRESET) return;
            if (IsVanilla(def, preset)) return; 
            string path = PresetFilePath(def, preset);
            if (File.Exists(path))
                File.Delete(path);
            if (string.Equals(GetActivePreset(def), preset, StringComparison.Ordinal))
                SetActivePreset(def, Plugin.DEFAULTPRESET);
            LoadoutInjectorPlugin.Instance.Config.Save();
        }
        internal static void RenamePreset(AircraftDefinition def, string oldPreset, string newPreset)
        {
            oldPreset = Norm(oldPreset);
            newPreset = Norm(newPreset);
            if (oldPreset == Plugin.DEFAULTPRESET || newPreset == Plugin.DEFAULTPRESET) return;
            if (IsVanilla(def, oldPreset)) return;
            if (string.Equals(oldPreset, newPreset, StringComparison.Ordinal)) return;
            string oldPath = PresetFilePath(def, oldPreset);
            if (!File.Exists(oldPath)) return;
            string newPath = PresetFilePath(def, newPreset);
            Directory.CreateDirectory(AircraftDir(def));
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(oldPath, newPath);
            if (string.Equals(GetActivePreset(def), oldPreset, StringComparison.Ordinal))
                SetActivePreset(def, newPreset);
            LoadoutInjectorPlugin.Instance.Config.Save();
        }
        internal static List<string> ListPresets(AircraftDefinition def)
        {
            var list = new List<string> { Plugin.DEFAULTPRESET };
            string dir = AircraftDir(def);
            if (Directory.Exists(dir))
            {
                var rest = Directory.GetFiles(dir, "*.preset")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => n != Plugin.DEFAULTPRESET)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                list.AddRange(rest);
            }
            return list;
        }
        internal static string GetHardpointKey(AircraftDefinition def, string preset, int index)
        {
            string path = PresetFilePath(def, Norm(preset));
            if (!File.Exists(path)) return "";
            var parsed = ParseJson(File.ReadAllText(path));
            return index < parsed.Hardpoints.Count ? parsed.Hardpoints[index] : "";
        }
        internal static string ResolveWeaponName(AircraftSelectionMenu menu, string jsonKey)
        {
            if (string.IsNullOrEmpty(jsonKey))
                return null;
            var aircraft = MenuRefs.PreviewAircraft(menu);
            if (aircraft?.weaponManager == null)
                return jsonKey;
            var sets = aircraft.weaponManager.hardpointSets;
            if (sets == null)
                return jsonKey;
            foreach (var set in sets)
            {
                if (set?.weaponOptions == null) continue;
                var match = set.weaponOptions
                    .FirstOrDefault(w => w != null && w.jsonKey == jsonKey);
                if (match != null)
                {
                    return match.mountName; 
                }
            }
            return jsonKey; 
        }
        internal static string BuildPresetTooltip(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            string path = PresetFilePath(def, preset);
            if (!File.Exists(path))
                return preset == Plugin.DEFAULTPRESET
                    ? "Current live loadout (auto-saved)"
                    : "No saved data";
            var parsed = ParseJson(File.ReadAllText(path));
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine($"Preset: {preset}");
            sb.AppendLine($"Fuel: {(int)(ResolveFuel(parsed, def) * 100f)}%");
            if (!string.IsNullOrEmpty(parsed.Livery))
                sb.AppendLine("Livery: custom");
            if (parsed.Vanilla)
                sb.AppendLine("Source: game default (editable, not deletable)");
            var counts = new Dictionary<string, int>();
            var keys = (parsed.Hardpoints != null && parsed.Hardpoints.Count > 0)
                ? parsed.Hardpoints
                : new List<string>(parsed.Stations.Values);
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key))
                    continue;
                if (!counts.ContainsKey(key))
                    counts[key] = 0;
                counts[key]++;
            }
            if (counts.Count == 0)
            {
                sb.AppendLine("Weapons: None");
            }
            else
            {
                sb.AppendLine("Weapons:");
                foreach (var kv in counts.OrderByDescending(k => k.Value))
                {
                    string displayName = ResolveWeaponName(menu, kv.Key);
                    sb.AppendLine($"  {displayName} x{kv.Value}");
                }
            }
            return sb.ToString();
        }
    }
    [HarmonyPatch(typeof(LoadoutSelector), "LoadDefaults")]
    internal static class Patch_LoadDefaults
    {
        static void Postfix(LoadoutSelector __instance)
        {
            if (!LoadoutInjectorPlugin.Cfg_Integration_LoadoutPresets_Enable.Value) return;
            var menu = __instance.GetComponentInParent<AircraftSelectionMenu>();
            if (menu == null) return;
            PresetMenuUI.Attach(menu);
            var def = menu.GetSelectedType();
            var active = PresetIO.GetActivePreset(def);
            PresetIO.LoadPreset(menu, def, active);
        }
    }
    [HarmonyPatch(typeof(LoadoutSelector), "UpdateWeapons")]
    class Patch_AutoSave_Default
    {
        static void Postfix(LoadoutSelector __instance)
        {
            if (!LoadoutInjectorPlugin.Cfg_Integration_LoadoutPresets_Enable.Value) return;
            var menu = __instance.GetComponentInParent<AircraftSelectionMenu>();
            if (menu == null) return;
            var def = menu.GetSelectedType();
            if (def == null) return;
            var selectors = MenuRefs.WeaponSelectors(__instance);
            if (selectors == null || selectors.Count == 0) return;
            if (!selectors.Any(s => s.GetValue() != null))
                return;
            PresetIO.SaveCurrentToPreset(menu, def, Plugin.DEFAULTPRESET);
            LoadoutInjectorPlugin.ModLogger.LogInfo("Auto-saved last used.");
        }
    }
    internal static class PresetMenuUI
    {
        internal static AircraftSelectionMenu Menu;
        internal static bool Dirty;
        private static bool _edit;
        private static string _selected = "";
        private static string _name = "";
        private static bool _confirmDelete;
        private static Rect _rect = new Rect(10, 10, 220, 200);
        private static float right_Padding = 5f;
        private static float _desiredH;
        private static string _cachedUnitName = "";
        private static List<string> _presets = new List<string>();
        private static string _currentTooltip = "";
        internal static void Attach(AircraftSelectionMenu menu)
        {
            Menu = menu;
            Dirty = true;
        }
        internal static void Draw()
        {
            AircraftSelectionMenu menu = Menu;
            if (menu == null || !menu.isActiveAndEnabled) return;
            AircraftDefinition def = menu.GetSelectedType();
            if (def == null) return;
            if (Dirty || _cachedUnitName != def.unitName)
            {
                _cachedUnitName = def.unitName;
                _presets = PresetIO.ListPresets(def);
                Dirty = false;
                string active = PresetIO.GetActivePreset(def);
                string wanted = !string.IsNullOrWhiteSpace(_selected) ? _selected : active;
                int idx = _presets.IndexOf(wanted);
                if (idx < 0) idx = 0;
                _selected = _presets.Count > 0 ? _presets[Mathf.Clamp(idx, 0, _presets.Count - 1)] : "";
                if (!_edit) _name = _selected;
            }
            _rect.height = _desiredH;
            _rect.y = Screen.height * 0.2f;
            _rect.width = _edit ? 420f : 180f;
            _rect.x = Screen.width - _rect.width - right_Padding;
            _rect = GUI.Window(2082, _rect, _ => Window(menu, def), "Loadout Presets");
            DrawTooltip();
        }
        private static void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_currentTooltip))
                return;
            GUI.depth = -1000;
            Vector2 mouse = Event.current.mousePosition;
            mouse = GUIUtility.GUIToScreenPoint(mouse);
            Vector2 size = GUI.skin.box.CalcSize(new GUIContent(_currentTooltip));
            Rect rect = new Rect(
                mouse.x - size.x - 15f,
                mouse.y + 15f,
                size.x + 10f,
                size.y + 6f
            );
            GUI.color = new Color(1f, 1f, 1f, 2f);
            GUI.Box(rect, _currentTooltip);
        }
        private static void Window(AircraftSelectionMenu menu, AircraftDefinition def)
        {
            _currentTooltip = GUI.tooltip;
            string active = PresetIO.GetActivePreset(def);
            string focus = string.IsNullOrWhiteSpace(_selected) ? active : _selected;
            GUILayout.BeginVertical();
            for (int i = 0; i < _presets.Count; i++)
            {
                string p = _presets[i];
                bool isFocus = string.Equals(p, focus, StringComparison.Ordinal);
                GUIStyle style = isFocus ? GUI.skin.label : GUI.skin.button;
                GUIContent loadoutContent = new GUIContent(p, PresetIO.BuildPresetTooltip(menu, def, p));
                if (GUILayout.Button(loadoutContent, style))
                {
                    PresetIO.LoadPreset(menu, def, p);
                    _selected = _name = focus = p;
                    _confirmDelete = false;
                }
            }
            GUILayout.Space(6);
            if (!_edit)
            {
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUIContent editContent = new GUIContent("Edit", "Edit, create, or delete loadout presets");
                if (GUILayout.Button(editContent, GUILayout.Width(70f)))
                {
                    _edit = true;
                    _selected = _name = active;
                    _confirmDelete = false;
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Name:", GUILayout.ExpandWidth(false));
                var prevName = _name;
                _name = GUILayout.TextField(_name ?? "", 32, GUILayout.ExpandWidth(true));
                if (_confirmDelete && prevName != _name) _confirmDelete = false;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Add"))
                {
                    string name = (_name ?? "").Trim();
                    if (name.Length != 0 && name != Plugin.DEFAULTPRESET)
                    {
                        PresetIO.SetActivePreset(def, name);
                        PresetIO.SaveCurrentToPreset(menu, def, name);
                        _selected = _name = name;
                        _confirmDelete = false;
                        Dirty = true;
                        LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Added {def.unitName} / {name}");
                    }
                }
                bool protectedPreset = _selected == Plugin.DEFAULTPRESET || PresetIO.IsVanilla(def, _selected);
                GUI.enabled = !string.IsNullOrWhiteSpace(_selected);
                if (GUILayout.Button("Overwrite"))
                {
                    string target = (_selected ?? "").Trim();
                    if (target.Length != 0)
                    {
                        PresetIO.SaveCurrentToPreset(menu, def, target);
                        _confirmDelete = false;
                        LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Overwrote {def.unitName}:{target}");
                    }
                }
                GUI.enabled = !string.IsNullOrWhiteSpace(_selected) && !protectedPreset;
                if (GUILayout.Button("Rename"))
                {
                    string current = (_selected ?? "").Trim();
                    string target = (_name ?? "").Trim();
                    if (current.Length != 0 && target.Length != 0 && target != Plugin.DEFAULTPRESET
                        && !string.Equals(current, target, StringComparison.Ordinal))
                    {
                        PresetIO.RenamePreset(def, current, target);
                        _selected = _name = target;
                        _confirmDelete = false;
                        Dirty = true;
                        LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Renamed {def.unitName}:{current} -> {target}");
                    }
                }
                if (GUILayout.Button(_confirmDelete ? "Confirm" : "Delete"))
                {
                    if (!_confirmDelete) _confirmDelete = true;
                    else
                    {
                        PresetIO.DeletePreset(def, _selected);
                        LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Deleted {def.unitName} / {_selected}");
                        _selected = "";
                        _name = "";
                        _confirmDelete = false;
                        Dirty = true;
                    }
                }
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Dump Vanilla",
                        "Writes every aircraft authored AI loadouts here so they can be edited. AI draws from these too."),
                    GUILayout.Width(110f)))
                {
                    int n = PresetIO.DumpVanillaAILoadouts(true);
                    JsonLoadoutInjector.ClearCache();
                    Dirty = true;
                    LoadoutInjectorPlugin.ModLogger.LogInfo("[Presets] Dump Vanilla wrote " + n + " loadout(s).");
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Done", GUILayout.Width(70f)))
                {
                    _edit = false;
                    _confirmDelete = false;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint)
            {
                _desiredH = Mathf.Max(80, GUILayoutUtility.GetLastRect().yMax + 22);
                _currentTooltip = GUI.tooltip;
            }
        }
    }
}