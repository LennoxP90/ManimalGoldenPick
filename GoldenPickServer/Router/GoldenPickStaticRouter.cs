using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// custom route the in-game client hits when a pan is earned — its own, or one heard
// over the relay. drops a persistent SystemMessage into the player's messenger, which
// ALSO fires SPT's built-in new-message popup. [Injectable] means SPT's DI scan
// auto-registers it (same as give-ui's router) — no manual wiring in Mod.cs.
[Injectable]
public class GoldenPickStaticRouter(JsonUtil jsonUtil, MailSendService mailSendService)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<GoldenPickAnnounceRequest>(
                "/goldenpick/announce",
                (url, info, sessionId, output) =>
                {
                    // SendSystemMessageToPlayer is synchronous; wrap the ack in a
                    // completed ValueTask to satisfy the route delegate without async.
                    mailSendService.SendSystemMessageToPlayer(sessionId, info.Message, null);
                    return new ValueTask<string>("{\"success\":true}");
                }
            ),
        ]
    ) { }
