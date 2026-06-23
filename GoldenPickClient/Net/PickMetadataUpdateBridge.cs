using System;
using System.Threading.Tasks;
using Manimal.GoldenPick.GoldenPickSheen;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // pick_metadata_update broadcast → forwarded here. two effects:
    //  1) invalidate the local in-memory PickMetadataLookup cache for this pickId so the next
    //     tooltip/sheen render fetches the new values from the server
    //  2) POST to SPT server /goldenpick/update-pickmeta so the on-disk PickMetadataStore.json
    //     is overwritten and survives a server restart
    //
    // unlike grant/redeem flows, this does NOT mint/mail anything — the player already has
    // the pick in their stash; we're just rewriting its cosmetics.
    internal static class PickMetadataUpdateBridge
    {
        public static void ForwardToLocalServer(EarnEvent ev)
        {
            // synchronous cache invalidate first — even if the server POST is in-flight, any
            // immediate re-render will miss the stale cache and re-fetch fresh server values.
            PickMetadataLookup.Invalidate(ev.PickId);

            Task.Run(() =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(new UpdateBody
                    {
                        pickId            = ev.PickId,
                        sheenColorHex     = ev.SheenColorHex,
                        customName        = ev.CustomName,
                        customDescription = ev.CustomDescription,
                        pickNumber        = ev.PickNumber,
                    });
                    var resp = RequestHandler.PostJson("/goldenpick/update-pickmeta", json);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] update-pickmeta forwarded id={ev.PickId} → {resp}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] update-pickmeta forward failed: {e.Message}");
                }
            });
        }

        private class UpdateBody
        {
            public string pickId;
            public string sheenColorHex;
            public string customName;
            public string customDescription;
            public int? pickNumber;
        }
    }
}
