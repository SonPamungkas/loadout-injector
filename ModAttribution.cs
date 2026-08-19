using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
namespace LoadoutInjector
{
    internal static class ModAttribution
    {
        internal const string Vanilla = "vanilla";
        private const string LoaderGuid = "com.nikkorap.blueprinter";
        private const string Unknown = "unknown";
        private static HashSet<int> _vanillaMountIds;
        private static Dictionary<string, int> _vanillaStationCounts;
        private static string _codeModLabel;
        private static bool _baselineTaken;
        private static Dictionary<string, string> _bundleOwners;
        internal static void CaptureBaseline(Encyclopedia instance)
        {
            if (_baselineTaken) return;
            _baselineTaken = true;
            _vanillaMountIds = new HashSet<int>();
            foreach (var m in Resources.FindObjectsOfTypeAll<WeaponMount>())
            {
                if (m != null) _vanillaMountIds.Add(m.GetInstanceID());
            }
            _vanillaStationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (instance?.aircraft != null)
            {
                foreach (var def in instance.aircraft)
                {
                    if (def?.unitPrefab == null) continue;
                    var wm = def.unitPrefab.GetComponentInChildren<WeaponManager>(true);
                    if (wm?.hardpointSets == null) continue;
                    _vanillaStationCounts[AircraftKey(def)] = wm.hardpointSets.Length;
                }
            }
            if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
            {
                LoadoutInjectorPlugin.ModLogger.LogInfo(
                    $"[Attribution] Vanilla baseline: {_vanillaMountIds.Count} mounts, {_vanillaStationCounts.Count} aircraft.");
            }
        }
        private static string AircraftKey(UnitDefinition def)
        {
            if (def == null) return "unknown";
            if (!string.IsNullOrEmpty(def.jsonKey)) return def.jsonKey;
            return string.IsNullOrEmpty(def.unitName) ? "unknown" : def.unitName;
        }
        private static Dictionary<string, string> BundleOwners()
        {
            if (_bundleOwners != null) return _bundleOwners;
            if (Encyclopedia.i == null) return null;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    if (ab == null || ab.isStreamedSceneAssetBundle) continue;
                    string owner = OwnerLabel(ab.name);
                    foreach (string assetPath in ab.GetAllAssetNames())
                    {
                        string stem = Path.GetFileNameWithoutExtension(assetPath);
                        if (!string.IsNullOrEmpty(stem)) map[stem] = owner;
                    }
                }
            }
            catch (Exception ex)
            {
                if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                    LoadoutInjectorPlugin.ModLogger.LogWarning("[Attribution] Bundle scan deferred: " + ex.Message);
                return null;
            }
            if (map.Count == 0) return null; 
            _bundleOwners = map;
            if (LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true)
                LoadoutInjectorPlugin.ModLogger.LogInfo($"[Attribution] Indexed {map.Count} assets from loaded asset bundles.");
            return _bundleOwners;
        }
        private static string OwnerLabel(string bundleName)
        {
            if (string.IsNullOrEmpty(bundleName)) return "unnamed bundle";
            string guid = GuidForBundleName(bundleName);
            return guid == null ? bundleName : guid + " (" + bundleName + ")";
        }
        private static string GuidForBundleName(string bundleName)
        {
            if (Chainloader.PluginInfos == null) return null;
            string token = Simplify(bundleName);
            if (token.Length < 4) return null;
            var matches = new List<string>();
            foreach (var pi in Chainloader.PluginInfos.Values)
            {
                if (pi?.Metadata == null) continue;
                string dll = string.IsNullOrEmpty(pi.Location) ? "" : Simplify(Path.GetFileNameWithoutExtension(pi.Location));
                string display = Simplify(pi.Metadata.Name);
                bool hit = (dll.Length >= 4 && (dll.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(dll, StringComparison.Ordinal)))
                        || (display.Length >= 4 && (display.StartsWith(token, StringComparison.Ordinal) || token.StartsWith(display, StringComparison.Ordinal)));
                if (hit && !matches.Contains(pi.Metadata.GUID)) matches.Add(pi.Metadata.GUID);
            }
            return matches.Count == 1 ? matches[0] : null;
        }
        private static string Simplify(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'a' && c <= 'z') sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
        private static string CodeModLabel()
        {
            if (_codeModLabel != null) return _codeModLabel;
            var owners = new List<string>();
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Encyclopedia)).Where(m => m.Name == "AfterLoad"))
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                foreach (var p in (info.Prefixes ?? Enumerable.Empty<Patch>()).Concat(info.Postfixes ?? Enumerable.Empty<Patch>()))
                {
                    if (p.owner == LoadoutInjectorPlugin.PluginGuid || p.owner == LoaderGuid) continue;
                    if (!owners.Contains(p.owner)) owners.Add(p.owner);
                }
            }
            if (owners.Count == 0)
                _codeModLabel = "modded(code)";
            else if (owners.Count == 1)
                _codeModLabel = Describe(owners[0]);
            else
                _codeModLabel = "modded(code; candidates: " + string.Join("|", owners.Select(Describe).ToArray()) + ")";
            return _codeModLabel;
        }
        private static string Describe(string guid)
        {
            if (Chainloader.PluginInfos != null
                && Chainloader.PluginInfos.TryGetValue(guid, out var pi)
                && pi?.Metadata != null)
            {
                return guid + " (" + pi.Metadata.Name + ")";
            }
            return guid;
        }
        internal static string OwnerOf(WeaponMount mount)
        {
            if (mount == null) return Unknown;
            var bundles = BundleOwners();
            if (bundles != null && !string.IsNullOrEmpty(mount.name)
                && bundles.TryGetValue(mount.name, out var bundleOwner))
                return bundleOwner;
            if (_vanillaMountIds != null && _vanillaMountIds.Contains(mount.GetInstanceID()))
                return Vanilla;
            if (_vanillaMountIds == null)
                return Vanilla; 
            return CodeModLabel();
        }
        internal static string OwnerOfStation(Aircraft aircraft, int stationIndex, HardpointSet hs)
        {
            string aircraftOwner = OwnerOfAircraft(aircraft);
            if (aircraftOwner != null) return aircraftOwner;
            if (hs != null && HasModCreatedHardpoint(hs)) return CodeModLabel();
            string key = AircraftKey(aircraft?.definition);
            if (_vanillaStationCounts != null
                && _vanillaStationCounts.TryGetValue(key, out int vanillaCount)
                && stationIndex >= vanillaCount)
            {
                return CodeModLabel();
            }
            return Vanilla;
        }
        private static string OwnerOfAircraft(Aircraft aircraft)
        {
            var bundles = BundleOwners();
            if (bundles == null || aircraft == null) return null;
            var def = aircraft.definition;
            if (def != null)
            {
                if (!string.IsNullOrEmpty(def.name) && bundles.TryGetValue(def.name, out var byDef)) return byDef;
                if (def.unitPrefab != null && bundles.TryGetValue(def.unitPrefab.name, out var byPrefab)) return byPrefab;
            }
            string raw = aircraft.gameObject.name;
            if (raw.EndsWith("(Clone)", StringComparison.Ordinal)) raw = raw.Substring(0, raw.Length - 7);
            return bundles.TryGetValue(raw, out var byGo) ? byGo : null;
        }
        internal static bool HasModCreatedHardpoint(HardpointSet hs)
        {
            if (hs?.hardpoints == null) return false;
            foreach (var hp in hs.hardpoints)
            {
                var name = hp?.transform == null ? null : hp.transform.name;
                if (IsGuidHardpointName(name)) return true;
            }
            return false;
        }
        private static bool IsGuidHardpointName(string name)
        {
            const string prefix = "Hardpoint_";
            if (name == null || name.Length != prefix.Length + 8) return false;
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            for (int i = prefix.Length; i < name.Length; i++)
            {
                char c = name[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}