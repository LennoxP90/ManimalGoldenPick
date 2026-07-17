using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

public record PickKillRequest : IRequestData
{
    [JsonPropertyName("pickId")]          public required string PickId          { get; set; }
    [JsonPropertyName("killerProfileId")] public required string KillerProfileId { get; set; }
    [JsonPropertyName("killerNickname")]  public required string KillerNickname  { get; set; }
}
