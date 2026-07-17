using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Generators;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using ISptLogger = SPTarkov.Server.Core.Models.Utils.ISptLogger<SPTarkov.Server.Core.Services.LocationLifecycleService>;

namespace GoldenPick.RaidProgress;

// hook for "the player completed a raid" — subclass of LocationLifecycleService that overrides
// EndLocalRaid and rolls the drop AFTER the base implementation saves the raid result.
//
// THIS SERVICE OWNS EVERYTHING NOW: counter, drop roll, crate minting. no external relay is
// consulted — the survived-raid count lives in RaidCounterStore, the roll decision in
// DropOracle, and the awarded crate's pick number in CrateRecordStore (the BepInEx client
// later queries that store via /goldenpick/cratesig to confirm the crate was server-minted
// before unpack).
//
// why a subclass and not Harmony: SPT's DI resolves services by TypePriority. a higher-
// priority subclass of the same base wins resolution — every consumer of LocationLifecycleService
// transparently uses ours. the 26 base deps are passed straight through; we add our own
// counter-store + crate-record-store + mail/item-helper for the grant.
// TypeOverride is the ACTUAL DI mechanism for replacing a base service — SPT scans for
// attributes with TypeOverride set and removes the named type from the registration set
// BEFORE injecting. without this we and the base both registered; consumers (MatchController)
// ended up resolving the base one and our override never fired, so raid-end never reached
// this service. TypePriority alone is just a sort order, not an override directive.
[Injectable(InjectionType.Singleton, typeOverride: typeof(LocationLifecycleService))]
public class SurvivedRaidCrateService(
    // --- our own deps ---
    SPTarkov.Server.Core.Models.Utils.ISptLogger<SurvivedRaidCrateService> ourLogger,
    RaidCounterStore raidCounters,
    CrateRecordStore crateStore,
    MailSendService mailSendService,
    ItemHelper itemHelper,
    // --- base service deps, passed straight through ---
    ISptLogger logger,
    RewardHelper rewardHelper,
    ConfigServer configServer,
    TimeUtil timeUtil,
    DatabaseService databaseService,
    ProfileHelper profileHelper,
    BackupService backupService,
    ProfileActivityService profileActivityService,
    BotNameService botNameService,
    ICloner cloner,
    RaidTimeAdjustmentService raidTimeAdjustmentService,
    LocationLootGenerator locationLootGenerator,
    ServerLocalisationService serverLocalisationService,
    BotLootCacheService botLootCacheService,
    LootGenerator lootGenerator,
    TraderHelper traderHelper,
    RandomUtil randomUtil,
    InRaidHelper inRaidHelper,
    PlayerScavGenerator playerScavGenerator,
    SaveServer saveServer,
    HealthHelper healthHelper,
    PmcChatResponseService pmcChatResponseService,
    PmcWaveGenerator pmcWaveGenerator,
    QuestHelper questHelper,
    InsuranceService insuranceService,
    MatchBotDetailsCacheService matchBotDetailsCacheService,
    BtrDeliveryService btrDeliveryService
) : LocationLifecycleService(
    logger, rewardHelper, configServer, timeUtil, databaseService, profileHelper,
    backupService, profileActivityService, botNameService, cloner, raidTimeAdjustmentService,
    locationLootGenerator, serverLocalisationService, botLootCacheService, lootGenerator,
    mailSendService, traderHelper, randomUtil, inRaidHelper, playerScavGenerator, saveServer,
    healthHelper, pmcChatResponseService, pmcWaveGenerator, questHelper, insuranceService,
    matchBotDetailsCacheService, btrDeliveryService)
{
    private const string CrateTpl = "9c2f1a0b7e6d4c83a5f10b2e";
    private const string MailMessage = "A Sealed Golden Crate has appeared! Unpack it to see what's inside.";

    public override void EndLocalRaid(MongoId sessionId, EndLocalRaidRequestData request)
    {
        // let SPT do all its normal raid-end processing first — quests, insurance, profile save.
        // base finishing writes raid loot (including anything looted off bots) into the profile
        // inventory, which is exactly what we want the audit to see.
        base.EndLocalRaid(sessionId, request);

        // fire-and-forget the local roll. await it inside so any exception cant crash
        // the SPT raid-end flow, but DON'T let the async work block the caller.
        _ = RollAndMaybeMail(sessionId, request);
    }

    private async Task RollAndMaybeMail(MongoId sessionId, EndLocalRaidRequestData request)
    {
        await Task.Yield(); // keep the async signature; work below is synchronous + cheap
        try
        {
            var survived   = request.Results?.Result == ExitStatus.SURVIVED;
            var runthrough = request.Results?.Result == ExitStatus.RUNNER;
            if (!survived || runthrough) return;

            var pmc = profileHelper.GetPmcProfile(sessionId);
            var nickname = pmc?.Info?.Nickname ?? "An operative";

            var newCount = raidCounters.IncrementSurvived(sessionId.ToString(), nickname);
            ourLogger.Info($"[GoldenPick] survived raid — count={newCount}");

            if (!DropOracle.ShouldAward(newCount, () => Random.Shared.NextDouble())) return;

            // WIN: mint a crate id, assign the next global pick number, record + mail.
            var crateId   = MintMongoId();
            var awardedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var pickNumber = crateStore.NextPickNumber();
            crateStore.Record(crateId, awardedAt, sessionId.ToString(), pickNumber);
            GrantCrate(sessionId, crateId);
            ourLogger.Info($"[GoldenPick] AWARD count={newCount} crate={crateId} pick#={pickNumber}");
        }
        catch (Exception e) { ourLogger.Error($"[GoldenPick] post-raid roll failed: {e.Message}"); }
    }

    // 24-char lowercase hex, EFT MongoId shape
    private static string MintMongoId()
    {
        var bytes = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void GrantCrate(MongoId sessionId, string crateId)
    {
        try
        {
            var (ok, tpl) = itemHelper.GetItem(CrateTpl);
            if (!ok || tpl == null) { ourLogger.Error($"[GoldenPick] crate tpl '{CrateTpl}' not in db — cant mail"); return; }

            var crate = new Item
            {
                Id = new MongoId(crateId),
                Template = tpl.Id,
                Upd = itemHelper.GenerateUpdForItem(tpl),
            };
            itemHelper.SetFoundInRaid(new List<Item> { crate });
            mailSendService.SendSystemMessageToPlayer(sessionId, MailMessage, new List<Item> { crate });
            ourLogger.Info($"[GoldenPick] crate mailed to {sessionId} (id={crateId})");
        }
        catch (Exception e) { ourLogger.Error($"[GoldenPick] crate grant failed: {e.Message}"); }
    }
}
