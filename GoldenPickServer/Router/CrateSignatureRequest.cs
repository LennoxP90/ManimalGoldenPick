using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// the BepInEx client posts {"crateId":"..."} when unpack is attempted. response is the
// stored signature record (or empty/missing if the crate isnt a relay-signed legitimate one).
public record CrateSignatureRequest : IRequestData
{
    [JsonPropertyName("crateId")]
    public required string CrateId { get; set; }
}
