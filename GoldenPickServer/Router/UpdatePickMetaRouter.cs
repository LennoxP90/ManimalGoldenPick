using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// BepInEx forwards pick_metadata_update broadcasts here. overwrites the cosmetic fields
// on an existing PickMetadataStore entry (owner / awarded / signature untouched). returns
// {ok: true} on success, {ok: false} if no record existed for the pickId (which would
// suggest a desync between relay and SPT — relay knows about the pick but server doesn't).
[Injectable]
public class UpdatePickMetaRouter(JsonUtil jsonUtil, PickMetadataStore store)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<UpdatePickMetaRequest>(
                "/goldenpick/update-pickmeta",
                (url, info, sessionId, output) =>
                {
                    var ok = store.UpdateCosmetics(
                        info.PickId, info.SheenColorHex, info.CustomName, info.CustomDescription, info.PickNumber);
                    return new ValueTask<string>(ok ? "{\"ok\":true}" : "{\"ok\":false,\"reason\":\"no record\"}");
                }
            ),
        ]
    ) { }
