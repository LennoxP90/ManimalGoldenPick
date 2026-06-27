using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // sheen on the loading-screen / menu character model. PlayerModelLoader.method_0 builds
    // the menu weapon prefab via PoolManagerClass.CreateItem → WeaponPrefab.Init(null, true).
    // postfix runs after the prefab is fully set up so we have its hierarchy.
    public class MenuModelSheenPatch : ModulePatch
    {
        // remember which menu prefab carried which pick id, so Clear() can deactivate without
        // needing the Item parameter (Clear takes none)
        internal static readonly Dictionary<WeaponPrefab, string> MenuPrefabPickId = new Dictionary<WeaponPrefab, string>();

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PlayerModelLoader), "method_0");

        [PatchPostfix]
        public static void Postfix(PlayerModelLoader __instance, Item weapon)
        {
            try
            {
                if (weapon == null) return;
                if (weapon.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

                var wp = __instance?.WeaponPrefab;
                if (wp == null) return;

                var weaponRoot = wp.Hierarchy?.GetTransform(ECharacterWeaponBones.Weapon_root);
                if (weaponRoot == null)
                {
                    Plugin.LogSource?.LogWarning("[GoldenPick/Sheen] menu prefab had no Weapon_root — skipping");
                    return;
                }
                // parent to the actual mesh transform — Weapon_root sits at the rig attachment
                // (belt area in the menu char's pose) but the visible mesh is at the hand
                var meshXform = SheenAnchorFinder.FindMeshTransform(weaponRoot.gameObject) ?? weaponRoot;

                var baseMat = SheenAssets.GetBaseMaterial();
                if (baseMat == null) return;

                // EFT clones inventory items when building the menu/loading preview
                // character → each clone gets a fresh MongoId → SheenColors.ForItemId
                // returns a DIFFERENT color each render. resolve the REAL pick's id from
                // the player's profile so the menu color matches the in-raid color (which
                // uses the stable inventory id).
                var stableId = StablePickIdResolver.Resolve(weapon.Id);

                // counterfeit gate — only relay-registered picks get sheen. unregistered
                // ones render as plain golden picks until the next raid-end audit catches them.
                var meta = PickMetadataLookup.GetOrNull(stableId);
                if (meta == null) return;

                // authored sheen color wins over the deterministic hash (admin-granted picks)
                UnityEngine.Color color;
                if (PickMetadataLookup.TryParseHexColor(meta.SheenColorHex, out var customColor))
                    color = customColor;
                else
                    color = SheenColors.ForItemId(stableId);
                var mgr = SheenHost.Manager;
                // key the menu instance by the STABLE id, not the cloned one — that way each
                // menu open re-uses the same key (replacing the prior instance) instead of
                // accumulating stale instances across renders.
                var menuKey = SheenManager.MakeKey(stableId, SheenManager.ScopeMenu);
                mgr.MenuModeItemIds.Add(menuKey);
                MenuPrefabPickId[wp] = stableId;  // map back to STABLE id for Clear lookup
                mgr.Activate(stableId, meshXform, meshXform.gameObject, color, baseMat, SheenManager.ScopeMenu);
                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] pick sheen ON (menu) id={stableId} (cloned was {weapon.Id}) color={color}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] menu method_0 postfix failed: {e}"); }
        }

        // stable-pick-id resolution moved to StablePickIdResolver (shared with the in-hands
        // hook so hideout firing-range clones, menu char clones, etc. all resolve the same).
    }

    // PlayerModelLoader.Clear() returns the menu prefab to pool — that's our cleanup signal.
    // Prefix so we read the prefab BEFORE it's nulled.
    public class MenuModelSheenClearPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(PlayerModelLoader), nameof(PlayerModelLoader.Clear));

        [PatchPrefix]
        public static void Prefix(PlayerModelLoader __instance)
        {
            try
            {
                var wp = __instance?.WeaponPrefab;
                if (wp == null) return;
                if (!MenuModelSheenPatch.MenuPrefabPickId.TryGetValue(wp, out var pickId)) return;
                MenuModelSheenPatch.MenuPrefabPickId.Remove(wp);
                var mgr = SheenHost.ManagerIfExists;
                if (mgr != null)
                {
                    // remove the composite key we stored at Activate time + deactivate the
                    // menu-scoped instance only (in-game scoped instance for the same id stays)
                    mgr.MenuModeItemIds.Remove(SheenManager.MakeKey(pickId, SheenManager.ScopeMenu));
                    mgr.Deactivate(pickId, SheenManager.ScopeMenu);
                }
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] menu Clear prefix failed: {e}"); }
        }
    }
}
