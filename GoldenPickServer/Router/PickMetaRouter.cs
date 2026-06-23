using System.Text.Json;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// returns stored pick metadata for a pickId. BepInEx uses it for sheen color override +
// custom tooltip lines. {found:false} → normal pick, take the default look.
[Injectable]
public class PickMetaRouter(JsonUtil jsonUtil, PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<PickMetaRequest>(
                "/goldenpick/pickmeta",
                (url, info, sessionId, output) =>
                {
                    var r = store.TryGet(info.PickId);
                    if (r == null) return new ValueTask<string>("{\"found\":false}");
                    var payload = JsonSerializer.Serialize(new
                    {
                        found             = true,
                        ownerNickname     = r.OwnerNickname,
                        awardedAt         = r.AwardedAt,
                        signature         = r.Signature,
                        sheenColorHex     = r.SheenColorHex,
                        customName        = r.CustomName,
                        customDescription = r.CustomDescription,
                        pickNumber        = r.PickNumber,
                    });
                    return new ValueTask<string>(payload);
                }
            ),
        ]
    ) { }
