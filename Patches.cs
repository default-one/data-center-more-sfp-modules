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

                if (arr[prefabID] == null)
                {
                    var template = Core.BuildModulePrefab(mgm, prefabID, entry,
                                                          Core.TemplateHolder?.transform);
                    if (template != null)
                        template.name = $"SFPModule_template_{prefabID}";
                    arr[prefabID] = template;
                }
            }
        }
    }

    // =========================================================================
    // Patch: CableLink.InsertSFP (Prefix)
    // Child modules taken from a custom box retain the vanilla QSFP+ prefabID
    // (3) because setting prefabID on active child GameObjects causes the world
    // tracker to spawn infinite loose modules. Instead we fix it here — at the
    // exact moment the module is inserted into a port — so the save stores the
    // correct custom prefabID and load can restore the right module.
    // =========================================================================
    [HarmonyPatch(typeof(CableLink), nameof(CableLink.InsertSFP))]
    internal static class PatchCableLinkInsertSFP
    {
        private static void Prefix(float speed, SFPModule module)
        {
            var usableObj = module?.GetComponent<UsableObject>();
            if (usableObj == null) return;

            foreach (var (prefabID, entry) in ModuleRegistry.Entries)
            {
                if (Mathf.Approximately(speed, entry.SpeedInternal) &&
                    usableObj.prefabID != prefabID)
                {
                    usableObj.prefabID = prefabID;
                    break;
                }
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
