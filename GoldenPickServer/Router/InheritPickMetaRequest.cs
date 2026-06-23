using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// the BepInEx client POSTs this at unpack BOOM time so the server can copy the consumed
// crate's metadata (pick_number) over to the newly-minted pick's metadata. that's how the
// tooltip layer finds out a crate-derived pick is "Pick #N".
public record InheritPickMetaRequest : IRequestData
{
    [JsonPropertyName("pickId")]        public required string PickId        { get; set; }
    [JsonPropertyName("sourceCrateId")] public required string SourceCrateId { get; set; }
}
