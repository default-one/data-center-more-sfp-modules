using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;

[assembly: MelonInfo(typeof(MoreSFPModules.Core), "More SFP Modules", "1.0.0", "leoms1408")]
[assembly: MelonGame("Waseku", "Data Center")]

namespace MoreSFPModules
{
    public class Core : MelonMod
    {
        // Sprite from the vanilla QSFP+ shop entry — reused as icon for all custom modules.
        internal static Sprite BaseQsfpSprite;

        // sfpType of the vanilla QSFP+ module (form-factor; determines port compatibility).
        // Our custom modules keep this value so they fit the same switch ports.
        internal static int BaseQsfpSfpType = -1;

        // prefabID of the vanilla QSFP+ module — used as clone source in BuildModulePrefab/BuildBoxPrefab.
        internal static int BaseQsfpPrefabID = -1;

        // Inactive holder for prefab templates — parenting templates here makes their
        // activeInHierarchy = false, so the game's UsableObject tracker ignores them.
        // Object.Instantiate still produces active clones from inactive-hierarchy objects.
        internal static GameObject TemplateHolder { get; private set; }

        // -----------------------------------------------------------------------
        // Scans vanilla sfpPrefabs to find the highest-speed module (QSFP+ 40G),
        // stores it as the clone source, then extends the sfpPrefabs array with
        // one slot per custom module starting at MOD_ID_BASE (100).
        //
        // Starting at 100 instead of vanillaCount prevents prefabID collisions if
        // the game later adds new vanilla SFP types at indices 4, 5, 6 …
        //
        // Called from PatchMainGameManagerAwake — the earliest point where
        // sfpPrefabs is populated, guaranteed to run before OnLoad() restores saves.
        // -----------------------------------------------------------------------
        internal static void SetupRegistry(MainGameManager mgm)
        {
            ModuleRegistry.Clear();

            var sfpPrefabs = mgm.sfpPrefabs;
            if (sfpPrefabs == null || sfpPrefabs.Length == 0)
            {
                MelonLogger.Warning("[More SFP] sfpPrefabs is empty — skipping setup.");
                return;
            }

            MelonLogger.Msg($"[More SFP] Vanilla SFP prefabs: {sfpPrefabs.Length}");

            float highestSpeed = -1f;

            for (int i = 0; i < sfpPrefabs.Length; i++)
            {
                var go        = sfpPrefabs[i];
                if (go == null) continue;
                var sfpMod    = go.GetComponent<SFPModule>();
                var usableObj = go.GetComponent<UsableObject>();
                float speed   = sfpMod    != null ? sfpMod.speed       : -1f;
                int   sfpType = sfpMod    != null ? sfpMod.sfpType     : -1;
                int   pid     = usableObj != null ? usableObj.prefabID : -1;

                if (speed > highestSpeed)
                {
                    highestSpeed     = speed;
                    BaseQsfpSfpType  = sfpType;
                    BaseQsfpPrefabID = pid;
                }
            }

            if (BaseQsfpPrefabID < 0)
            {
                MelonLogger.Error("[More SFP] Could not identify base QSFP+ prefab.");
                return;
            }

            MelonLogger.Msg($"[More SFP] Base QSFP+: prefabID={BaseQsfpPrefabID}, " +
                            $"sfpType={BaseQsfpSfpType}, {highestSpeed * 5f} Gbps");

            // Create/recreate the inactive holder that hides templates from the world system.
            if (TemplateHolder != null)
                Object.Destroy(TemplateHolder);
            TemplateHolder = new GameObject("MoreSFP_TemplateHolder");
            TemplateHolder.SetActive(false);
            Object.DontDestroyOnLoad(TemplateHolder);

            const int MOD_ID_BASE = 100;
            int vanillaCount = sfpPrefabs.Length;

            if (vanillaCount > MOD_ID_BASE)
            {
                MelonLogger.Error($"[More SFP] vanilla sfpPrefabs.Length={vanillaCount} exceeds " +
                                  $"MOD_ID_BASE={MOD_ID_BASE}! prefabID collision risk — mod disabled.");
                return;
            }

            // Vanilla entries at their original indices, null padding up to MOD_ID_BASE,
            // then one slot per custom module.
            var extended = new GameObject[MOD_ID_BASE + ModuleList.All.Length];
            for (int i = 0; i < vanillaCount; i++)
                extended[i] = sfpPrefabs[i];

            int nextID = MOD_ID_BASE;

            foreach (var def in ModuleList.All)
            {
                int id = nextID++;
                var entry = new ModuleRegistry.Entry(
                    speedInternal: def.InternalSpeed,
                    moduleSfpType: BaseQsfpSfpType,
                    boxSfpType:    id,
                    basePrefabID:  BaseQsfpPrefabID
                );
                ModuleRegistry.Register(id, entry);

                // Store a template at sfpPrefabs[id] so LoadSFPsFromSave (which does
                // direct array access) can find the prefab during save loading.
                // Instantiated directly under TemplateHolder so it is never active
                // in hierarchy — the world tracker ignores it.
                // PatchLoadSFPsFromSave refreshes this slot before each load to guard
                // against Il2Cpp GC invalidating the cached pointer over time.
                var template = BuildModulePrefab(mgm, id, entry, TemplateHolder.transform);
                if (template != null)
                    template.name = $"SFPModule_template_{id}";
                extended[id] = template;

                MelonLogger.Msg($"[More SFP] Registered '{def.DisplayName}': " +
                                $"prefabID={id}, {def.SpeedGbps} Gbps");
            }

            mgm.sfpPrefabs = extended;
            MelonLogger.Msg($"[More SFP] sfpPrefabs extended: {vanillaCount} → {extended.Length}");
        }

