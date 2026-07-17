using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-pick metadata (color / name / description / number / owner / signature). lives at
// `<mod-dir>/data/pick_metadata.json`, keyed by pickId. populated by admin grants AND by
// crate unpacks (inherit-pickmeta copies the crate's signature + number to the new pick).
//
// the BepInEx client queries /goldenpick/pickmeta for sheen color + tooltip rendering.
// the file is editable on disk but the embedded Ed25519 public-key verify gates real
// picks from forgeries.
[Injectable(InjectionType.Singleton)]
public class PickMetadataStore(ISptLogger<PickMetadataStore> logger)
{
    // OwnerProfileId is the STABLE identity — SPT sessionId. OwnerNickname is the display
    // label as last observed (refreshed whenever we see a fresh one). LEGACY records from
    // before this split deserialize with OwnerProfileId=null; treat them as orphaned and
    // re-attribute on next interaction using local session info.
    public sealed record PickMetadata(
        string? OwnerProfileId,
        string OwnerNickname,
        long AwardedAt,
        string? SheenColorHex,
        string? CustomName,
        string? CustomDescription,
        int? PickNumber,
        int KillCount = 0);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(PickMetadataStore).Assembly.Location)!,
        "data", "pick_metadata.json");

    private Dictionary<string, PickMetadata>? _records;
    private readonly object _lock = new();

    private Dictionary<string, PickMetadata> Load()
    {
        if (_records != null) return _records;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _records = JsonSerializer.Deserialize<Dictionary<string, PickMetadata>>(json) ?? new();
            }
            else _records = new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] pick metadata load failed, starting fresh: {e.Message}");
            _records = new();
        }
        return _records;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_records,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { logger.Error($"[GoldenPick] pick metadata save failed: {e.Message}"); }
    }

    public void Put(string pickId, PickMetadata rec)
    {
        lock (_lock)
        {
            var map = Load();
            map[pickId] = rec;
            Save();
        }
    }

    // admin-edit update: overwrite ONLY the cosmetic fields (sheen color, name, description,
    // number). owner_nickname, awarded_at, and signature stay as they were at original mint
    // time — those are tied to the pick's signed identity and must not change. returns true
    // if the record existed, false if no metadata was stored under pickId.
    public bool UpdateCosmetics(string pickId, string? sheenColorHex, string? customName, string? customDescription, int? pickNumber)
    {
        lock (_lock)
        {
            var map = Load();
            if (!map.TryGetValue(pickId, out var existing)) return false;
            map[pickId] = existing with
            {
                SheenColorHex     = sheenColorHex,
                CustomName        = customName,
                CustomDescription = customDescription,
                PickNumber        = pickNumber,
            };
            Save();
            return true;
        }
    }

    public PickMetadata? TryGet(string pickId)
    {
        lock (_lock)
        {
            var map = Load();
            return map.TryGetValue(pickId, out var r) ? r : null;
        }
    }

    // owner-gated kill increment. matches on OwnerProfileId when present, falls back to
    // case-insensitive nickname for legacy rows. on match, refreshes owner identity (rename
    // propagation) and returns the new kill count; null if the pick is unknown or not owned
    // by the killer. no rate-limiting — this is a trusted private server.
    public int? IncrementKill(string pickId, string killerProfileId, string killerNickname)
    {
        if (string.IsNullOrEmpty(pickId) || string.IsNullOrEmpty(killerProfileId) || string.IsNullOrEmpty(killerNickname))
            return null;
        lock (_lock)
        {
            var map = Load();
            if (!map.TryGetValue(pickId, out var m)) return null;

            bool owns = !string.IsNullOrEmpty(m.OwnerProfileId)
                ? string.Equals(m.OwnerProfileId, killerProfileId, StringComparison.Ordinal)
                : string.Equals(m.OwnerNickname, killerNickname, StringComparison.OrdinalIgnoreCase);
            if (!owns) return null;

            var updated = m with
            {
                OwnerProfileId = killerProfileId,
                OwnerNickname  = killerNickname,
                KillCount      = m.KillCount + 1,
            };
            map[pickId] = updated;
            Save();
            return updated.KillCount;
        }
    }

    public sealed record LeaderboardEntry(
        string PickId, string? OwnerProfileId, string OwnerNickname, long AwardedAt,
        string? SheenColorHex, string? CustomName, string? CustomDescription,
        int? PickNumber, int KillCount);

    // every registered pick, ordered by kills desc then pick number asc. drives /goldenpick/leaderboard.
    public List<LeaderboardEntry> GetLeaderboard()
    {
        lock (_lock)
        {
            return Load()
                .Select(kv => new LeaderboardEntry(
                    kv.Key, kv.Value.OwnerProfileId, kv.Value.OwnerNickname, kv.Value.AwardedAt,
                    kv.Value.SheenColorHex, kv.Value.CustomName, kv.Value.CustomDescription,
                    kv.Value.PickNumber, kv.Value.KillCount))
                .OrderByDescending(e => e.KillCount)
                .ThenBy(e => e.PickNumber ?? int.MaxValue)
                .ThenBy(e => e.AwardedAt)
                .ToList();
        }
    }
}
