using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Config;

// mod settings loaded once from <mod-dir>/config.json (a default ships in ServerModFiles).
// falls back to built-in defaults if the file is missing or unparseable, so a bad edit can
// never stop the server booting.
//
// NOTE: spt-mod.sh replaces the whole mod folder on update (backing the old one up to
// mod-backups/), so a customised port must be re-applied after a mod update — copy it back
// from the backup or re-edit. port changes are rare, so this is a minor caveat.
[Injectable(InjectionType.Singleton)]
public class GoldenPickConfig
{
    public sealed record Settings
    {
        public bool LeaderboardEnabled { get; init; } = true;
        public int LeaderboardPort { get; init; } = 6970;
    }

    public Settings Current { get; }

    public GoldenPickConfig(ISptLogger<GoldenPickConfig> logger)
    {
        var path = Path.Combine(
            Path.GetDirectoryName(typeof(GoldenPickConfig).Assembly.Location)!,
            "config.json");
        Settings loaded;
        try
        {
            loaded = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(
                      File.ReadAllText(path),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Settings()
                : new Settings();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] config.json load failed, using defaults: {e.Message}");
            loaded = new Settings();
        }
        Current = loaded;
        logger.Info($"[GoldenPick] config: leaderboardEnabled={Current.LeaderboardEnabled} leaderboardPort={Current.LeaderboardPort}");
    }
}
