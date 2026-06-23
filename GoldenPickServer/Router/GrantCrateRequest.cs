using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Utils;

namespace GoldenPick.Router;

// BepInEx posts this to local SPT when it receives a relay crate_grant broadcast targeting
// its own nickname. carries the relay-issued crateId + signature so we mint the EXACT item
// the signature is over (using a fresh id would break verification on unpack).
public record GrantCrateRequest : IRequestData
{
    [JsonPropertyName("crateId")]   public required string CrateId   { get; set; }
    [JsonPropertyName("signature")] public required string Signature { get; set; }
    [JsonPropertyName("awardedAt")] public required long   AwardedAt { get; set; }
    // the identifier the RELAY used as the "profileId" in its signature payload
    //   (crate|crateId|profileId|awardedAt). for the test/admin path this is the nickname;
    // for future automated grants it could be anything. stored verbatim so the client can
    // reconstruct the canonical payload at verify time.
    [JsonPropertyName("profileId")]  public required string ProfileId  { get; set; }
    // auto-incremented "Pick #N" carried from the relay's /raid/end response. inherits to
    // the unpacked pick's metadata so the tooltip can show the number.
    [JsonPropertyName("pickNumber")] public int? PickNumber { get; set; }
}