        // -----------------------------------------------------------------------
        // Clones the vanilla QSFP+ module prefab and applies our custom speed and
        // prefabID. Called on-demand from patches rather than caching the result,
        // because Il2Cpp's GC can silently invalidate native pointers on cached
        // GameObjects stored in C# data structures.
        //
        // parent: when non-null the clone is instantiated directly under that transform,
        // so it is never active in hierarchy and the world tracker cannot pick it up.
        // Pass TemplateHolder.transform for cached templates, null for live clones.
        // -----------------------------------------------------------------------
        internal static GameObject BuildModulePrefab(MainGameManager mgm, int prefabID,
                                                     ModuleRegistry.Entry entry,
                                                     Transform parent = null)
        {
            var basePrefab = mgm.sfpPrefabs[entry.BasePrefabID];
            if (basePrefab == null)
            {
                MelonLogger.Error($"[More SFP] Base prefab [{entry.BasePrefabID}] is null.");
                return null;
            }

            var clone = parent != null
                ? Object.Instantiate(basePrefab, parent, false)
                : Object.Instantiate(basePrefab);
            clone.name = $"SFPModule_custom_{prefabID}";

            var sfpMod = clone.GetComponent<SFPModule>();
            if (sfpMod != null)
                sfpMod.speed = entry.SpeedInternal;

            var usableObj = clone.GetComponent<UsableObject>();
            if (usableObj != null)
                usableObj.prefabID = prefabID;

            return clone;
        }

        // -----------------------------------------------------------------------
        // Clones the vanilla QSFP+ box prefab and applies our custom sfpBoxType
        // and prefabID. Also updates all child SFPModule components inside the box
        // so the player receives the correct module when unboxing.
        // -----------------------------------------------------------------------
        internal static GameObject BuildBoxPrefab(MainGameManager mgm, int prefabID,
                                                  ModuleRegistry.Entry entry)
        {
            var boxPrefabs = mgm.sfpsBoxedPrefab;
            if (boxPrefabs == null) return null;

            GameObject baseBox = entry.BasePrefabID < boxPrefabs.Length
                ? boxPrefabs[entry.BasePrefabID]
                : null;

            // Fall back to the first non-null box if the expected index is missing.
            if (baseBox == null)
                for (int i = 0; i < boxPrefabs.Length; i++)
                    if (boxPrefabs[i] != null) { baseBox = boxPrefabs[i]; break; }

            if (baseBox == null)
            {
                MelonLogger.Warning("[More SFP] No base box prefab found.");
                return null;
            }

            var clone = Object.Instantiate(baseBox);
            clone.name = $"SFPBox_custom_{prefabID}";

            var sfpBox = clone.GetComponent<SFPBox>();
            if (sfpBox != null)
                sfpBox.sfpBoxType = prefabID;

            var usableObj = clone.GetComponent<UsableObject>();
            if (usableObj != null)
                usableObj.prefabID = prefabID;

            // The box prefab contains the SFPModules as child GameObjects.
            // Only update speed — do NOT set prefabID on children, as that would
            // register them as independent world items and cause them to spawn loose.
            // PatchCableLinkInsertSFP corrects the prefabID at insertion time instead.
            foreach (var childModule in clone.GetComponentsInChildren<SFPModule>())
                childModule.speed = entry.SpeedInternal;

            return clone;
        }

        // -----------------------------------------------------------------------
        // Triggered on every scene load. Starts the shop injection coroutine for
        // any scene other than the main menu (buildIndex 0).
        // -----------------------------------------------------------------------
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (buildIndex != 0)
                MelonCoroutines.Start(AddShopItems());
        }

