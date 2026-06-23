using System;
using System.Threading.Tasks;
using Manimal.GoldenPick.GoldenPickSheen;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // called at the unpack BOOM (right after the pick is placed in inventory). POSTs the
    // newly-minted pick's id + the consumed crate's id to local SPT — the server copies
    // the crate's stored pick_number (and signature attestation) over to a new pick metadata
    // record, so the tooltip layer finds "Pick #N" for crate-derived picks.
    internal static class InheritPickMetaBridge
    {
        public static void Forward(string pickId, string sourceCrateId)
        {
            if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(sourceCrateId)) return;
            Task.Run(async () =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(new Body
                    {
                        pickId = pickId,
                        sourceCrateId = sourceCrateId,
                    });
                    var resp = RequestHandler.PostJson("/goldenpick/inherit-pickmeta", json);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] inherit-pickmeta forwarded → {resp}");

                    // CRITICAL: the icon strip / tooltip patches may have already queried
                    // pickmeta in the brief window between PlaceGrantedPick and this POST
                    // completing — that early query returned {found:false} and locked a null
                    // into PickMetadataLookup's cache. invalidate the entry so the NEXT
                    // render re-queries the server and picks up the freshly-inherited number.
                    // do it AFTER the POST so the server has the record by the time the cache
                    // miss re-fires. small extra delay covers any straggling render passes.
                    await Task.Delay(50);
                    PickMetadataLookup.Invalidate(pickId);
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] inherit-pickmeta forward failed: {e.Message}");
                }
            });
        }

        private class Body
        {
            public string pickId;
            public string sourceCrateId;
        }
    }
}
