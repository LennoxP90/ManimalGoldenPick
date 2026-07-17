using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// called by the client at the unpack moment. copies the source crate's pick_number into a fresh
// PickMetadataStore record keyed by the new pickId, which ALSO registers the pick on the local
// leaderboard (GetLeaderboard enumerates the store). crate-derived picks get default cosmetics.
[Injectable]
public class InheritPickMetaRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<InheritPickMetaRouter> logger,
    CrateRecordStore crateStore,
    PickMetadataStore pickStore,
    ProfileHelper profileHelper)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<InheritPickMetaRequest>(
                "/goldenpick/inherit-pickmeta",
                (url, info, sessionId, output) =>
                {
                    try
                    {
                        var crate = crateStore.TryGet(info.SourceCrateId);
                        if (crate == null)
                        {
                            logger.Warning($"[GoldenPick] inherit-pickmeta: source crate {info.SourceCrateId} not in store; pick {info.PickId} gets no number");
                            return new ValueTask<string>("{\"ok\":false,\"reason\":\"crate_missing\"}");
                        }

                        var pmc = profileHelper.GetPmcProfile(sessionId);
                        var nickname = pmc?.Info?.Nickname ?? crate.ProfileId;

                        pickStore.Put(info.PickId, new PickMetadataStore.PickMetadata(
                            OwnerProfileId: sessionId.ToString(),
                            OwnerNickname: nickname,
                            AwardedAt: crate.AwardedAt,
                            SheenColorHex: null,
                            CustomName: null,
                            CustomDescription: null,
                            PickNumber: crate.PickNumber));

                        logger.Info($"[GoldenPick] inherit-pickmeta: pick {info.PickId} ← crate {info.SourceCrateId} (#{crate.PickNumber?.ToString() ?? "none"}) owner={nickname}");
                        return new ValueTask<string>("{\"ok\":true}");
                    }
                    catch (Exception e)
                    {
                        logger.Error($"[GoldenPick] inherit-pickmeta failed: {e.Message}");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"exception\"}");
                    }
                }
            ),
        ]
    ) { }
