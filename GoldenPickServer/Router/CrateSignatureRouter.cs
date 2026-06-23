using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// route the BepInEx client mod hits to ask "is this crate id legitimately relay-signed?"
// returns the signature record on success or a 404-style empty body on miss. the client
// then verifies the signature locally against the Ed25519 public key embedded in its source.
//
// the route never reveals private state — only the stored signature, which was already given
// to the player at award time. there's nothing here a determined cheater couldn't read out of
// the JSON file directly.
[Injectable]
public class CrateSignatureRouter(JsonUtil jsonUtil, CrateSignatureStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<CrateSignatureRequest>(
                "/goldenpick/cratesig",
                (url, info, sessionId, output) =>
                {
                    var rec = store.TryGet(info.CrateId);
                    if (rec == null) return new ValueTask<string>("{\"found\":false}");
                    var payload = JsonSerializer.Serialize(new
                    {
                        found = true,
                        signature = rec.Signature,
                        awardedAt = rec.AwardedAt,
                        profileId = rec.ProfileId,
                    });
                    return new ValueTask<string>(payload);
                }
            ),
        ]
    ) { }
