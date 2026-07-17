using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// public leaderboard JSON — every registered pick ordered by kills. consumed by leaderboard.html
// and any tool using the SPT client HTTP transport.
[Injectable]
public class LeaderboardRouter(JsonUtil jsonUtil, PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<LeaderboardRequest>(
                "/goldenpick/leaderboard",
                (url, info, sessionId, output) =>
                {
                    var picks = store.GetLeaderboard();
                    return new ValueTask<string>(JsonSerializer.Serialize(new { ok = true, picks }));
                }
            ),
        ]
    ) { }
