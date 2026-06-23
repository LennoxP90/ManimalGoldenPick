using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Manimal.GoldenPick.Net
{
    // submits a confirmed golden-pick kill to the relay. fire-and-forget; if the relay rejects
    // (owner mismatch or unknown pick) we log + move on — the kill already happened, the
    // statue effect already played, the only thing missing is the leaderboard increment.
    //
    // POSTs directly to relay HTTP (NOT via the local SPT server) — analogous to how
    // /raid/end works, but client-side here because the kill event ONLY fires inside the
    // game client where Player.OnPlayerDeadStatic raises.
    internal static class PickKillBridge
    {
        private static readonly HttpClient _http = new HttpClient();

        public static void Submit(string pickId, string killerProfileId, string killerNickname)
        {
            if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname)) return;
            Task.Run(async () =>
            {
                try
                {
                    var url = BuildHttpUrl("/pick/kill");
                    if (url == null) return;
                    var body = JsonConvert.SerializeObject(new { pickId, killerProfileId, killerNickname });
                    var resp = await _http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                    var respText = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode)
                        Plugin.LogSource?.LogInfo($"[GoldenPick] pick kill recorded ({pickId}): {respText}");
                    else
                        Plugin.LogSource?.LogWarning($"[GoldenPick] pick kill rejected ({(int)resp.StatusCode}): {respText}");
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] pick kill submit failed: {e.Message}");
                }
            });
        }

        // derive HTTP URL from the configured WS relay URL — same host, http/https scheme.
        // matches the Plugin.RelayUrl format (wss://host/ws).
        private static string BuildHttpUrl(string path)
        {
            try
            {
                var ws = Plugin.RelayUrl;
                if (string.IsNullOrEmpty(ws)) return null;
                var http = ws.StartsWith("wss://") ? "https://" + ws.Substring(6)
                         : ws.StartsWith("ws://")  ? "http://"  + ws.Substring(5)
                         : ws;
                // strip the /ws suffix if present
                if (http.EndsWith("/ws")) http = http.Substring(0, http.Length - 3);
                if (path.StartsWith("/")) path = path.Substring(1);
                return http.TrimEnd('/') + "/" + path;
            }
            catch { return null; }
        }
    }
}
