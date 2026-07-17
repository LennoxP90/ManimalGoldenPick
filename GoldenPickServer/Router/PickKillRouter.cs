using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// client posts a confirmed golden-pick kill here. owner-gated increment (a player can only
// raise their own pick's count); unknown/not-owned picks return {ok:false}.
[Injectable]
public class PickKillRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<PickKillRouter> logger,
    PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PickKillRequest>(
                "/goldenpick/pick/kill",
                (url, info, sessionId, output) =>
                {
                    var newCount = store.IncrementKill(info.PickId, info.KillerProfileId, info.KillerNickname);
                    if (newCount == null)
                    {
                        logger.Warning($"[GoldenPick] kill rejected pickId={info.PickId} killer={info.KillerNickname} (unknown or not owner)");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"not_owner_or_unknown\"}");
                    }
                    logger.Info($"[GoldenPick] kill pickId={info.PickId} killer={info.KillerNickname} newCount={newCount}");
                    return new ValueTask<string>(JsonSerializer.Serialize(new { ok = true, killCount = newCount }));
                }
            ),
        ]
    ) { }
