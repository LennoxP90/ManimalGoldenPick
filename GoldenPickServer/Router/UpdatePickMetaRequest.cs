using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// BepInEx forwards this when the relay broadcasts pick_metadata_update. instructs the SPT
// server to OVERWRITE PickMetadataStore's entry for pickId — keeps ownerNickname/awardedAt/
// signature, replaces sheenColorHex/customName/customDescription/pickNumber. survives
// server restart since PickMetadataStore is JSON-backed on disk.
public record UpdatePickMetaRequest : IRequestData
{
    [JsonPropertyName("pickId")]            public required string  PickId            { get; set; }
    [JsonPropertyName("sheenColorHex")]     public string?          SheenColorHex     { get; set; }
    [JsonPropertyName("customName")]        public string?          CustomName        { get; set; }
    [JsonPropertyName("customDescription")] public string?          CustomDescription { get; set; }
    [JsonPropertyName("pickNumber")]        public int?             PickNumber        { get; set; }
}
