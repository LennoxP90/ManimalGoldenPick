using GoldenPick.Config;
using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Web;

// serves the Golden Ice Pick leaderboard as a browsable web page from inside the SPT server
// process — its own Kestrel host on config.LeaderboardPort, the same "mod hosts its own web
// server" pattern SPT-RaidReview uses. reads the local PickMetadataStore, so there is no
// external service. the page and its JSON are served from the SAME origin, so the fetch is
// same-origin and CORS is a non-issue (permissive CORS is set anyway as belt-and-suspenders).
//
// started on SPT boot as fire-and-forget and fully wrapped: a bind failure (port already in
// use, etc.) logs and is swallowed — it must never block the SPT server from starting.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 3)]
public class LeaderboardWebServer(
    ISptLogger<LeaderboardWebServer> logger,
    GoldenPickConfig config,
    PickMetadataStore store) : IOnLoad
{
    public Task OnLoad()
    {
        var settings = config.Current;
        if (!settings.LeaderboardEnabled)
        {
            logger.Info("[GoldenPick] leaderboard web server disabled via config.json");
            return Task.CompletedTask;
        }

        // run the host off the OnLoad thread so a slow/failed bind never stalls startup
        _ = Task.Run(() => Start(settings.LeaderboardPort));
        return Task.CompletedTask;
    }

    private async Task Start(int port)
    {
        try
        {
            var htmlPath = Path.Combine(
                Path.GetDirectoryName(typeof(LeaderboardWebServer).Assembly.Location)!,
                "leaderboard.html");

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            var app = builder.Build();
            app.UseCors();

            app.MapGet("/", () => ServeHtml(htmlPath));
            app.MapGet("/leaderboard.html", () => ServeHtml(htmlPath));

            // JSON standings — camelCase field names matching what leaderboard.html reads.
            app.MapGet("/leaderboard", () =>
            {
                var picks = store.GetLeaderboard().Select(p => new
                {
                    pickId = p.PickId,
                    ownerProfileId = p.OwnerProfileId,
                    ownerNickname = p.OwnerNickname,
                    awardedAt = p.AwardedAt,
                    sheenColorHex = p.SheenColorHex,
                    customName = p.CustomName,
                    customDescription = p.CustomDescription,
                    pickNumber = p.PickNumber,
                    killCount = p.KillCount,
                });
                return Results.Json(new { ok = true, picks });
            });

            logger.Info($"[GoldenPick] leaderboard web server listening on http://0.0.0.0:{port}/");
            await app.RunAsync();
        }
        catch (Exception e)
        {
            logger.Error($"[GoldenPick] leaderboard web server failed to start on port {port}: {e.Message}");
        }
    }

    private static IResult ServeHtml(string path) =>
        File.Exists(path)
            ? Results.Content(File.ReadAllText(path), "text/html; charset=utf-8")
            : Results.NotFound("leaderboard.html missing from the mod directory");
}
