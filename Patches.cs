using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace MoreSFPModules
{
    // =========================================================================
    // Patch: MainGameManager.Awake (Postfix)
    // Earliest point where sfpPrefabs is populated — before OnLoad() restores
    // save data. Populates the registry and extends sfpPrefabs.
    // =========================================================================
    [HarmonyPatch(typeof(MainGameManager), nameof(MainGameManager.Awake))]
    internal static class PatchMainGameManagerAwake
    {
        private static void Postfix(MainGameManager __instance)
        {
            MelonLogger.Msg("[More SFP] MainGameManager.Awake → setting up registry.");
            Core.SetupRegistry(__instance);
        }
    }

    // =========================================================================
    // Patch: MainGameManager.Start (Postfix)
    // Safety net — re-runs SetupRegistry if Start() reset sfpPrefabs back to
    // its vanilla size (which would orphan our custom indices).
    // =========================================================================
    [HarmonyPatch(typeof(MainGameManager), nameof(MainGameManager.Start))]
    internal static class PatchMainGameManagerStart
    {
        private static void Postfix(MainGameManager __instance)
        {
            var arr = __instance.sfpPrefabs;
            int len = arr?.Length ?? 0;

            if (len > 0 && !ModuleRegistry.Entries.ContainsKey(len - 1))
            {
                MelonLogger.Warning("[More SFP] sfpPrefabs was RESET — re-extending in Start.");
                Core.SetupRegistry(__instance);
            }
        }
    }

    // =========================================================================
    // Patch: MainGameManager.GetSfpPrefab (Prefix)
    // Intercepts requests for our custom prefabIDs and returns a freshly cloned
    // module instead of falling through to the vanilla array lookup.
    // Building fresh every call avoids Il2Cpp GC invalidation of cached pointers.
    // =========================================================================
    [HarmonyPatch(typeof(MainGameManager), nameof(MainGameManager.GetSfpPrefab))]
    internal static class PatchGetSfpPrefab
    {
        private static bool Prefix(MainGameManager __instance, int prefabID, ref GameObject __result)
        {
            if (!ModuleRegistry.TryGet(prefabID, out var entry)) return true;

            __result = Core.BuildModulePrefab(__instance, prefabID, entry);
            return false;
        }
    }

    // =========================================================================
    // Patch: MainGameManager.GetSfpBoxPrefab (Prefix)
    // Intercepts requests for our custom box prefabIDs and returns a freshly
    // cloned box with the correct sfpBoxType and child module speed/ID.
    // =========================================================================
    [HarmonyPatch(typeof(MainGameManager), nameof(MainGameManager.GetSfpBoxPrefab))]
    internal static class PatchGetSfpBoxPrefab
    {
        private static bool Prefix(MainGameManager __instance, int prefabID, ref GameObject __result)
        {
            if (!ModuleRegistry.TryGet(prefabID, out var entry)) return true;

            __result = Core.BuildBoxPrefab(__instance, prefabID, entry);
            return false;
        }
    }

    // =========================================================================
    // Patch: ComputerShop.GetPrefabForItem (Prefix)
    // Routes our custom itemID to the correct prefab when the player buys from
    // the shop. Handles both SFPBox (type 9) and bare SFPModule (type 8).
    // =========================================================================
    [HarmonyPatch(typeof(ComputerShop), nameof(ComputerShop.GetPrefabForItem))]
    internal static class PatchGetPrefabForItem
    {
        private static bool Prefix(int itemID, PlayerManager.ObjectInHand itemType, ref GameObject __result)
        {
            if (!ModuleRegistry.TryGet(itemID, out var entry)) return true;

            var mgm = MainGameManager.instance;
            if (mgm == null) return true;

            // ObjectInHand.SFPBox == 9, ObjectInHand.SFPModule == 8
            if ((int)itemType == 9)
            {
                __result = Core.BuildBoxPrefab(mgm, itemID, entry);
                return false;
            }
            if ((int)itemType == 8)
            {
                __result = Core.BuildModulePrefab(mgm, itemID, entry);
                return false;
            }

            return true;
        }
    }

    // =========================================================================
    // Patch: SFPBox.LoadSFPsFromSave (Prefix)
    // The load code accesses sfpPrefabs[prefabID] directly — it does NOT call
    // GetSfpPrefab(). Il2Cpp's GC can null our cached template between Awake
    // and the actual load. This prefix rebuilds fresh templates at all custom
    // indices immediately before the load code reads the array.
    // =========================================================================
    [HarmonyPatch(typeof(SFPBox), nameof(SFPBox.LoadSFPsFromSave))]
    internal static class PatchLoadSFPsFromSave
    {
        private static void Prefix()
        {
            var mgm = MainGameManager.instance;
            if (mgm == null) return;

            var arr = mgm.sfpPrefabs;
            if (arr == null) return;

            foreach (var (prefabID, entry) in ModuleRegistry.Entries)
            {
                if (prefabID < 0 || prefabID >= arr.Length) continue;

                var template = Core.BuildModulePrefab(mgm, prefabID, entry);
                if (template != null)
                {
                    template.name = $"SFPModule_template_{prefabID}";
                    Object.DontDestroyOnLoad(template);
                }
                arr[prefabID] = template;
            }
        }
    }

    // =========================================================================
    // Patch: SFPBox.CanAcceptSFP (Prefix)
    // Our custom box uses sfpBoxType == prefabID (e.g. 100), but our modules
    // carry sfpType == vanilla QSFP+ type for port compatibility. Without this
    // patch the box would reject our module because the types don't match.
    // =========================================================================
    [HarmonyPatch(typeof(SFPBox), nameof(SFPBox.CanAcceptSFP))]
    internal static class PatchCanAcceptSFP
    {
        private static bool Prefix(SFPBox __instance, int sfpType, ref bool __result)
        {
            int boxType = __instance.sfpBoxType;
            if (!ModuleRegistry.TryGet(boxType, out var entry)) return true;

            __result = (sfpType == entry.ModuleSfpType);
            return false;
        }
    }
}
