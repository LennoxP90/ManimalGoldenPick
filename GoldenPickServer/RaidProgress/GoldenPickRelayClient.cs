using System.Net.Http.Json;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// thin HTTP client for the GoldenPick relay's /raid/end endpoint. the relay OWNS the survived-
// raid counter and the drop roll — this just notifies it at raid end and forwards the result.
//
// failure is non-blocking: a network blip means the raid simply doesnt count toward a drop
// (no harm, just a missed tick). we NEVER throw out of here — a relay outage cant be allowed
// to break SPT's raid-end flow.
//
// the relay URL + key match the BepInEx client mod's constants. these arent secrets (anyone
// with the mod has them) so theyre fine to hardcode in both places.
[Injectable(InjectionType.Singleton)]
public class GoldenPickRelayClient(ISptLogger<GoldenPickRelayClient> logger)
{
    private const string RelayBase = "https://goldenpan-relay-manimal.fly.dev";
    private const string RelayKey  = "7355608";
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // fetch every registered pickId from the relay's /leaderboard. used by the counterfeit
    // audit to verify in-stash picks against the authoritative server-side list (vs the
    // local PickMetadataStore which only knows about picks THIS player received). returns
    // null on network error / non-2xx / parse failure — caller MUST treat null as "skip
    // the audit", not "everything is counterfeit" (false positives would red-rebel legit
    // picks during transient relay outages).
    public async Task<HashSet<string>?> GetRegisteredPickIds()
    {
        try
        {
            var resp = await _http.GetAsync($"{RelayBase}/leaderboard");
            if (!resp.IsSuccessStatusCode)
            {
                logger.Warning($"[GoldenPick] /leaderboard returned {(int)resp.StatusCode}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<LeaderboardResponse>();
            if (parsed?.Picks == null) return null;
            var set = new HashSet<string>(parsed.Picks.Count);
            foreach (var p in parsed.Picks) if (!string.IsNullOrEmpty(p.PickId)) set.Add(p.PickId);
            return set;
        }
        catch (Exception e)
        {
            logger.Warning($"[GoldenPick] /leaderboard fetch failed ({e.GetType().Name}): {e.Message}");
            return null;
        }
    }

    // password-based pick redemption — hits relay's /pick/redeem. on success, relay broadcasts
    // a pick_grant which our existing PickGrantBridge picks up and mails. returns the result
    // so the chat command handler can tell the player "success" vs "no match".
    // pushes a crate-derived pick into the relay's awarded_picks table so it shows on the
    // public leaderboard. idempotent on the relay side — calling twice for the same pickId
    // is a no-op. fire-and-forget pattern: returns true on 2xx, false on network/auth fail.
    public async Task<bool> RegisterCrateDerivedPick(
        string pickId, string? ownerProfileId, string ownerNickname, long awardedAt, string signature, int? pickNumber)
    {
        try
        {
            // admin-key gated route — append the key from config to the URL
            var url = $"{RelayBase}/pick/register-crate-derived";
            if (!string.IsNullOrEmpty(RelayKey)) url += "?key=" + Uri.EscapeDataString(RelayKey);
            var body = new RegisterCrateDerivedRequest(pickId, ownerProfileId, ownerNickname, awardedAt, signature, pickNumber);
            var resp = await _http.PostAsJsonAsync(url, body);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Warning($"[GoldenPick] relay /pick/register-crate-derived returned {(int)resp.StatusCode} for pickId={pickId}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            logger.Warning($"[GoldenPick] relay /pick/register-crate-derived failed ({e.GetType().Name}): {e.Message}");
            return false;
        }
    }

    // fills in (or refreshes) the identity columns on an existing pick. used after admin
    // grant delivery when SPT first learns the recipient's profileId — admin only typed
    // nickname into the form, so the relay's awarded_picks row had owner_profile_id=NULL
    // until this call lands.
    public async Task<bool> UpdateOwnerProfile(string pickId, string profileId, string nickname)
    {
        try
        {
            var url = $"{RelayBase}/pick/update-owner-profile";
            if (!string.IsNullOrEmpty(RelayKey)) url += "?key=" + Uri.EscapeDataString(RelayKey);
            var body = new UpdateOwnerProfileRequest(pickId, profileId, nickname);
            var resp = await _http.PostAsJsonAsync(url, body);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Warning($"[GoldenPick] relay /pick/update-owner-profile returned {(int)resp.StatusCode} for pickId={pickId}");
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            logger.Warning($"[GoldenPick] relay /pick/update-owner-profile failed ({e.GetType().Name}): {e.Message}");
            return false;
        }
    }

    public async Task<RedeemResponse?> RedeemPick(string password, string currentNickname, string currentProfileId)
    {
        try
        {
            var url = $"{RelayBase}/pick/redeem";  // no key — password is the access credential
            var req = new RedeemRequest(password, currentNickname, currentProfileId);
            var resp = await _http.PostAsJsonAsync(url, req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new RedeemResponse(false, null, null, null);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Warning($"[GoldenPick] relay /pick/redeem returned {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<RedeemResponse>();
        }
        catch (Exception e)
        {
            logger.Warning($"[GoldenPick] relay /pick/redeem failed ({e.GetType().Name}): {e.Message}");
            return null;
        }
    }

    public async Task<RaidEndResponse?> NotifyRaidEnd(string profileId, string nickname, bool survived, bool runthrough, string lastResult)
    {
        try
        {
            var url = $"{RelayBase}/raid/end?key={Uri.EscapeDataString(RelayKey)}";
            var req = new RaidEndRequest(profileId, nickname, survived, runthrough, lastResult);
            var resp = await _http.PostAsJsonAsync(url, req);
            if (!resp.IsSuccessStatusCode)
            {
                logger.Warning($"[GoldenPick] relay /raid/end returned {(int)resp.StatusCode} — drop opportunity skipped");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<RaidEndResponse>();
        }
        catch (Exception e)
        {
            logger.Warning($"[GoldenPick] relay /raid/end failed ({e.GetType().Name}): {e.Message} — drop opportunity skipped");
            return null;
        }
    }
}

// wire-compatible with the relay's request/response shapes (Program.cs DTOs)
public sealed record RaidEndRequest(
    [property: JsonPropertyName("profileId")]  string ProfileId,
    [property: JsonPropertyName("nickname")]   string Nickname,
    [property: JsonPropertyName("survived")]   bool   Survived,
    [property: JsonPropertyName("runthrough")] bool   Runthrough,
    [property: JsonPropertyName("lastResult")] string LastResult
);

public sealed record CrateAward(
    [property: JsonPropertyName("crateId")]    string CrateId,
    [property: JsonPropertyName("awardedAt")]  long   AwardedAt,
    [property: JsonPropertyName("signature")]  string Signature,
    [property: JsonPropertyName("pickNumber")] int    PickNumber
);

public sealed record RaidEndResponse(
    [property: JsonPropertyName("awarded")]  bool        Awarded,
    [property: JsonPropertyName("crate")]    CrateAward? Crate,
    [property: JsonPropertyName("newCount")] int         NewCount
);

public sealed record RedeemRequest(
    [property: JsonPropertyName("password")]         string Password,
    [property: JsonPropertyName("currentNickname")]  string CurrentNickname,
    [property: JsonPropertyName("currentProfileId")] string CurrentProfileId
);

public sealed record RegisterCrateDerivedRequest(
    [property: JsonPropertyName("pickId")]         string  PickId,
    [property: JsonPropertyName("ownerProfileId")] string? OwnerProfileId,
    [property: JsonPropertyName("ownerNickname")]  string  OwnerNickname,
    [property: JsonPropertyName("awardedAt")]      long    AwardedAt,
    [property: JsonPropertyName("signature")]      string  Signature,
    [property: JsonPropertyName("pickNumber")]     int?    PickNumber
);

public sealed record UpdateOwnerProfileRequest(
    [property: JsonPropertyName("pickId")]    string PickId,
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("nickname")]  string Nickname
);

public sealed record RedeemResponse(
    [property: JsonPropertyName("ok")]         bool    Ok,
    [property: JsonPropertyName("pickId")]     string? PickId,
    [property: JsonPropertyName("customName")] string? CustomName,
    [property: JsonPropertyName("pickNumber")] int?    PickNumber
);

// minimal shape of the relay's /leaderboard response — we only need pickId for the
// counterfeit audit. other fields exist on the wire (ownerNickname, killCount, etc.)
// but we ignore them at the deserializer.
internal sealed record LeaderboardResponse(
    [property: JsonPropertyName("ok")]    bool                       Ok,
    [property: JsonPropertyName("picks")] List<LeaderboardPickEntry>? Picks
);
internal sealed record LeaderboardPickEntry(
    [property: JsonPropertyName("pickId")] string PickId
);
