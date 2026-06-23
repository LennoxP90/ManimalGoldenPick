using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace Manimal.GoldenPick.GoldenPickSheen
{
    // looks up custom pick metadata from the local SPT server (POST /goldenpick/pickmeta).
    // returns null (no metadata) for picks that weren't admin-granted — those still take the
    // deterministic hash color via SheenColors.ForItemId.
    //
    // cached per-id forever-in-session (metadata never changes for a given pickId, and the
    // sheen activate path doesnt happen often enough to warrant cache expiry). polling cost
    // is paid ONCE per pick the player encounters.
    internal static class PickMetadataLookup
    {
        public sealed class Metadata
        {
            public string SheenColorHex;
            public string CustomName;
            public string CustomDescription;
            public int? PickNumber;
        }

        // null-cached when the server says found:false, so we dont re-query every activate
        private static readonly Dictionary<string, Metadata> _cache = new Dictionary<string, Metadata>();
        private static readonly object _lock = new object();

        public static Metadata GetOrNull(string pickId)
        {
            if (string.IsNullOrEmpty(pickId)) return null;
            lock (_lock)
            {
                if (_cache.TryGetValue(pickId, out var hit)) return hit;
            }

            try
            {
                var body = JsonConvert.SerializeObject(new MetaReq { pickId = pickId });
                var resp = RequestHandler.PostJson("/goldenpick/pickmeta", body);
                var parsed = JsonConvert.DeserializeObject<MetaResp>(resp);
                Metadata m = null;
                if (parsed != null && parsed.found)
                {
                    m = new Metadata
                    {
                        SheenColorHex     = parsed.sheenColorHex,
                        CustomName        = parsed.customName,
                        CustomDescription = parsed.customDescription,
                        PickNumber        = parsed.pickNumber,
                    };
                }
                lock (_lock) { _cache[pickId] = m; }  // cache hit OR null
                return m;
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[GoldenPick] pickmeta lookup failed for {pickId}: {e.Message}");
                return null;
            }
        }

        // evict a specific id from the cache so the next GetOrNull re-queries the server.
        // called from the pick_metadata_update WS handler when admin edits a pick's color/name/etc.
        public static void Invalidate(string pickId)
        {
            if (string.IsNullOrEmpty(pickId)) return;
            lock (_lock) { _cache.Remove(pickId); }
        }

        // parses "#RRGGBB" or "RRGGBB" hex strings. returns true on success.
        public static bool TryParseHexColor(string hex, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(hex)) return false;
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length != 6) return false;
            try
            {
                var r = Convert.ToInt32(hex.Substring(0, 2), 16);
                var g = Convert.ToInt32(hex.Substring(2, 2), 16);
                var b = Convert.ToInt32(hex.Substring(4, 2), 16);
                color = new Color(r / 255f, g / 255f, b / 255f, 1f);
                return true;
            }
            catch { return false; }
        }

        private class MetaReq { public string pickId; }
        private class MetaResp
        {
            public bool found;
            public string sheenColorHex;
            public string customName;
            public string customDescription;
            public int? pickNumber;
        }
    }
}
