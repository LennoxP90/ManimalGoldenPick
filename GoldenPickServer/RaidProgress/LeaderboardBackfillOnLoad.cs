using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// at SPT server boot, push every locally-stored pick metadata record into the relay's
// awarded_picks table. needed because crate-derived picks unpacked BEFORE the leaderboard
// feature existed are in PickMetadataStore (local) but absent from awarded_picks (relay) —
// without this they wouldnt appear on the public leaderboard until the player happens to
// unpack a NEW crate. registering is idempotent on the relay side so re-running this on
// every boot is fine.
//
// admin-direct picks (granted via /admin/grant-pick) were inserted into awarded_picks at
// grant time — the INSERT OR IGNORE just skips them here. so the backfill only ACTUALLY
// adds rows for crate-derived picks the relay hasn't seen yet.
[Injectable]
public class LeaderboardBackfillOnLoad(
    ISptLogger<LeaderboardBackfillOnLoad> logger,
    PickMetadataStore pickStore,
    GoldenPickRelayClient relayClient
) : IOnLoad
{
    public async Task OnLoad()
    {
        try
        {
            var snapshot = pickStore.Snapshot();
            if (snapshot.Count == 0)
            {
                logger.Info("[GoldenPick/Backfill] no local pick metadata — nothing to push to relay");
                return;
            }

            int registered = 0, skipped = 0, failed = 0;
            foreach (var (pickId, meta) in snapshot)
            {
                // signature is required by the relay endpoint — if its missing locally we
                // can't push (shouldnt happen since grant + inherit both populate it, but
                // defensive log if so)
                if (string.IsNullOrEmpty(meta.Signature) || string.IsNullOrEmpty(meta.OwnerNickname))
                {
                    logger.Warning($"[GoldenPick/Backfill] skipping pickId={pickId}: missing signature/owner");
                    skipped++;
                    continue;
                }

                // OwnerProfileId may be null on legacy records (recorded before the profileId
                // split). pass it through — the relay accepts NULL and will fill it in on
                // the next kill submission via its fallback nickname-match path.
                var ok = await relayClient.RegisterCrateDerivedPick(
                    pickId, meta.OwnerProfileId, meta.OwnerNickname, meta.AwardedAt, meta.Signature, meta.PickNumber);
                if (ok) registered++;
                else failed++;
            }

            logger.Info($"[GoldenPick/Backfill] pushed {registered} pick(s) to relay leaderboard "
                      + $"({skipped} skipped, {failed} failed; total local records: {snapshot.Count}). "
                      + "relay-side INSERT OR IGNORE means already-known picks count as 'registered'.");
        }
        catch (Exception e)
        {
            // never let backfill failure block server startup
            logger.Error($"[GoldenPick/Backfill] failed (server startup continues): {e}");
        }
    }
}
