using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace GoldenPick.Audit;

// boot-time scan that catches counterfeit golden picks (console-spawned, profile-edited,
// crash-orphaned). every profile's PMC + scav inventories are scanned; any golden-pick
// item whose Id ISN'T in PickMetadataStore gets its template rewritten to red rebel,
// and the profile is saved.
//
// the rationale: real picks always flow through one of two paths that store metadata:
//   - admin grant → /goldenpick/grant-pick → PickMetadataStore.Put
//   - crate unpack → /goldenpick/inherit-pickmeta → PickMetadataStore.Put
// anything that exists in inventory WITHOUT a metadata record came from somewhere we
// don't trust (console, edit, mod). the audit gives the player a red rebel — a real,
// usable melee — but strips the gold cosmetic + sheen.
//
// DEFENSIVE GUARD: refuses to run if PickMetadataStore.Count == 0. zero records means
// either a brand-new install (nothing to audit) OR a wiped/corrupted metadata file —
// running the audit in the latter case would transform EVERY real pick into red rebel.
// log loudly and skip on zero.
[Injectable]
public class CounterfeitPickAudit(
    ISptLogger<CounterfeitPickAudit> logger,
    SaveServer saveServer,
    PickMetadataStore metaStore
) : IOnLoad
{
    private const string GoldenPickTpl = "6a371980784a6d8a3ec033ed";
    private const string RedRebelTpl   = "5c0126f40db834002a125382";

    public async Task OnLoad()
    {
        try
        {
            var metaCount = metaStore.Count;
            if (metaCount == 0)
            {
                logger.Warning("[GoldenPick/Audit] PickMetadataStore has 0 records — SKIPPING counterfeit "
                             + "scan. either fresh install (fine) OR metadata file got wiped/corrupted "
                             + "(running the audit would red-rebel EVERY legit pick). investigate "
                             + "user/mods/GoldenPickServer/data/pick_metadata.json if you expected entries.");
                return;
            }

            var profiles = saveServer.GetProfiles();
            int profilesScanned = 0, picksTransformed = 0, picksKept = 0;

            foreach (var (sessionId, profile) in profiles)
            {
                profilesScanned++;
                bool changed = false;

                changed |= ScanInventory(profile.CharacterData?.PmcData?.Inventory?.Items, sessionId, "pmc",
                                         ref picksTransformed, ref picksKept);
                changed |= ScanInventory(profile.CharacterData?.ScavData?.Inventory?.Items, sessionId, "scav",
                                         ref picksTransformed, ref picksKept);

                if (changed)
                {
                    await saveServer.SaveProfileAsync(sessionId);
                    logger.Info($"[GoldenPick/Audit] profile {sessionId} saved after transformation");
                }
            }

            logger.Info($"[GoldenPick/Audit] scan complete: {profilesScanned} profiles, "
                      + $"{picksKept} legit picks kept, {picksTransformed} counterfeits red-rebel'd. "
                      + $"metadata records: {metaCount}");
        }
        catch (Exception e)
        {
            // never let an audit failure block server startup — log + move on
            logger.Error($"[GoldenPick/Audit] scan failed (server startup continues): {e}");
        }
    }

    // walks an inventory items list (flat — EFT inventories are flat with parent/slot
    // references for hierarchy). returns true if any item was mutated, so the caller
    // knows to persist the profile.
    private bool ScanInventory(List<Item>? items, string sessionId, string label,
                               ref int transformedCount, ref int keptCount)
    {
        if (items == null || items.Count == 0) return false;
        bool changed = false;

        foreach (var item in items)
        {
            if (item.Template != GoldenPickTpl) continue;

            var meta = metaStore.TryGet(item.Id);
            if (meta != null) { keptCount++; continue; }

            // counterfeit — rewrite the template. keep the same Id so any inventory
            // references (parent/slot pointers) stay intact.
            logger.Warning($"[GoldenPick/Audit] {sessionId}/{label}: counterfeit pick id={item.Id} → red rebel");
            item.Template = RedRebelTpl;
            transformedCount++;
            changed = true;
        }

        return changed;
    }
}
