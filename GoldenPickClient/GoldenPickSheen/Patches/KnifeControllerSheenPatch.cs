using System;
using System.Collections;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // UNIVERSAL in-hands hook: WeaponPrefab.OnEnable fires whenever ANY weapon GameObject
    // becomes active in the scene — for ALL weapon types, on ALL paths (raid spawn with
    // pick equipped, hideout, manual swap-to-pick, swap-back). this replaces the previous
    // SetItemInHandsSheenPatch which only caught manual-equip and missed the "spawn into raid
    // already holding the pick" case (controller pre-constructed during raid load, no equip
    // event fires).
    //
    // we filter by the prefab's template id (cheap struct compare), then poll MainPlayer's
    // HandsController to find the actual Item — needed for the deterministic per-pick color.
    // poll is a coroutine because OnEnable can fire microseconds before the controller is
    // assigned at raid load.
    //
    // OnEnable also fires for THIRD-PERSON weapon models on other players — we filter on
    // MainPlayer.HandsController.Item so we only activate for OUR held pick, not bystanders'.
    public class WeaponPrefabOnEnableSheenPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(WeaponPrefab), nameof(WeaponPrefab.OnEnable));

        [PatchPostfix]
        public static void Postfix(WeaponPrefab __instance)
        {
            try
            {
                // no pre-filter on the prefab's template — ResourceType is `protected` on
                // AssetPoolObject so we'd need reflection to read it from here. instead, the
                // coroutine checks MainPlayer.HandsController on frame 1 and exits immediately
                // if it's not our pick. cost: one near-instant coroutine per weapon swap.
                Plugin.Instance.StartCoroutine(WaitForLocalKnifeAndActivate());
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] WeaponPrefab.OnEnable postfix failed: {e}"); }
        }

        // poll the LOCAL player's HandsController until it's a BaseKnifeController for our
        // pick. exits FAST if the player swapped to a different weapon (so non-pick swaps
        // don't burn 3 seconds of polling). only keeps waiting if HandsController is null /
        // not-yet-set (raid spawn case where the controller resolves a frame or two later).
        private static IEnumerator WaitForLocalKnifeAndActivate()
        {
            float t = 0f;
            const float Timeout = 3f;
            while (t < Timeout)
            {
                var player = Singleton<GameWorld>.Instance?.MainPlayer;
                var ctrl = player?.HandsController;

                if (ctrl is Player.BaseKnifeController bkc
                    && bkc.Item != null
                    && bkc.Item.TemplateId.ToString() == GoldenPickConstants.GoldenPickTpl)
                {
                    // hit. dedupe — OnEnable can fire multiple times for the same prefab. dedupe
                    // by STABLE id (Scabbard slot's pick), not the controller's possibly-cloned
                    // item id; that way clone-respawns (hideout firing range) reuse the existing
                    // instance instead of activating a fresh one each time.
                    var stableId = StablePickIdResolver.Resolve(bkc.Item.Id);
                    if (SheenHost.ManagerIfExists?.HasInstance(stableId, SheenManager.ScopeIngame) == true) yield break;
                    ActivateForKnife(bkc.Item, bkc);
                    yield break;
                }

                // settled-but-not-ours: exit immediately rather than polling for 3 seconds.
                // null controller / null player means "not yet settled" → keep waiting.
                if (ctrl != null) yield break;

                t += Time.deltaTime;
                yield return null;
            }
        }

        private static void ActivateForKnife(Item item, Player.BaseKnifeController ctrl)
        {
            try
            {
                // BaseKnifeController.HandsHierarchy is internal/protected, so reflect to read it.
                // its a TransformLinks (the WeaponPrefab.Hierarchy from smethod_4) — get the
                // canonical Weapon_root bone off it.
                var ctrlType = ctrl.GetType();
                var hh = AccessTools.Field(ctrlType, "_handsHierarchy")?.GetValue(ctrl)
                      ?? AccessTools.Property(ctrlType, "HandsHierarchy")?.GetValue(ctrl);

                Transform weaponRoot = null;
                if (hh != null)
                {
                    var getXform = AccessTools.Method(hh.GetType(), "GetTransform", new[] { typeof(ECharacterWeaponBones) });
                    if (getXform != null)
                        weaponRoot = getXform.Invoke(hh, new object[] { ECharacterWeaponBones.Weapon_root }) as Transform;
                    if (weaponRoot == null)
                    {
                        var self = AccessTools.Property(hh.GetType(), "Self")?.GetValue(hh) as Transform;
                        if (self != null) weaponRoot = self;
                    }
                }

                if (weaponRoot == null)
                {
                    Plugin.LogSource?.LogWarning("[GoldenPick/Sheen] knife controller had no Weapon_root transform — skipping");
                    return;
                }

                // parent to the actual visible MESH, not Weapon_root — see SheenAnchorFinder
                // header for why. Weapon_root is a rig attachment that's at the belt in some
                // poses (menu char) while the mesh tracks the hand.
                var meshXform = SheenAnchorFinder.FindMeshTransform(weaponRoot.gameObject) ?? weaponRoot;

                var baseMat = SheenAssets.GetBaseMaterial();
                if (baseMat == null) return;

                // resolve a STABLE id for color/metadata lookup — some hideout sub-scenes
                // (firing range) spawn a CLONE of the equipped pick with a fresh MongoId, and
                // that clone has no metadata record → falls through to hash color which differs
                // per id. resolving to the player's Scabbard slot's pick id stays stable.
                var stableId = StablePickIdResolver.Resolve(item.Id);

                Color color;
                var meta = PickMetadataLookup.GetOrNull(stableId);
                if (meta != null && PickMetadataLookup.TryParseHexColor(meta.SheenColorHex, out var customColor))
                    color = customColor;
                else
                    color = SheenColors.ForItemId(stableId);

                // instance key uses the stable id too — keeps the SheenManager dictionary
                // from accumulating one entry per clone. preview cam's match-by-owner-id
                // (mitsuru pattern) still works because we always use the same stable id.
                SheenHost.Manager.Activate(stableId, meshXform, meshXform.gameObject, color, baseMat, SheenManager.ScopeIngame);
                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] pick sheen ON (in-hands) id={item.Id} color={color}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] knife activate failed: {e}"); }
        }
    }

    // tear down the sheen when the controller is destroyed (sheathed / swapped). matches
    // Player.BaseKnifeController.Destroy at Player.cs:32440 — the unconditional teardown that
    // calls AssetPoolObject.ReturnToPool. Prefix because Item.Id is needed BEFORE the GameObject
    // gets pooled. Destroy is plain non-generic, no Harmony issues.
    public class KnifeControllerDestroySheenPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player.BaseKnifeController), nameof(Player.BaseKnifeController.Destroy));

        [PatchPrefix]
        public static void Prefix(Player.BaseKnifeController __instance)
        {
            try
            {
                var item = __instance?.Item;
                if (item == null || item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;
                // only tear down the in-game scope — menu/preview instances are owned by
                // separate hooks and live on their own lifecycle. resolve to the stable id
                // so we deactivate the right instance even when controllers carry clones.
                var stableId = StablePickIdResolver.Resolve(item.Id);
                SheenHost.ManagerIfExists?.Deactivate(stableId, SheenManager.ScopeIngame);
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] knife Destroy prefix failed: {e}"); }
        }
    }
}
