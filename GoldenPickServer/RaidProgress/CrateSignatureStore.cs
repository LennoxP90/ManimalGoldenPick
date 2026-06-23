using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.RaidProgress;

// per-crate-id signature persistence. Item.Upd has a sealed schema (no arbitrary mod fields),
// so we keep signatures in a side-channel JSON file and the BepInEx client mod queries this
// server (NOT the relay) for the signature when it tries to unpack a crate.
//
// "open source = users can edit the file" is fine here because the signature is verified
// against the relay's Ed25519 public key — a forged entry won't pass cryptographic verification,
// so the only thing tampering buys you is corruption. real picks (relay-signed) verify; spawned/
// faked ones don't.
//
// path is `<mod-dir>/data/crate_signatures.json`. mod-dir is where the DLL sits, which on
// SPT is `user/mods/GoldenPickServer/`.
[Injectable(InjectionType.Singleton)]
public class CrateSignatureStore(ISptLogger<CrateSignatureStore> logger)
{
    public sealed record SignatureRecord(string Signature, long AwardedAt, string ProfileId, int? PickNumber);

    private readonly string _path = Path.Combine(
        Path.GetDirectoryName(typeof(CrateSignatureStore).Assembly.Location)!,
        "data", "crate_signatures.json");

    private Dictionary<string, SignatureRecord>? _sigs;
    private readonly object _lock = new();

    private Dictionary<string, SignatureRecord> Load()
    {
        if (_sigs != null) return _sigs;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _sigs = JsonSerializer.Deserialize<Dictionary<string, SignatureRecord>>(json) ?? new();
            }
            else _sigs = new();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] signature store load failed, starting fresh: {e.Message}");
            _sigs = new();
        }
        return _sigs;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sigs,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { logger.Error($"[GoldenPick] signature store save failed: {e.Message}"); }
    }

    public void Record(string crateId, string signature, long awardedAt, string profileId, int? pickNumber)
    {
        lock (_lock)
        {
            var map = Load();
            map[crateId] = new SignatureRecord(signature, awardedAt, profileId, pickNumber);
            Save();
        }
    }

    public SignatureRecord? TryGet(string crateId)
    {
        lock (_lock)
        {
            var map = Load();
            return map.TryGetValue(crateId, out var s) ? s : null;
        }
    }
}