        // -----------------------------------------------------------------------
        // Waits for the shop to finish initializing, then injects a shop button
        // for each registered custom module into the "HL Mods" section.
        // The 1.5 s delay is necessary because the shop UI is built after scene load.
        // -----------------------------------------------------------------------
        private IEnumerator AddShopItems()
        {
            yield return new WaitForSeconds(1.5f);

            var mgm = MainGameManager.instance;
            if (mgm == null) { LoggerInstance.Warning("MGM null — shop skipped."); yield break; }

            var computerShop = mgm.computerShop;
            if (computerShop == null) { LoggerInstance.Warning("Shop null — skipped."); yield break; }

            // Find the vanilla QSFP+ box shop entry to use as a UI clone template.
            // The shop sells SFPBox items (type 9), not bare SFPModule items.
            ShopItem sourceItem = null;
            if (computerShop.shopItems != null)
            {
                foreach (var si in computerShop.shopItems)
                {
                    if (si == null || si.shopItemSO == null) continue;

                    if ((int)si.shopItemSO.itemType == 9 && si.shopItemSO.itemID == BaseQsfpPrefabID)
                    {
                        sourceItem     = si;
                        BaseQsfpSprite = si.shopItemSO.sprite;
                    }
                }
            }

            if (sourceItem == null)
            {
                LoggerInstance.Warning("[More SFP] No QSFP+ box shop item found — shop buttons skipped.");
                yield break;
            }

            var shopParent = computerShop.shopItemParent;
            if (shopParent == null) { LoggerInstance.Warning("shopItemParent null."); yield break; }

            // Target the "HL Mods" section inside VL-ShopItems so our items appear
            // in the correct category rather than being appended at the end.
            var modsTransform = shopParent.transform.Find("HL Mods");
            if (modsTransform != null)
                shopParent = modsTransform.gameObject;
            else
                LoggerInstance.Warning("[More SFP] 'HL Mods' not found — falling back to shopItemParent.");

            float itemHeight = 0f;
            var sourceRt = sourceItem.GetComponent<UnityEngine.RectTransform>();
            if (sourceRt != null)
                itemHeight = sourceRt.rect.height;

            int addedCount = 0;
            int defIndex   = 0;
            foreach (var (prefabID, _) in ModuleRegistry.Entries)
            {
                if (defIndex >= ModuleList.All.Length) break;
                var def = ModuleList.All[defIndex++];
                var added = AddShopButton(sourceItem, shopParent, prefabID, def, sourceItem.shopItemSO.price);
                if (added != null) addedCount++;
            }

            // The HL Mods container has a fixed height — extend it so the ScrollRect
            // can scroll far enough to reveal our newly added items.
            var containerRt = shopParent.GetComponent<UnityEngine.RectTransform>();
            if (containerRt != null && itemHeight > 0f && addedCount > 0)
            {
                var sd = containerRt.sizeDelta;
                sd.y += itemHeight * addedCount;
                containerRt.sizeDelta = sd;
            }

            UnityEngine.Canvas.ForceUpdateCanvases();
        }

        // -----------------------------------------------------------------------
        // Clones an existing shop item GameObject, assigns a new ShopItemSO with
        // the custom module's name/price/ID, and adds it to the given parent.
        // Returns the created GameObject, or null if the ShopItem component is missing.
        // -----------------------------------------------------------------------
        private static GameObject AddShopButton(ShopItem source, GameObject parent, int prefabID,
                                               ModuleDefinition def, int basePrice)
        {
            var newSO = ScriptableObject.CreateInstance<ShopItemSO>();
            newSO.itemName   = $"5x {def.DisplayName}";
            newSO.price      = (int)(basePrice * def.PriceMultiplier);
            newSO.xpToUnlock = def.XpToUnlock;
            newSO.itemType   = source.shopItemSO.itemType; // SFPBox (9)
            newSO.itemID     = prefabID;
            newSO.eol        = source.shopItemSO.eol;
            newSO.sprite     = BaseQsfpSprite;

            var cloned = Object.Instantiate(source.gameObject, parent.transform, false);
            cloned.name = $"ShopItem_{def.DisplayName.Replace(" ", "_")}";
            cloned.transform.localPosition = Vector3.zero;
            cloned.transform.localScale    = Vector3.one;

            var shopItem = cloned.GetComponent<ShopItem>();
            if (shopItem == null)
            {
                MelonLogger.Error($"[More SFP] ShopItem component missing for '{def.DisplayName}'.");
                Object.Destroy(cloned);
                return null;
            }

            shopItem.shopItemSO = newSO;
            shopItem.guid       = def.ShopGuid;
            cloned.SetActive(true);

            MelonLogger.Msg($"[More SFP] Shop button added: '{newSO.itemName}' (prefabID={prefabID}, price={newSO.price})");
            return cloned;
        }
    }
}
