using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Config;

// mod settings, at the SPT-standard in-mod-folder location <mod>/config/config.json (a default
// also ships in the release). seeded with defaults on first boot if absent, and falls back to
// built-in defaults if the file is missing or unparseable so a bad edit can never stop the
// server booting.
//
// NOTE on updates: a full mod-folder-replace updater (e.g. spt-mod.sh) would normally reset
// this to the shipped default — the accompanying spt-mod.sh patch preserves the existing
// config/ folder across updates so a customised port survives (use its --fresh-config flag to
// force the shipped defaults after a config-schema change).
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
        var modDir = Path.GetDirectoryName(typeof(GoldenPickConfig).Assembly.Location)!;
        var path = Path.Combine(modDir, "config", "config.json");

        Settings loaded = new();
        try
        {
            if (File.Exists(path))
            {
                loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), ReadOpts) ?? new Settings();
            }
            else
            {
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
        logger.Info($"[GoldenPick] config: leaderboardEnabled={Current.LeaderboardEnabled} leaderboardPort={Current.LeaderboardPort}");
    }
}
