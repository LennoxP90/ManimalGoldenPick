using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// receives a relay-issued crate award + signature and materializes it in the player's mailbox.
// the BepInEx client funnels relay /ws crate_grant broadcasts here when they match the local
// nickname — so the actual minting + signature persistence still runs server-side (the client
// never touches CrateSignatureStore, never owns the signing surface).
//
// effectively the same flow as SurvivedRaidCrateService.GrantCrate, just triggered by a manual
// admin button instead of a raid-end roll. behaves identically downstream.
[Injectable]
public class GrantCrateRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<GrantCrateRouter> logger,
    CrateSignatureStore signatureStore,
    MailSendService mailSendService,
    ItemHelper itemHelper)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<GrantCrateRequest>(
                "/goldenpick/grant-crate",
                (url, info, sessionId, output) =>
                {
                    try
                    {
                        const string CrateTpl = "9c2f1a0b7e6d4c83a5f10b2e";
                        var (ok, tpl) = itemHelper.GetItem(CrateTpl);
                        if (!ok || tpl == null)
                        {
                            logger.Error($"[GoldenPick] grant-crate: tpl '{CrateTpl}' not in db");
                            return new ValueTask<string>("{\"ok\":false,\"reason\":\"tpl_missing\"}");
                        }

                        var crate = new Item
                        {
                            Id = new MongoId(info.CrateId),
                            Template = tpl.Id,
                            Upd = itemHelper.GenerateUpdForItem(tpl),
                        };
                        itemHelper.SetFoundInRaid(new List<Item> { crate });

                        // store the relay-supplied profileId verbatim (NOT the local sessionId).
                        // it's what the relay signed with, so it's what the client needs to
                        // reconstruct the canonical payload at verify time. pick_number rides
                        // along too — propagates to the unpacked pick.
                        signatureStore.Record(info.CrateId, info.Signature, info.AwardedAt, info.ProfileId, info.PickNumber);
                        mailSendService.SendSystemMessageToPlayer(
                            sessionId,
                            "A Sealed Golden Crate has appeared! Unpack it to see what's inside.",
                            new List<Item> { crate });

                        logger.Info($"[GoldenPick] grant-crate mailed to {sessionId} (id={info.CrateId})");
                        return new ValueTask<string>("{\"ok\":true}");
                    }
                    catch (Exception e)
                    {
                        logger.Error($"[GoldenPick] grant-crate failed: {e.Message}");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"exception\"}");
                    }
                }
            ),
        ]
    ) { }
