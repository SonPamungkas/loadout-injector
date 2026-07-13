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
    }

    internal static class PresetIO
    {
        private const string ActiveKey = "ActivePreset";

        private static string Norm(string preset)
        {
            preset = Regex.Replace((preset ?? "").Trim(), @"[=\r\n\t\\\""'\[\]]", "_");
            return preset.Length == 0 ? Plugin.DEFAULTPRESET : preset;
        }

        private static string SanitizeFileName(string name)
        {
            name = name ?? "";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        internal static string RootDir => Path.Combine(Paths.PluginPath, "loadout-preset");

        internal static string AircraftDir(AircraftDefinition def) =>
            Path.Combine(RootDir, SanitizeFileName(def.jsonKey ?? def.unitName ?? "unknown"));

        internal static string PresetFilePath(AircraftDefinition def, string preset) =>
            Path.Combine(AircraftDir(def), SanitizeFileName(Norm(preset)) + ".preset");

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

        private static string BuildJson(float fuel, string livery, List<string> hardpoints)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"Fuel\":").Append(fuel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"Livery\":\"").Append(EscapeJson(livery)).Append("\"");
            sb.Append(",\"Hardpoints\":[");
            for (int i = 0; i < hardpoints.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(hardpoints[i])).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private struct ParsedPreset
        {
            public float Fuel;
            public string Livery;
            public List<string> Hardpoints;
        }

        private static ParsedPreset ParseJson(string json)
        {
            var result = new ParsedPreset { Fuel = 1f, Livery = "", Hardpoints = new List<string>() };
            if (string.IsNullOrEmpty(json)) return result;

            try
            {
                var fuelMatch = Regex.Match(json, "\"Fuel\"\\s*:\\s*([0-9.eE+-]+)");
                if (fuelMatch.Success)
                    result.Fuel = float.Parse(fuelMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

                var liveryMatch = Regex.Match(json, "\"Livery\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (liveryMatch.Success)
                    result.Livery = UnescapeJson(liveryMatch.Groups[1].Value);

                var hpMatch = Regex.Match(json, "\"Hardpoints\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                if (hpMatch.Success)
                {
                    foreach (Match m in Regex.Matches(hpMatch.Groups[1].Value, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                        result.Hardpoints.Add(UnescapeJson(m.Groups[1].Value));
                }
            }
            catch (Exception ex)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[Presets] Failed to parse preset file: " + ex.Message);
            }
            return result;
        }

        internal static void SaveCurrentToPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            if (string.IsNullOrWhiteSpace(preset)) return;

            var loadoutSelector = MenuRefs.LoadoutSelectorRef(menu);
            var weaponSelectors = MenuRefs.WeaponSelectors(loadoutSelector);
            var preview = MenuRefs.PreviewAircraft(menu);

            var hardpoints = new List<string>(weaponSelectors.Count);
            for (int i = 0; i < weaponSelectors.Count; i++)
            {
                WeaponMount mount = weaponSelectors[i].GetValue();
                hardpoints.Add(mount?.jsonKey ?? "");
            }

            float fuel = preview != null ? preview.fuelLevel : 1f;
            string livery = preview != null ? SerializeLiveryKey(preview.NetworkLiveryKey) : "";

            string dir = AircraftDir(def);
            Directory.CreateDirectory(dir);
            File.WriteAllText(PresetFilePath(def, preset), BuildJson(fuel, livery, hardpoints));
        }

        internal static bool LoadPreset(AircraftSelectionMenu menu, AircraftDefinition def, string preset)
        {
            preset = Norm(preset);

            bool applied = ApplyPreset(menu, def, preset);
            SetActivePreset(def, preset);

            LoadoutInjectorPlugin.Instance.Config.Save();
            LoadoutInjectorPlugin.ModLogger.LogInfo($"[Presets] Loaded {preset}:{def.unitName} {(applied ? "" : "(no saved preset yet)")} ");

            return applied;
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

            for (int i = 0; i < n; i++)
            {
                string key = i < parsed.Hardpoints.Count ? parsed.Hardpoints[i] : "";

                weaponSelectors[i].SetValue(
                    string.IsNullOrEmpty(key)
                        ? null
                        : sets[i].weaponOptions.Find(w => w != null && w.jsonKey == key)
                );
            }

            preview.fuelLevel = parsed.Fuel;

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

        internal static void DeletePreset(AircraftDefinition def, string preset)
        {
            preset = Norm(preset);
            if (preset == Plugin.DEFAULTPRESET)
                return;

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
            sb.AppendLine($"Fuel: {(int)(parsed.Fuel * 100f)}%");

            if (!string.IsNullOrEmpty(parsed.Livery))
                sb.AppendLine("Livery: custom");

            var counts = new Dictionary<string, int>();
            foreach (string key in parsed.Hardpoints)
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

                GUI.enabled = !string.IsNullOrWhiteSpace(_selected) && _selected != Plugin.DEFAULTPRESET;

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