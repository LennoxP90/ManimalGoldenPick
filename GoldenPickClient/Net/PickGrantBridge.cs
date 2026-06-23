using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // when the relay broadcasts a pick_grant event matching our nickname, the BepInEx client
    // forwards it here. POSTs the full pick metadata (id, signature, sheen color, custom
    // name/description, pick number) to the local SPT server's /goldenpick/grant-pick route,
    // which mints + mails the pick + persists the metadata for later lookup.
    //
    // mirror of CrateGrantBridge but with the richer metadata payload.
    internal static class PickGrantBridge
    {
        public static void ForwardToLocalServer(EarnEvent ev)
        {
            Task.Run(() =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(new GrantBody
                    {
                        pickId            = ev.PickId,
                        signature         = ev.Signature,
                        awardedAt         = ev.AwardedAt,
                        ownerNickname     = ev.Player,
                        sheenColorHex     = ev.SheenColorHex,
                        customName        = ev.CustomName,
                        customDescription = ev.CustomDescription,
                        pickNumber        = ev.PickNumber,
                    });
                    var resp = RequestHandler.PostJson("/goldenpick/grant-pick", json);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] grant-pick forwarded → {resp}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] grant-pick forward failed: {e.Message}");
                }
            });
        }

        // matches GrantPickRequest JsonPropertyName casing
        private class GrantBody
        {
            public string pickId;
            public string signature;
            public long awardedAt;
            public string ownerNickname;
            public string sheenColorHex;
            public string customName;
            public string customDescription;
            public int? pickNumber;
        }
    }
}
