using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // submits a confirmed golden-pick kill to the LOCAL SPT server (/goldenpick/pick/kill).
    // fire-and-forget; a rejection (not owner / unknown pick) is logged and dropped — the kill
    // already happened, only the leaderboard increment is missed.
    internal static class PickKillBridge
    {
        public static void Submit(string pickId, string killerProfileId, string killerNickname)
        {
            if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname)) return;
            Task.Run(() =>
            {
                try
                {
                    var body = JsonConvert.SerializeObject(new { pickId, killerProfileId, killerNickname });
                    var resp = RequestHandler.PostJson("/goldenpick/pick/kill", body);
                    Plugin.LogSource?.LogInfo($"[GoldenPick] pick kill recorded ({pickId}): {resp}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] pick kill submit failed: {e.Message}");
                }
            });
        }
    }
}
