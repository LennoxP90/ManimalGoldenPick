using System.Linq;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using Manimal.GoldenPick.GoldenPickSheen;
using SPT.Reflection.Patching;

namespace Manimal.GoldenPick.Extract
{
    // a registered golden pick satisfies red-rebel/paracord extracts (Cliff Descent etc) AND
    // lifts the no-armor restriction on the same extract. each requirement is an
    // ExfiltrationRequirement subclass: GClass3706=HasItem, GClass3705=EmptyOrSize,
    // GClass3704=Empty. counterfeit picks don't bypass — same relay-registered gate as sheen.
    internal static class RedRebelBypass
    {
        public const string RedRebelTpl = "5c0126f40db834002a125382";
        public const string ParacordTpl = "5c12688486f77426843c7d32";

        // template ids for which a registered golden pick is treated as "have this item"
        public static readonly System.Collections.Generic.HashSet<string> BypassableItemTpls
            = new System.Collections.Generic.HashSet<string> { RedRebelTpl, ParacordTpl };

        // true if the player carries a relay-registered golden pick. counterfeits return false.
        public static bool PlayerHasRegisteredPick(Player player)
        {
            try
            {
                var inv = player?.Profile?.Inventory;
                if (inv == null) return false;
                foreach (var item in inv.GetAllItemByTemplate(GoldenPickConstants.GoldenPickTpl))
                {
                    if (item == null) continue;
                    if (PickMetadataLookup.GetOrNull(item.Id) != null) return true;
                }
            }
            catch { /* inventory not ready / iteration hiccup — treat as no */ }
            return false;
        }

        // scopes the slot bypass — only lift slot restrictions on extracts that also require
        // a bypassable item, not every extract that happens to check empty slots.
        public static bool ExtractIsRedRebelStyle(ExfiltrationPoint point)
        {
            try
            {
                if (point?.Requirements == null) return false;
                foreach (var req in point.Requirements)
                {
                    if (req == null) continue;
                    if (req.Requirement == ERequirementState.HasItem && BypassableItemTpls.Contains(req.Id))
                        return true;
                }
            }
            catch { }
            return false;
        }
    }

    // HasItem (GClass3706) — satisfies red rebel / paracord item checks
    public class RedRebelHasItemBypassPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GClass3706), nameof(ExfiltrationRequirement.Met),
                new[] { typeof(Player), typeof(ExfiltrationPoint) });

        [PatchPostfix]
        public static void Postfix(GClass3706 __instance, Player player, ref bool __result)
        {
            if (__result) return;
            if (__instance == null || !RedRebelBypass.BypassableItemTpls.Contains(__instance.Id)) return;
            if (!RedRebelBypass.PlayerHasRegisteredPick(player)) return;
            __result = true;
        }
    }

    // EmptyOrSize (GClass3705) — lifts the no-armor restriction
    public class RedRebelEmptyOrSizeBypassPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GClass3705), nameof(ExfiltrationRequirement.Met),
                new[] { typeof(Player), typeof(ExfiltrationPoint) });

        [PatchPostfix]
        public static void Postfix(Player player, ExfiltrationPoint point, ref bool __result)
        {
            if (__result) return;
            if (!RedRebelBypass.ExtractIsRedRebelStyle(point)) return;
            if (!RedRebelBypass.PlayerHasRegisteredPick(player)) return;
            __result = true;
        }
    }

    // Empty (GClass3704) — strict-empty slot variant
    public class RedRebelEmptyBypassPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GClass3704), nameof(ExfiltrationRequirement.Met),
                new[] { typeof(Player), typeof(ExfiltrationPoint) });

        [PatchPostfix]
        public static void Postfix(Player player, ExfiltrationPoint point, ref bool __result)
        {
            if (__result) return;
            if (!RedRebelBypass.ExtractIsRedRebelStyle(point)) return;
            if (!RedRebelBypass.PlayerHasRegisteredPick(player)) return;
            __result = true;
        }
    }
}
