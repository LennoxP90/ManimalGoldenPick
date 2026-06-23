using System.Reflection;
using System.Text;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using TMPro;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // tooltip + inspect-window patches that inject the pick's custom name / description /
    // pick number. each patch template-gates first (cheap), then looks up PickMetadataLookup
    // and rewrites the field in place. PickMetadataLookup is server-backed with an in-session
    // cache — first hover may hit the network, subsequent hovers are local.

    // inventory icon caption (GridItemView.method_29 postfix → __result)

    public class PickIconCaptionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GridItemView), "method_29");

        [PatchPostfix]
        public static void Postfix(GridItemView __instance, ref string __result)
        {
            var item = __instance?.Item;
            if (item == null) return;
            if (item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

            var meta = PickMetadataLookup.GetOrNull(item.Id);
            if (meta == null) return;  // not a tracked pick — leave default caption alone

            // start from whatever the vanilla caption was (preserves color tags). strip the
            // wrapping color if present so we can insert custom name cleanly; if not, just
            // append.
            var label = meta.CustomName ?? __result;
            if (meta.PickNumber.HasValue)
                __result = $"{label} (#{meta.PickNumber.Value})";
            else if (meta.CustomName != null)
                __result = label;
        }
    }

    // inspect window title (ItemSpecificationPanel.String_0 getter postfix)
    public class PickInspectNamePatch : ModulePatch
    {
        private static FieldInfo _itemField;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.PropertyGetter(typeof(ItemSpecificationPanel), "String_0");

        [PatchPostfix]
        public static void Postfix(ItemSpecificationPanel __instance, ref string __result)
        {
            if (_itemField == null)
                _itemField = AccessTools.Field(typeof(ItemSpecificationPanel), "item_0");
            var item = _itemField?.GetValue(__instance) as Item;
            if (item == null) return;
            if (item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

            var meta = PickMetadataLookup.GetOrNull(item.Id);
            if (meta == null) return;

            var label = meta.CustomName ?? __result;
            __result = meta.PickNumber.HasValue ? $"{label} (#{meta.PickNumber.Value})" : label;
        }
    }

    // inspect description (ItemSpecificationPanel.method_1 postfix). layout:
    //   <custom description, or default if none>
    //   <blank line>
    //   Pick #N        (only if pick_number is set)
    public class PickInspectDescPatch : ModulePatch
    {
        private static FieldInfo  _itemField;
        private static FieldInfo  _labelsField;
        private static MethodInfo _setDescMethod;
        private static PropertyInfo _examinedGetter;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ItemSpecificationPanel), "method_1");

        [PatchPostfix]
        public static void Postfix(ItemSpecificationPanel __instance)
        {
            if (_itemField == null)
            {
                _itemField      = AccessTools.Field(typeof(ItemSpecificationPanel), "item_0");
                _labelsField    = AccessTools.Field(typeof(ItemSpecificationPanel), "_itemLabels");
                _examinedGetter = AccessTools.Property(typeof(ItemSpecificationPanel), "Boolean_0");
            }

            var item = _itemField?.GetValue(__instance) as Item;
            if (item == null) return;
            if (item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

            var meta = PickMetadataLookup.GetOrNull(item.Id);
            if (meta == null) return;
            // nothing to inject? bail (don't disturb vanilla)
            if (meta.CustomDescription == null && !meta.PickNumber.HasValue) return;

            var labels = _labelsField?.GetValue(__instance);
            if (labels == null) return;
            if (_setDescMethod == null) _setDescMethod = AccessTools.Method(labels.GetType(), "method_2");
            if (_setDescMethod == null) return;

            bool examined = (bool?)_examinedGetter?.GetValue(__instance) ?? false;
            string baseDesc = examined
                ? (meta.CustomDescription ?? item.Description?.Localized(null) ?? string.Empty)
                : string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(baseDesc))
                sb.Append(baseDesc);
            if (meta.PickNumber.HasValue)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append($"Pick #{meta.PickNumber.Value}");
            }

            _setDescMethod.Invoke(labels, new object[] { sb.ToString() });
        }
    }

    // icon caption auto-sizing (GridItemView.method_28 postfix) — lets long custom names
    // shrink to fit the icon label area instead of being cut off
    public class PickIconAutoSizePatch : ModulePatch
    {
        private static FieldInfo _captionField;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GridItemView), "method_28");

        [PatchPostfix]
        public static void Postfix(GridItemView __instance)
        {
            var item = __instance?.Item;
            if (item == null) return;
            if (item.TemplateId.ToString() != GoldenPickConstants.GoldenPickTpl) return;

            if (_captionField == null) _captionField = AccessTools.Field(typeof(GridItemView), "Caption");
            var caption = _captionField?.GetValue(__instance) as TextMeshProUGUI;
            if (caption == null) return;

            // first application only: capture the prefab's original font size as the ceiling
            // so auto-sizing can shrink but never grow past Tarkov's default icon label size.
            if (!caption.enableAutoSizing)
            {
                caption.fontSizeMax      = caption.fontSize;
                caption.fontSizeMin      = 4f;
                caption.enableAutoSizing = true;
            }
        }
    }
}
