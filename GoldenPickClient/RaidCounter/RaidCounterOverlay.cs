using System;
using System.Collections;
using System.Net.Http;
using Manimal.GoldenPick.Earn;
using Newtonsoft.Json;
using UnityEngine;

namespace Manimal.GoldenPick.RaidCounter
{
    // top-right debug overlay showing the relay's per-profile survived-raid counter through
    // each 5-raid reward cycle. NO POLLING — state is pushed via WebSocket from the relay's
    // /raid/end handler (broadcast type "raid_result"). Plugin.Update receives the broadcast
    // and calls Notify(...) which mutates the static fields read by OnGUI.
    //
    // display rules:
    //   - shows current cycle position (1..5) based on the relay's monotonic counter
    //   - when newCount % 5 == 0 (a reward raid just hit), shows "5" + result label
    //     (REWARD GRANTED / REWARD NOT GRANTED) for ResultDisplayDuration seconds, then
    //     transitions display to "0" until the next raid bumps it back to "1"
    //   - last-result label below (survived / runner / etc.) so you can see which exits
    //     count vs dont
    //
    // toggled by Plugin.RaidCounterOverlayEnabled config (off by default — its a debug tool).
    internal class RaidCounterOverlay : MonoBehaviour
    {
        private const float ResultDisplayDuration = 6f;

        // pushed from Plugin.Update on raid_result events. static so the GUI in this class
        // can read them without coordinating instances.
        private static int _lastNewCount = -1;
        private static bool _lastAwarded;
        private static string _lastResult = "";
        private static float _resultShownAt = -999f;
        private static bool _hasUnshownResult;

        // one-shot rehydrate on game start — without this the overlay sits at 0 until the
        // first raid completes. polls until ResolveLocalProfileId returns non-empty (the
        // profile resolves a few seconds into the menu), then GETs /raid/state and seeds
        // _lastNewCount so the display matches the relay's authoritative count.
        private static readonly HttpClient _http = new HttpClient();

        private void Start()
        {
            StartCoroutine(RehydrateFromRelay());
        }

