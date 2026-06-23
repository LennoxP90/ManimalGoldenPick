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
        string Signature,
        string? SheenColorHex,
        string? CustomName,
        string? CustomDescription,
        int? PickNumber);

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

    // total record count — used by the counterfeit audit as a sanity-check before running
    // a destructive transformation pass. zero records means either (a) genuinely no picks
    // have ever been minted on this install, OR (b) the metadata file got wiped/corrupted.
    // we cant tell which from inside the server, so the audit refuses to run on zero.
    public int Count
    {
        get { lock (_lock) { return Load().Count; } }
    }

    // snapshot all records — used by the relay-backfill IOnLoad to push every locally-known
    // pick into the relay's awarded_picks table on server boot. caller iterates the snapshot
    // outside the lock so async work doesnt block other reads/writes.
    public IReadOnlyList<KeyValuePair<string, PickMetadata>> Snapshot()
    {
        lock (_lock)
        {
            return Load().ToList();
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
}
