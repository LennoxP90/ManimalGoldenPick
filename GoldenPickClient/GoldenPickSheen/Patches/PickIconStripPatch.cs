using System.Reflection;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Manimal.GoldenPick.GoldenPickSheen.Patches
{
    // colored strip across the top edge of the inventory icon, showing the pick's sheen
    // color. applied to every pick — admin-granted uses PickMetadataLookup.SheenColorHex,
    // crate-derived falls through to SheenColors.ForItemId. patches ItemView.UpdateStaticInfo
    // and adds a thin RectTransform+Image anchored above the text label.
    public class PickIconStripPatch : ModulePatch
    {
        private const string StripGoName = "GoldenPickSheenStrip";

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(ItemView), "UpdateStaticInfo");

        [PatchPostfix]
        public static void Postfix(ItemView __instance)
        {
            try
            {
                if (__instance == null) return;
                var item = __instance.Item;
                var isOurPick = item != null && item.TemplateId.ToString() == GoldenPickConstants.GoldenPickTpl;
                var stripGo = __instance.transform.Find(StripGoName)?.gameObject;

                // EFT pools ItemView instances — the same view gets recycled to display
                // different items. if this update is for a non-pick item OR a counterfeit
                // pick (not on the relay), hide the strip explicitly so it doesn't linger
                // from a prior render.
                var meta = isOurPick ? PickMetadataLookup.GetOrNull(item.Id) : null;
                if (!isOurPick || meta == null)
                {
                    if (stripGo != null) stripGo.SetActive(false);
                    return;
                }

                // color — authored first, deterministic hash fallback for crate-derived
                Color stripColor;
                if (PickMetadataLookup.TryParseHexColor(meta.SheenColorHex, out var custom))
                    stripColor = custom;
                else
                    stripColor = SheenColors.ForItemId(item.Id);
                if (stripGo == null)
                {
                    stripGo = new GameObject(StripGoName);
                    stripGo.transform.SetParent(__instance.transform, false);

                    var rt = stripGo.AddComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot     = new Vector2(0.5f, 1f);
                    rt.sizeDelta = new Vector2(0f, 14f);
                    rt.anchoredPosition = Vector2.zero;

                    var img = stripGo.AddComponent<Image>();
                    img.raycastTarget = false;
                }

                stripGo.GetComponent<Image>().color = stripColor;
                stripGo.SetActive(true);

                // sibling-order: place above the icon mesh but below the text caption, so the
                // text isn't occluded and the icon mesh stays visible underneath the strip.
                int curIdx = stripGo.transform.GetSiblingIndex();
                int firstTextIdx = -1;
                int childCount = __instance.transform.childCount;
                for (int ci = 0; ci < childCount; ci++)
                {
                    var ch = __instance.transform.GetChild(ci);
                    if (ch == stripGo.transform) continue;
                    if (ch.GetComponentInChildren<TextMeshProUGUI>(true) != null)
                    {
                        firstTextIdx = ci;
                        break;
                    }
                }
                if (firstTextIdx >= 0)
                {
                    int insertAt = firstTextIdx > curIdx ? firstTextIdx - 1 : firstTextIdx;
                    stripGo.transform.SetSiblingIndex(insertAt);
                }
                else
                {
                    int target = Mathf.Max(0, childCount - 2);
                    int insertAt = target > curIdx ? target - 1 : target;
                    stripGo.transform.SetSiblingIndex(insertAt);
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogSource?.LogError($"[GoldenPick/Sheen] PickIconStrip postfix failed: {e}");
            }
        }
    }
}
