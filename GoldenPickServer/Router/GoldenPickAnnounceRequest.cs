using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// body the in-game client posts to /goldenpick/announce. just the line to show in the
// messenger. IRequestData is what StaticRouter deserializes the body into.
public record GoldenPickAnnounceRequest : IRequestData
{
    [JsonPropertyName("message")]
    public required string Message { get; set; }
}
