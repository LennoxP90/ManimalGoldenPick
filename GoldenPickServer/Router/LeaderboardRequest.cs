using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// no input fields — the leaderboard is a full snapshot. present so StaticRouter's RouteAction<T>
// has a body type to bind.
public record LeaderboardRequest : IRequestData { }
