using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// route the BepInEx client mod hits to ask "did the server mint this crate id, and what pick
// number does it correspond to?" returns the recorded pick number on success or a 404-style
// empty body on miss. the client checks this before letting the crate unpack.
//
// the route never reveals private state — only the pick number, which was already given
// to the player at award time. there's nothing here a determined cheater couldn't read out of
// the JSON file directly.
[Injectable]
public class CrateSignatureRouter(JsonUtil jsonUtil, CrateRecordStore store)
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
                        pickNumber = rec.PickNumber,
                    });
                    return new ValueTask<string>(payload);
                }
            ),
        ]
    ) { }
