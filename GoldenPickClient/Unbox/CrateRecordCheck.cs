using System;
using Newtonsoft.Json;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Unbox
{
    // asks the local SPT server whether it minted this crate (POST /goldenpick/cratesig).
    // NOT anti-cheat — there's no signature anymore — just enough to stop a console-spawned
    // crate (no server record) from unpacking into a real pick. fail-open on network error so
    // a transient hiccup never blocks a legitimately-earned crate.
    internal static class CrateRecordCheck
    {
        public static bool IsServerMinted(string crateId)
        {
            if (string.IsNullOrEmpty(crateId)) return false;
            try
            {
                var body = JsonConvert.SerializeObject(new Req { crateId = crateId });
                var resp = RequestHandler.PostJson("/goldenpick/cratesig", body);
                var parsed = JsonConvert.DeserializeObject<Resp>(resp);
                return parsed != null && parsed.found;
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[GoldenPick] crate record check errored ({e.GetType().Name}): {e.Message} — allowing unpack");
                return true; // fail-open
            }
        }

        private class Req { public string crateId; }
        private class Resp { public bool found; public int? pickNumber; }
    }
}
