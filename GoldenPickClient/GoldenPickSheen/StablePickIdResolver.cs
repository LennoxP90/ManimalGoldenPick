using EFT.InventoryLogic;
using SPT.Reflection.Utils;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // returns the STABLE Item.Id of whatever pick is currently equipped in the player's
    // Scabbard slot. used as a color-resolution anchor everywhere a "candidate" pick id
    // might be a CLONE with a fresh MongoId (cloned inventories: menu char preview, hideout
    // firing range sub-scene, anywhere EFT renders a separate world-instance of the player's
    // gear). all of those clones share the SAME equipped scabbard item from the player's
    // real profile — that's what we key sheen lookups off so the color stays consistent.
    //
    // fallback is the candidate id when the profile/inventory/scabbard slot can't be resolved
    // (rare loading-screen edge case) — better to render SOMETHING than nothing.
    internal static class StablePickIdResolver
    {
        public static string Resolve(string candidateId)
        {
            try
            {
                var inv = PatchConstants.BackEndSession?.Profile?.Inventory;
                var equipped = inv?.Equipment?.GetSlot(EquipmentSlot.Scabbard)?.ContainedItem;
                if (equipped != null && equipped.TemplateId.ToString() == GoldenPickConstants.GoldenPickTpl)
                    return equipped.Id;
            }
            catch { /* profile not ready — fall through */ }
            return candidateId;
        }
    }
}
