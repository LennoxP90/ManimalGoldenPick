using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Manimal.GoldenPick.Net
{
    // persistent websocket link to the global relay. holds the connection on a
    // background task, auto-reconnects with capped backoff, and drops inbound events
    // into a thread-safe queue that Plugin.Update drains on the unity main thread
    // (DisplayMessageNotification must run there). sends are fire-and-forget — if were
    // not connected the event is just dropped, since the relay only matters live.
    internal class RelayClient
    {
        private readonly string _url;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private readonly ConcurrentQueue<EarnEvent> _inbound = new ConcurrentQueue<EarnEvent>();
        private volatile bool _running;

        public RelayClient(string url) => _url = url;

        public bool TryDequeue(out EarnEvent ev) => _inbound.TryDequeue(out ev);

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => ConnectLoop(_cts.Token));
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
        }

        public void SendEarn(EarnEvent ev)
        {
            Task.Run(async () =>
            {
                try
                {
                    var ws = _ws;
                    if (ws == null || ws.State != WebSocketState.Open) return;
                    var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(ev));
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] relay send failed: {e.Message}");
                }
            });
        }

        private async Task ConnectLoop(CancellationToken token)
        {
            int backoff = 2;
            while (_running && !token.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri(_url), token);
                    Plugin.LogSource?.LogInfo("[GoldenPick] relay connected");
                    backoff = 2;
                    await ReceiveLoop(_ws, token);
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] relay connect failed: {e.Message}");
                }

                if (!_running) break;
                // capped exponential backoff between reconnect attempts
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), token); } catch { }
                backoff = Math.Min(backoff * 2, 30);
            }
        }

        private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                try
                {
                    var ev = JsonConvert.DeserializeObject<EarnEvent>(sb.ToString());
                    if (ev == null || string.IsNullOrEmpty(ev.Player)) continue;
                    // Plugin.Update switches on Type when draining — accept all known types
                    if (ev.Type == "pick_earned" || ev.Type == "crate_grant"
                        || ev.Type == "raid_result" || ev.Type == "pick_grant"
                        || ev.Type == "pick_metadata_update")
                        _inbound.Enqueue(ev);
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] bad relay message: {e.Message}");
                }
            }
        }
    }
}
