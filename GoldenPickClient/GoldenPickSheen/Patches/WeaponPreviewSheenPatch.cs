using System;
using System.Linq;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI.WeaponModding;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // sheen in the weapon-inspect popup (double-click → preview). EFT renders the previewed
    // weapon into its own camera; that camera isn't the main game cam, so the sheen wouldn't
    // show without registering it explicitly.
    //
    // EFT's preview-open call lives on a nested anonymous class inside WeaponPreview (compiler-
    // generated async-state-machine). its instance method_1 is the resolver — reflect to find it.
    //
    // approach: create a SEPARATE preview-scoped SheenInstance parented to the preview-spawned
    // weapon's mesh. preview prefabs come from PoolManagerClass.CreateCleanLootPrefab and have
    // simplified materials WITHOUT _StencilType — so we inject _StencilType=2 into them at
    // activate-time, matching what in-game weapon materials carry. this lets the sheen shader's
    // stencil test pass and gives us sheen in the inspect popup for ANY pick — equipped or not.
    public class WeaponPreviewOpenSheenPatch : ModulePatch
    {
        private static FieldInfo _weaponPreviewField;
        private static PropertyInfo _cameraProp;
        private static FieldInfo _itemField;
        // the preview-spawned weapon GameObject lives on WeaponPreview.gameObject_0 — the
        // field PoolManagerClass.CreateCleanLootPrefab assigns at the end of method_1
        private static FieldInfo _gameObjectField;
        private static readonly int StencilTypePropId = Shader.PropertyToID("_StencilType");

        protected override MethodBase GetTargetMethod()
        {
            foreach (var nested in typeof(WeaponPreview).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                var m = AccessTools.Method(nested, "method_1");
                if (m == null) continue;
                var wpField = AccessTools.GetDeclaredFields(nested).FirstOrDefault(f => f.FieldType == typeof(WeaponPreview));
                if (wpField == null) continue;
                _weaponPreviewField = wpField;
                return m;
            }
            Plugin.LogSource?.LogError("[GoldenPick/Sheen] couldnt find WeaponPreview inner method_1");
            return null;
        }

        [PatchPostfix]
        public static void Postfix(object __instance)
        {
            try
            {
                if (_weaponPreviewField == null) return;
                var preview = _weaponPreviewField.GetValue(__instance) as WeaponPreview;
                if (preview == null) return;

                if (_cameraProp == null) _cameraProp = AccessTools.Property(typeof(WeaponPreview), "WeaponPreviewCamera");
                var camera = _cameraProp?.GetValue(preview) as Camera;
                if (camera == null) return;

                if (_itemField == null) _itemField = AccessTools.Field(typeof(WeaponPreview), "item_0");
                var item = _itemField?.GetValue(preview) as Item;
                if (item == null) return;

                if (item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;
                var mgr = SheenHost.Manager;
                var baseMat = SheenAssets.GetBaseMaterial();
                if (baseMat == null) return;

                if (_gameObjectField == null) _gameObjectField = AccessTools.Field(typeof(WeaponPreview), "gameObject_0");
                var previewGo = _gameObjectField?.GetValue(preview) as GameObject;
                if (previewGo == null)
                {
                    Plugin.LogSource?.LogWarning("[GoldenPick/Sheen] preview gameObject_0 was null — sheen wont render in inspect");
                    return;
                }

                var meshXform = SheenAnchorFinder.FindMeshTransform(previewGo) ?? previewGo.transform;

                // inject _StencilType=2 into every material on the previewed prefab. clean-loot
                // prefabs use simplified shaders that may or may not even expose the property;
                // HasProperty is the cheap test. without this, the sheen shader's stencil pass
                // fails and the cube draws but stays invisible.
                int materialsTagged = 0;
                foreach (var r in previewGo.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var m in r.materials)
                    {
                        if (m == null) continue;
                        if (m.HasProperty(StencilTypePropId))
                        {
                            m.SetFloat(StencilTypePropId, 2f);
                            materialsTagged++;
                        }
                    }
                }

                // color resolution — admin-granted custom picks override the deterministic
                // hash via PickMetadataLookup. for stash picks not currently equipped, the
                // PreviewCameraItemId lookup uses the raw item id so OnPreRender finds the
                // right preview-scoped instance.
                Color color;
                var meta = PickMetadataLookup.GetOrNull(item.Id);
                if (meta != null && PickMetadataLookup.TryParseHexColor(meta.SheenColorHex, out var customColor))
                    color = customColor;
                else
                    color = SheenColors.ForItemId(item.Id);

                mgr.WeaponPreviewCameras.Add(camera);
                mgr.PreviewCameraItemId[camera] = item.Id;
                mgr.Activate(item.Id, meshXform, meshXform.gameObject, color, baseMat, SheenManager.ScopePreview);

                Plugin.LogSource?.LogInfo($"[GoldenPick/Sheen] pick sheen ON (preview) id={item.Id} color={color} materialsTagged={materialsTagged} renderingPath={camera.renderingPath}");
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] preview open postfix failed: {e}"); }
        }
    }

    // preview-close — unregister the camera so we stop trying to draw into a torn-down RT
    public class WeaponPreviewCloseSheenPatch : ModulePatch
    {
        private static PropertyInfo _cameraProp;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(WeaponPreview), "Hide");

        [PatchPostfix]
        public static void Postfix(WeaponPreview __instance)
        {
            try
            {
                if (_cameraProp == null) _cameraProp = AccessTools.Property(typeof(WeaponPreview), "WeaponPreviewCamera");
                var camera = _cameraProp?.GetValue(__instance) as Camera;
                if (camera == null) return;

                var mgr = SheenHost.ManagerIfExists;
                if (mgr == null) return;
                if (mgr.WeaponPreviewCameras.Remove(camera))
                {
                    // PreviewCameraItemId stores the raw item id — feed it to Deactivate(preview)
                    if (mgr.PreviewCameraItemId.TryGetValue(camera, out var pickId))
                        mgr.Deactivate(pickId, SheenManager.ScopePreview);
                    mgr.PreviewCameraItemId.Remove(camera);
                    mgr.RemoveCamera(camera);
                }
            }
            catch (Exception e) { Plugin.LogSource?.LogError($"[GoldenPick/Sheen] preview close postfix failed: {e}"); }
        }
    }
}