        private IEnumerator RehydrateFromRelay()
        {
            // wait up to 60s for the profile to come online — that's the menu/login flow
            float t = 0f;
            string profileId = null;
            while (t < 60f && string.IsNullOrEmpty(profileId))
            {
                try { profileId = GoldenPickEarner.ResolveLocalProfileId(); }
                catch { /* not ready */ }
                if (!string.IsNullOrEmpty(profileId)) break;
                t += 1f;
                yield return new WaitForSeconds(1f);
            }
            if (string.IsNullOrEmpty(profileId))
            {
                Plugin.LogSource?.LogWarning("[GoldenPick] raid counter rehydrate: profileId never resolved, overlay starts at 0");
                yield break;
            }

            // fire the HTTP fetch off the main thread — JsonConvert + HttpClient block.
            // the response just seeds the static fields, which OnGUI reads safely.
            var task = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var url = BuildHttpUrl($"/raid/state?profileId={Uri.EscapeDataString(profileId)}");
                    if (url == null) return;
                    var resp = await _http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) { Plugin.LogSource?.LogWarning($"[GoldenPick] raid/state {(int)resp.StatusCode}"); return; }
                    var body = await resp.Content.ReadAsStringAsync();
                    var parsed = JsonConvert.DeserializeObject<StateResp>(body);
                    if (parsed == null || !parsed.ok) return;
                    // seed the count WITHOUT triggering the "advanced to reward raid" branch —
                    // we just learned the current value, no reward-result animation should fire.
                    _lastNewCount = parsed.survivedCount;
                    _hasUnshownResult = false;
                    Plugin.LogSource?.LogInfo($"[GoldenPick] raid counter rehydrated from relay: {parsed.survivedCount}");
                }
                catch (Exception e) { Plugin.LogSource?.LogWarning($"[GoldenPick] raid counter rehydrate failed: {e.Message}"); }
            });
        }

        // derive http(s) URL from the configured wss relay URL (matches PickKillBridge pattern)
        private static string BuildHttpUrl(string path)
        {
            try
            {
                var ws = Plugin.RelayUrl;
                if (string.IsNullOrEmpty(ws)) return null;
                var http = ws.StartsWith("wss://") ? "https://" + ws.Substring(6)
                         : ws.StartsWith("ws://")  ? "http://"  + ws.Substring(5)
                         : ws;
                if (http.EndsWith("/ws")) http = http.Substring(0, http.Length - 3);
                if (path.StartsWith("/")) path = path.Substring(1);
                return http.TrimEnd('/') + "/" + path;
            }
            catch { return null; }
        }

        private class StateResp
        {
            public bool ok;
            public int survivedCount;
        }

        // called by Plugin.Update when a raid_result broadcast arrives for our nickname
        public static void Notify(int newCount, bool awarded, string lastResult)
        {
            bool advanced = newCount > _lastNewCount;
            bool isRewardRaid = newCount > 0 && newCount % 5 == 0;
            if (advanced && isRewardRaid)
            {
                _resultShownAt = Time.realtimeSinceStartup;
                _hasUnshownResult = true;
            }
            else if (advanced)
            {
                _hasUnshownResult = false;
            }
            _lastNewCount = newCount;
            _lastAwarded = awarded;
            _lastResult = lastResult ?? "";
        }

        private GUIStyle _bigStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _resultGoodStyle;
        private GUIStyle _resultBadStyle;
        private bool _stylesReady;

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _bigStyle = new GUIStyle(GUI.skin.label) { fontSize = 42, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _bigStyle.normal.textColor = new Color(1f, 0.84f, 0f);  // gold
            _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _smallStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            _resultGoodStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _resultGoodStyle.normal.textColor = new Color(0.3f, 1f, 0.3f);
            _resultBadStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _resultBadStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);
            _stylesReady = true;
        }

        private void OnGUI()
        {
            if (Plugin.RaidCounterOverlayEnabled == null || !Plugin.RaidCounterOverlayEnabled.Value) return;
            EnsureStyles();

            string bigText;
            string resultText = "";
            GUIStyle resultStyle = _resultGoodStyle;
            bool inResultWindow = _hasUnshownResult && (Time.realtimeSinceStartup - _resultShownAt) < ResultDisplayDuration;

            if (_lastNewCount < 0)
            {
                bigText = "0";  // nothing pushed yet this session
            }
            else if (inResultWindow)
            {
                bigText = "5";
                resultText = _lastAwarded ? "REWARD GRANTED" : "REWARD NOT GRANTED";
                resultStyle = _lastAwarded ? _resultGoodStyle : _resultBadStyle;
            }
            else if (_hasUnshownResult)
            {
                bigText = "0";  // result window expired
            }
            else
            {
                bigText = _lastNewCount == 0 ? "0" : (((_lastNewCount - 1) % 5) + 1).ToString();
            }

            const float Width  = 220f;
            const float Height = 130f;
            var x = Screen.width - Width - 10f;
            var y = 10f;
            GUI.Box(new Rect(x, y, Width, Height), GUIContent.none);

            GUI.Label(new Rect(x, y + 4f, Width, 22f), "RELAY RAID CYCLE", _smallStyle);
            GUI.Label(new Rect(x, y + 26f, Width, 60f), bigText, _bigStyle);
            if (!string.IsNullOrEmpty(resultText))
                GUI.Label(new Rect(x, y + 86f, Width, 18f), resultText, resultStyle);
            var statusLine = $"last: {(string.IsNullOrEmpty(_lastResult) ? "—" : _lastResult)}  total: {System.Math.Max(_lastNewCount, 0)}";
            GUI.Label(new Rect(x, y + Height - 18f, Width, 16f), statusLine, _smallStyle);
        }
    }
}
