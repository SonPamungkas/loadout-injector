using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using BepInEx.Logging;

namespace LoadoutInjector
{
    internal static class InjectionPipeline
    {
        public class PatchState
        {
            public HardpointSet Set;
            public List<WeaponMount> OriginalOptions;
        }

        private class HsCacheInfo
        {
            public bool IsResolved;
            public List<WeaponMount> CustomMounts;
        }

        private static readonly ConditionalWeakTable<HardpointSet, HsCacheInfo> _hsCache = new ConditionalWeakTable<HardpointSet, HsCacheInfo>();
        private static readonly ConditionalWeakTable<Aircraft, string> _acNameCache = new ConditionalWeakTable<Aircraft, string>();

        private static string GetCachedAircraftName(Aircraft aircraft)
        {
            if (_acNameCache.TryGetValue(aircraft, out string cachedName))
                return cachedName;

            string acName = JsonLoadoutInjector.GetAircraftName(aircraft);
            _acNameCache.Add(aircraft, acName);
            return acName;
        }

        [HarmonyPatch(typeof(WeaponChecker), "GetAvailableWeaponsNonAlloc")]
        public static class GetAvailableWeaponsNonAlloc_Injection
        {
            static void Prefix(object[] __args, out PatchState __state)
            {
                __state = null;

                bool verbose = LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true;

                HardpointSet hardpointSet = null;
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        if (__args[i] is HardpointSet hs)
                        {
                            hardpointSet = hs;
                            break;
                        }
                    }
                }

                if (hardpointSet == null) return;

                if (!_hsCache.TryGetValue(hardpointSet, out HsCacheInfo info))
                {
                    info = new HsCacheInfo { IsResolved = true, CustomMounts = null };

                    if (hardpointSet.hardpoints != null && hardpointSet.hardpoints.Count > 0 && hardpointSet.hardpoints[0].part != null)
                    {
                        var aircraft = hardpointSet.hardpoints[0].part.gameObject.GetComponentInParent<Aircraft>();
                        if (aircraft != null)
                        {
                            string acName = GetCachedAircraftName(aircraft);
                            int stationIndex = -1;

                            if (aircraft.weaponManager != null && aircraft.weaponManager.hardpointSets != null)
                            {
                                for (int i = 0; i < aircraft.weaponManager.hardpointSets.Length; i++)
                                {
                                    if (aircraft.weaponManager.hardpointSets[i] == hardpointSet)
                                    {
                                        stationIndex = i;
                                        break;
                                    }
                                }
                            }

                            if (stationIndex != -1)
                            {
                                var dict = JsonLoadoutInjector.GetLoadout(acName, aircraft);
                                if (dict != null && dict.TryGetValue(stationIndex, out var customMounts))
                                {
                                    if (customMounts != null && customMounts.Count > 0)
                                    {
                                        info.CustomMounts = customMounts;
                                        if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo($"[InjectionPipeline] GetAvailableWeaponsNonAlloc: Discovered {customMounts.Count} custom mounts for station {stationIndex}. Caching result.");
                                    }
                                }
                            }
                        }
                    }
                    _hsCache.Add(hardpointSet, info);
                }

                if (info.CustomMounts != null && info.CustomMounts.Count > 0)
                {
                    List<WeaponMount> original = hardpointSet.weaponOptions;
                    if (original == null) original = new List<WeaponMount>();

                    List<WeaponMount> expanded = new List<WeaponMount>(original);
                    HashSet<int> seen = new HashSet<int>(original.Where(x => x != null).Select(x => x.GetInstanceID()));

                    foreach (WeaponMount wm in info.CustomMounts)
                    {
                        if (wm != null && seen.Add(wm.GetInstanceID()))
                            expanded.Add(wm);
                    }

                    __state = new PatchState { Set = hardpointSet, OriginalOptions = original };
                    hardpointSet.weaponOptions = expanded;
                }
            }

            static void Postfix(PatchState __state)
            {
                if (__state != null && __state.Set != null && __state.OriginalOptions != null)
                {
                    __state.Set.weaponOptions = __state.OriginalOptions;
                }
            }
        }

        [HarmonyPatch(typeof(WeaponChecker), "VetLoadout")]
        public static class VetLoadout_Injection
        {
            static void Prefix(AircraftDefinition definition, NuclearOption.Networking.Player player, out List<PatchState> __state)
            {
                bool verbose = LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true;
                if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo("[InjectionPipeline] VetLoadout Prefix(): Triggered.");
                __state = null;

                if (definition == null || definition.unitPrefab == null)
                {
                    if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo("[InjectionPipeline] VetLoadout Prefix(): definition or unitPrefab is null. Returning.");
                    return;
                }

                Aircraft aircraft = definition.unitPrefab.GetComponent<Aircraft>();
                if (aircraft == null || aircraft.weaponManager == null || aircraft.weaponManager.hardpointSets == null)
                {
                    if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo("[InjectionPipeline] VetLoadout Prefix(): Aircraft or weaponManager is null. Returning.");
                    return;
                }

                string acName = GetCachedAircraftName(aircraft);

                if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo($"[InjectionPipeline] VetLoadout Prefix(): acName={acName}. Creating PatchState list.");

                var hardpointSets = aircraft.weaponManager.hardpointSets;
                bool stateInitialized = false;

                var stations = JsonLoadoutInjector.GetLoadout(acName, aircraft);
                if (stations != null && stations.Count > 0)
                {
                    for (int i = 0; i < hardpointSets.Length; i++)
                    {
                        HardpointSet hs = hardpointSets[i];
                        if (hs == null) continue;

                        if (stations.TryGetValue(i, out var customMounts))
                        {
                            if (customMounts != null && customMounts.Count > 0)
                            {
                                if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo($"[InjectionPipeline] VetLoadout Prefix(): Found {customMounts.Count} custom mounts for station {i}. Injecting...");
                                List<WeaponMount> original = hs.weaponOptions;
                                if (original == null) original = new List<WeaponMount>();

                                List<WeaponMount> expanded = new List<WeaponMount>(original);
                                HashSet<int> seen = new HashSet<int>(original.Where(x => x != null).Select(x => x.GetInstanceID()));

                                foreach (WeaponMount wm in customMounts)
                                {
                                    if (wm != null && seen.Add(wm.GetInstanceID()))
                                        expanded.Add(wm);
                                }

                                if (!stateInitialized)
                                {
                                    __state = new List<PatchState>();
                                    stateInitialized = true;
                                }

                                __state.Add(new PatchState { Set = hs, OriginalOptions = original });
                                hs.weaponOptions = expanded;
                                if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo($"[InjectionPipeline] VetLoadout Prefix(): Station {i} injection complete.");
                            }
                        }
                    }
                }
            }

            static void Postfix(List<PatchState> __state)
            {
                bool verbose = LoadoutInjectorPlugin.Cfg_DebugLogging?.Value == true;
                if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo("[InjectionPipeline] VetLoadout Postfix(): Triggered.");
                if (__state != null)
                {
                    foreach (PatchState state in __state)
                    {
                        if (state != null && state.Set != null && state.OriginalOptions != null)
                        {
                            state.Set.weaponOptions = state.OriginalOptions;
                        }
                    }
                    if (verbose) LoadoutInjectorPlugin.ModLogger.LogInfo($"[InjectionPipeline] VetLoadout Postfix(): Restored {__state.Count} stations.");
                }
            }
        }
    }
}
