using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Net
{
    // posts to our custom server route so the local SPT server drops a persistent
    // SystemMessage into the players messenger (which also fires SPTs own new-message
    // popup). RequestHandler handles host + session automatically, so the server-side
    // handler gets the right sessionId for free.
    //
    // PostJson is a blocking sync call — run it off the main thread so a slow local
    // request cant hitch the game. fire-and-forget; the mail is a nicety, not critical.
    internal static class ServerMail
    {
        public static void Announce(string message)
        {
            Task.Run(() =>
            {
                try
                {
                    var json = JsonConvert.SerializeObject(new AnnounceBody { message = message });
                    RequestHandler.PostJson("/goldenpick/announce", json);
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogWarning($"[GoldenPick] mail post failed: {e.Message}");
                }
            });
        }

        // lowercase 'message' to match [JsonPropertyName("message")] on the server DTO.
        private class AnnounceBody
        {
            public string message;
        }
    }
}
