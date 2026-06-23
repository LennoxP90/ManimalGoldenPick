using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// BepInEx posts this when it receives a relay pick_grant broadcast for the local nickname.
// carries the full metadata from the relay; the server mints + mails the pick with the
// relay-issued id, then persists the metadata in PickMetadataStore for client lookup.
public record GrantPickRequest : IRequestData
{
    [JsonPropertyName("pickId")]            public required string  PickId            { get; set; }
    [JsonPropertyName("signature")]         public required string  Signature         { get; set; }
    [JsonPropertyName("awardedAt")]         public required long    AwardedAt         { get; set; }
    [JsonPropertyName("ownerNickname")]     public required string  OwnerNickname     { get; set; }
    [JsonPropertyName("sheenColorHex")]     public string? SheenColorHex     { get; set; }
    [JsonPropertyName("customName")]        public string? CustomName        { get; set; }
    [JsonPropertyName("customDescription")] public string? CustomDescription { get; set; }
    [JsonPropertyName("pickNumber")]        public int?    PickNumber        { get; set; }
}
