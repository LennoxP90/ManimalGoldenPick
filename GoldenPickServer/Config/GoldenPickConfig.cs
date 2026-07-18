using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Config;

// mod settings, persisted at <SPT>/user/GoldenPick/config.json.
//
// deliberately OUTSIDE the mod folder: spt-mod.sh (and any "replace the mod dir" update flow)
// wipes <SPT>/user/mods/GoldenPickServer/ on update, but never touches <SPT>/user/GoldenPick/,
// so a customised port survives mod updates. seeded with defaults on first boot if absent, and
// falls back to built-in defaults if the file is missing or unparseable so a bad edit can never
// stop the server booting.
[Injectable(InjectionType.Singleton)]
public class GoldenPickConfig
{
    public sealed record Settings
    {
        public bool LeaderboardEnabled { get; init; } = true;
        public int LeaderboardPort { get; init; } = 6970;
    }

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Settings Current { get; }

    public GoldenPickConfig(ISptLogger<GoldenPickConfig> logger)
    {
        // mod DLL lives at <SPT>/user/mods/GoldenPickServer/ — two levels up is <SPT>/user/.
        var modDir = Path.GetDirectoryName(typeof(GoldenPickConfig).Assembly.Location)!;
        var userDir = Path.GetFullPath(Path.Combine(modDir, "..", ".."));
        var path = Path.Combine(userDir, "GoldenPick", "config.json");

        Settings loaded = new();
        try
        {
            if (File.Exists(path))
            {
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), ReadOpts) ?? new Settings();
            }
            else
            {
                // first run — seed the persistent config so it's discoverable + editable
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(loaded, WriteOpts));
                logger.Info($"[GoldenPick] wrote default config to {path}");
            }
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] config load/seed failed at {path}, using defaults: {e.Message}");
            loaded = new Settings();
        }

        Current = loaded;
        logger.Info($"[GoldenPick] config: leaderboardEnabled={Current.LeaderboardEnabled} leaderboardPort={Current.LeaderboardPort} (from {path})");
    }
}
