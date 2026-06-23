using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // when the relay broadcasts a crate_grant event matching our nickname, the BepInEx client
    // forwards it here. we POST the award to the local SPT server's /goldenpick/grant-crate
    // route, which mints the actual Item and mails it. all signature storage stays server-side.
    internal static class CrateGrantBridge
    {
        public static void ForwardToLocalServer(EarnEvent ev)
        {
            // off the main thread — PostJson is blocking
            Task.Run(() =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(new GrantBody
                    {
                        crateId   = ev.CrateId,
                        signature = ev.Signature,
                        awardedAt = ev.AwardedAt,
                        // ev.Player is the value the relay signed with (nickname for the
                        // test path). pass it through verbatim so the SPT server stores the
                        // exact "profileId" component of the signature payload.
                        profileId  = ev.Player,
                        pickNumber = ev.PickNumber,
                    });
                    var resp = RequestHandler.PostJson("/goldenpick/grant-crate", json);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] grant-crate forwarded → {resp}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] grant-crate forward failed: {e.Message}");
                }
            });
        }

        // lowercase to match server DTO JsonPropertyName attrs
        private class GrantBody
        {
            public string crateId;
            public string signature;
            public long awardedAt;
            public string profileId;
            public int? pickNumber;
        }
    }
}
