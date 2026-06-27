using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;

namespace GoldenPick.Audit;

// counterfeit-pick scanner — runs at boot (catches anything that accumulated while the
// server was down) and on every raid-end (catches anything looted off bots or otherwise
// added during the raid). triggered scans only; no periodic loop.
//
// each scan fetches the relay's authoritative list of registered pickIds via /leaderboard,
// then walks the target inventory(ies). any golden pick whose id ISN'T on the relay gets
// its template rewritten to red rebel + the profile is saved. local PickMetadataStore is
// NOT consulted — it only knows about picks THIS player received via grant/unpack, so
// looted-from-bot picks would never appear there. relay knows every legit pick across
// all players → catches looted / spawned / console-given counterfeits regardless of origin.
//
// DEFENSIVE GUARD: if the relay request fails OR returns 0 picks, the scan SKIPS the
// transform pass. treating "no data" as "all counterfeit" would red-rebel legit picks
// during a relay outage or DB wipe.
[Injectable]
public class CounterfeitPickAudit(
    ISptLogger<CounterfeitPickAudit> logger,
    SaveServer saveServer,
    GoldenPickRelayClient relayClient
) : IOnLoad
{
    private const string GoldenPickTpl = "6a371980784a6d8a3ec033ed";
    private const string RedRebelTpl   = "5c0126f40db834002a125382";

    public async Task OnLoad()
    {
        // boot pass — scan every loaded profile once
        await RunScan(sessionIdFilter: null, reason: "boot");
    }

    // public hook called by SurvivedRaidCrateService.EndLocalRaid — scans ONE profile,
    // not all. fire-and-forget on the caller side; this method swallows its own errors.
    public Task ScanProfile(MongoId sessionId) =>
        RunScan(sessionIdFilter: sessionId.ToString(), reason: "raid-end");

    private async Task RunScan(string? sessionIdFilter, string reason)
    {
        try
        {
            var validIds = await relayClient.GetRegisteredPickIds();
            if (validIds == null)
            {
                if (reason == "boot")
                    logger.Warning("[GoldenPick/Audit] relay /leaderboard unreachable — SKIPPING scan to "
                                 + "avoid red-rebel'ing legit picks during a network outage.");
                return;
            }
            if (validIds.Count == 0)
            {
                if (reason == "boot")
                    logger.Warning("[GoldenPick/Audit] relay returned 0 registered picks — SKIPPING scan. "
                                 + "fresh install (fine) OR relay DB wiped (would red-rebel every legit pick).");
                return;
            }

            var profiles = saveServer.GetProfiles();
            int profilesScanned = 0, picksTransformed = 0, picksKept = 0;

            foreach (var (sessionId, profile) in profiles)
            {
                if (sessionIdFilter != null && sessionId.ToString() != sessionIdFilter) continue;
                profilesScanned++;
                bool changed = false;

                changed |= ScanInventory(profile.CharacterData?.PmcData?.Inventory?.Items, validIds, sessionId, "pmc",
                                         ref picksTransformed, ref picksKept);
                changed |= ScanInventory(profile.CharacterData?.ScavData?.Inventory?.Items, validIds, sessionId, "scav",
                                         ref picksTransformed, ref picksKept);

                if (changed)
                {
                    _ = saveServer.SaveProfileAsync(sessionId);
                    logger.Info($"[GoldenPick/Audit] profile {sessionId} saved after transformation");
                }
            }

            if (reason == "boot" || picksTransformed > 0)
                logger.Info($"[GoldenPick/Audit] {reason} scan: {profilesScanned} profile(s), "
                          + $"{picksKept} legit kept, {picksTransformed} counterfeits red-rebel'd "
                          + $"(relay registered: {validIds.Count})");
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick/Audit] {reason} scan failed: {e}");
        }
    }

    private bool ScanInventory(List<Item>? items, HashSet<string> validIds, MongoId sessionId, string label,
                               ref int transformedCount, ref int keptCount)
    {
        if (items == null || items.Count == 0) return false;
        bool changed = false;

        foreach (var item in items)
        {
            if (item.Template != GoldenPickTpl) continue;

            if (validIds.Contains(item.Id)) { keptCount++; continue; }

            // counterfeit — rewrite the template. keep the same Id so any inventory
            // references (parent/slot pointers) stay intact.
            logger.Warning($"[GoldenPick/Audit] {sessionId}/{label}: counterfeit pick id={item.Id} (not on relay) → red rebel");
            item.Template = RedRebelTpl;
            transformedCount++;
            changed = true;
        }

        return changed;
    }
}
