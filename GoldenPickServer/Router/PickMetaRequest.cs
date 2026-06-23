using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// BepInEx client polls this when it needs to know a pick's custom metadata (sheen color,
// custom name, etc) — e.g. on sheen activation to decide color override, or on tooltip
// render to inject custom strings.
public record PickMetaRequest : IRequestData
{
    [JsonPropertyName("pickId")] public required string PickId { get; set; }
}
