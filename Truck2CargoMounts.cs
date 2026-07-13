using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LoadoutInjector
{
    [HarmonyPatch(typeof(Encyclopedia), "AfterLoad", new Type[] { })]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter("com.offiry.qol")]
    public static class Truck2CargoMounts_Patch
    {
        private struct VariantInfo
        {
            public string Suffix;
            public string Vehicle;
            public string DisplayName;
            public string ShortName;
            public float Mass;
            public float Cost;
            public string Description;
        }

        private static readonly VariantInfo[] Variants = new[]
        {
            new VariantInfo { Suffix = "LADS", Vehicle = "Truck2-LADS", DisplayName = "MSV LADS", ShortName = "MSV LADS", Mass = 15000, Cost = 18.5f,
                Description = "Support vehicle equipped with a powerful laser for point-defense against shells and missiles." },
            new VariantInfo { Suffix = "CRAM", Vehicle = "Truck2-CRAM", DisplayName = "MSV CRAM", ShortName = "MSV CRAM", Mass = 15000, Cost = 10.5f,
                Description = "Support vehicle equipped with a 20mm rotary autocannon for point-defense against shells and missiles." },
            new VariantInfo { Suffix = "RSAM", Vehicle = "Truck2-RSAM", DisplayName = "MSV R9 Launcher", ShortName = "MSV R9", Mass = 18000, Cost = 12.5f,
                Description = "Long-range air-defense vehicle capable of engaging targets designated by nearby radar trucks." },
            new VariantInfo { Suffix = "FC", Vehicle = "Truck2-FC", DisplayName = "MSV Fire Control", ShortName = "MSV FC", Mass = 20000, Cost = 2.30f,
                Description = "Support vehicle capable of providing advanced fire control information to nearby surface-to-air missile batteries." },
            new VariantInfo { Suffix = "FT", Vehicle = "Truck2-FT", DisplayName = "MSV Fuel Tanker", ShortName = "MSV FUEL", Mass = 20000, Cost = 0.45f,
                Description = "Support vehicle capable of refueling air units within 300m." },
            new VariantInfo { Suffix = "M", Vehicle = "Truck2-M", DisplayName = "MSV Munitions Truck", ShortName = "MSV MUNITIONS", Mass = 20000, Cost = 0.45f,
                Description = "Support vehicle capable of rearming air and ground units within 300m." },
            new VariantInfo { Suffix = "R", Vehicle = "Truck2-R", DisplayName = "MSV Radar Truck", ShortName = "MSV RADAR", Mass = 20000, Cost = 2.30f,
                Description = "Support vehicle providing radar detection and target designation for nearby units." },
            new VariantInfo { Suffix = "TBM", Vehicle = "Truck2-TBM", DisplayName = "MSV TBM-3C Launcher", ShortName = "MSV TBM-3C", Mass = 20000, Cost = 12.5f,
                Description = "Mobile ballistic missile launcher capable of striking distant ground targets." },
            new VariantInfo { Suffix = "TBM-N", Vehicle = "Truck2-TBM-N", DisplayName = "MSV TBM-3N Launcher", ShortName = "MSV TBM-3N", Mass = 20000, Cost = 12.5f,
                Description = "Mobile nuclear ballistic missile launcher capable of striking distant ground targets." },
            new VariantInfo { Suffix = "ASHM1", Vehicle = "Truck2-ASHM1", DisplayName = "MSV AShM-300 Launcher", ShortName = "MSV ASHM-300", Mass = 18000, Cost = 12.5f,
                Description = "Mobile anti-ship missile launcher capable of engaging naval targets at long range." },
            new VariantInfo { Suffix = "ASHM3", Vehicle = "Truck2-ASHM3", DisplayName = "MSV AGM-200B Launcher", ShortName = "MSV AGM-200B", Mass = 18000, Cost = 12.5f,
                Description = "Mobile anti-ship missile launcher capable of engaging naval targets at extended range." },
        };

        private static readonly HashSet<string> _created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [HarmonyPrefix]
        public static void Prefix(Encyclopedia __instance)
        {
            try { AddMissingMounts(__instance); }
            catch (Exception ex) { LoadoutInjectorPlugin.ModLogger.LogError("[LoadoutInjector] Truck2CargoMounts failed: " + ex); }
        }

        private static void AddMissingMounts(Encyclopedia __instance)
        {
            var qolAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "qol");
            if (qolAssembly == null) return;
            var qolPlugin = qolAssembly.GetType("QOLPlugin");
            if (qolPlugin == null) return;

            var duplicatePrefab = AccessTools.Method(qolPlugin, "DuplicatePrefab", new[] { typeof(string), typeof(string) });
            var duplicateWeaponInfo = AccessTools.Method(qolPlugin, "DuplicateWeaponInfo", new[] { typeof(string), typeof(string), typeof(GameObject) });
            var duplicateWeaponMount = AccessTools.Method(qolPlugin, "DuplicateWeaponMount");
            var addMountToEncyclopedia = AccessTools.Method(qolPlugin, "AddMountToEncyclopedia");

            var resourceCacheType = qolAssembly.GetType("qol.Utilities.ResourceCache");
            var byNameGeneric = resourceCacheType?.GetMethod("ByName", new[] { typeof(string), typeof(StringComparison) });
            var byNameVehicleDef = byNameGeneric?.MakeGenericMethod(typeof(VehicleDefinition));

            if (duplicatePrefab == null || duplicateWeaponInfo == null || duplicateWeaponMount == null
                || addMountToEncyclopedia == null || byNameVehicleDef == null)
            {
                LoadoutInjectorPlugin.ModLogger.LogWarning("[LoadoutInjector] Truck2CargoMounts: could not resolve qol helper methods, skipping.");
                return;
            }

            foreach (var variant in Variants)
            {
                string mountName = $"Truck2-{variant.Suffix}x1";

                WeaponMount mount = __instance.weaponMounts.FirstOrDefault(m => m.name == mountName);
                if (mount == null)
                {
                    if (_created.Contains(mountName)) continue; 

                    var vehicleDef = (VehicleDefinition)byNameVehicleDef.Invoke(null, new object[] { variant.Vehicle, StringComparison.InvariantCultureIgnoreCase });
                    if (vehicleDef == null) continue; 

                    var prefab = (GameObject)duplicatePrefab.Invoke(null, new object[] { "HLT-Rx1", mountName });
                    var info = (WeaponInfo)duplicateWeaponInfo.Invoke(null, new object[] { "HLT-R_info", $"Truck2-{variant.Suffix}_info", null });
                    mount = (WeaponMount)duplicateWeaponMount.Invoke(null, new object[] { "HLT-Rx1", mountName, prefab, info, vehicleDef });
                    addMountToEncyclopedia.Invoke(null, new object[] { __instance, mountName, mount });

                    info.weaponName = variant.DisplayName;
                    info.shortName = variant.ShortName;
                    info.massPerRound = variant.Mass;
                    info.costPerRound = variant.Cost;
                    info.description = variant.Description;
                    mount.mountName = variant.DisplayName;
                    mount.ammo = 1;

                    _created.Add(mountName);
                    LoadoutInjectorPlugin.ModLogger.LogInfo($"[LoadoutInjector] Truck2CargoMounts: added {mountName}");
                }

            }
        }

    }
}
