using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-profile survived-raid counter. JSON side-channel at <mod-dir>/data/raid_counters.json,
// keyed by profileId (SPT sessionId). replaces the relay's SQLite profile_raids table — the
// data volume (a handful of players) doesn't justify a DB, and this matches the existing
// PickMetadataStore / CrateSignatureStore file-store pattern.
[Injectable(InjectionType.Singleton)]
public class RaidCounterStore(ISptLogger<RaidCounterStore> logger)
{
    public sealed record Counter(int SurvivedCount, string Nickname, long LastUpdated);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(RaidCounterStore).Assembly.Location)!,
        "data", "raid_counters.json");

    private Dictionary<string, Counter>? _counters;
    private readonly object _lock = new();

    private Dictionary<string, Counter> Load()
    {
        if (_counters != null) return _counters;
        try
        {
            _counters = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Counter>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] raid counter load failed, starting fresh: {e.Message}");
            _counters = new();
        }
        return _counters;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_counters,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { logger.Error($"[GoldenPick] raid counter save failed: {e.Message}"); }
    }

    // bump the survived counter for this profile and return the new total (starts at 1).
    public int IncrementSurvived(string profileId, string nickname)
    {
        lock (_lock)
        {
            var map = Load();
            var next = (map.TryGetValue(profileId, out var c) ? c.SurvivedCount : 0) + 1;
            map[profileId] = new Counter(next, nickname, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Save();
            return next;
        }
    }

    public int GetCount(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return 0;
        lock (_lock)
        {
            return Load().TryGetValue(profileId, out var c) ? c.SurvivedCount : 0;
        }
    }
}
