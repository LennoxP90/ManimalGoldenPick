using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// receives a relay-issued admin pick grant + metadata. mints the actual PICK item (not a
// crate) with the relay's id, persists the metadata, and mails it directly to the player's
// messenger. analogous to GrantCrateRouter but for picks.
//
// the same PreserveGoldenCrateIdPatch covers both crate AND pick template ids — that patch's
// constant list was extended to include the pick tpl so the relay-issued id survives mail.
[Injectable]
public class GrantPickRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<GrantPickRouter> logger,
    PickMetadataStore metaStore,
    MailSendService mailSendService,
    ProfileHelper profileHelper,
    GoldenPickRelayClient relayClient,
    ItemHelper itemHelper)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<GrantPickRequest>(
                "/goldenpick/grant-pick",
                (url, info, sessionId, output) =>
                {
                    try
                    {
                        const string PickTpl = "6a371980784a6d8a3ec033ed";  // matches client GoldenPickConstants.GoldenPickTpl
                        var (ok, tpl) = itemHelper.GetItem(PickTpl);
                        if (!ok || tpl == null)
                        {
                            logger.Error($"[GoldenPick] grant-pick: tpl '{PickTpl}' not in db");
                            return new ValueTask<string>("{\"ok\":false,\"reason\":\"tpl_missing\"}");
                        }

                        var pick = new Item
                        {
                            Id = new MongoId(info.PickId),
                            Template = tpl.Id,
                            Upd = itemHelper.GenerateUpdForItem(tpl),
                        };
                        itemHelper.SetFoundInRaid(new List<Item> { pick });

                        // OwnerProfileId is the LOCAL sessionId — admin only typed a nickname,
                        // but we know now: this server IS the recipient. nickname comes from the
                        // local profile (authoritative) rather than info.OwnerNickname which is
                        // what the admin typed (could be stale or mismatched-case).
                        var pmc = profileHelper.GetPmcProfile(sessionId);
                        var nickname = pmc?.Info?.Nickname ?? info.OwnerNickname;

                        metaStore.Put(info.PickId, new PickMetadataStore.PickMetadata(
                            OwnerProfileId: sessionId.ToString(),
                            OwnerNickname: nickname,
                            AwardedAt: info.AwardedAt,
                            Signature: info.Signature,
                            SheenColorHex: info.SheenColorHex,
                            CustomName: info.CustomName,
                            CustomDescription: info.CustomDescription,
                            PickNumber: info.PickNumber));

                        var msg = info.CustomName != null
                            ? $"You've been granted a special pick — '{info.CustomName}'."
                            : "You've been granted a Golden Ice Pick.";
                        mailSendService.SendSystemMessageToPlayer(sessionId, msg, new List<Item> { pick });

                        // backfill the relay's owner_profile_id (admin grant left it NULL).
                        // fire-and-forget — if it fails the kill-check will fall back to
                        // nickname matching until next sync.
                        _ = relayClient.UpdateOwnerProfile(info.PickId, sessionId.ToString(), nickname);

                        logger.Info($"[GoldenPick] grant-pick mailed to {sessionId} (id={info.PickId} number={info.PickNumber?.ToString() ?? "(none)"} name='{info.CustomName ?? "default"}' owner={nickname})");
                        return new ValueTask<string>("{\"ok\":true}");
                    }
                    catch (Exception e)
                    {
                        logger.Error($"[GoldenPick] grant-pick failed: {e.Message}");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"exception\"}");
                    }
                }
            ),
        ]
    ) { }
