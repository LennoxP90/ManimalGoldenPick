using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-crate-id record persisted at <mod-dir>/data/crate_records.json. carries the pick number
// (and award time) from the server-side drop roll through to the client's unpack, and hands out
// the global auto-incremented "Pick #N". no signature: anti-cheat was removed — the server mints
// the crate, so a record's mere existence means "this server minted it" (enough to reject a
// console-spawned crate at unpack).
[Injectable(InjectionType.Singleton)]
public class CrateRecordStore(ISptLogger<CrateRecordStore> logger)
{
    public sealed record CrateRecord(long AwardedAt, string ProfileId, int? PickNumber);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(CrateRecordStore).Assembly.Location)!,
        "data", "crate_records.json");

    private Dictionary<string, CrateRecord>? _records;
    private readonly object _lock = new();

    private Dictionary<string, CrateRecord> Load()
    {
        if (_records != null) return _records;
        try
        {
            _records = File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, CrateRecord>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] crate record load failed, starting fresh: {e.Message}");
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
        catch (Exception e) { logger.Error($"[GoldenPick] crate record save failed: {e.Message}"); }
    }

    public void Record(string crateId, long awardedAt, string profileId, int? pickNumber)
    {
        lock (_lock)
        {
            var map = Load();
            map[crateId] = new CrateRecord(awardedAt, profileId, pickNumber);
            Save();
        }
    }

    public CrateRecord? TryGet(string crateId)
    {
        lock (_lock)
        {
            return Load().TryGetValue(crateId, out var r) ? r : null;
        }
    }

    // global "Pick #N": one more than the max pick number across all recorded crates. lock-held
    // so two concurrent awards can't collide. starts at 1.
    public int NextPickNumber()
    {
        lock (_lock)
        {
            var max = 0;
            foreach (var r in Load().Values)
                if (r.PickNumber is int n && n > max) max = n;
            return max + 1;
        }
    }
}
