using GoldenPick.RaidProgress;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Utils;

namespace GoldenPick.Router;

// called by the BepInEx client at the unpack BOOM moment, once the new pick item exists.
// looks up the source crate's stored metadata and copies pick_number across into a fresh
// PickMetadataStore record keyed by the new pickId. result: crate-derived picks show up
// in /goldenpick/pickmeta with their auto-incremented "Pick #N" populated.
//
// signature/color/name/description aren't copied — only crates carry signatures (picks
// derived from crates aren't independently signed; their legitimacy is established by the
// crate's signature at unpack time, not by the pick itself). admin-direct picks have their
// own signature populated via /goldenpick/grant-pick.
[Injectable]
public class InheritPickMetaRouter(
    JsonUtil jsonUtil,
    SPTarkov.Server.Core.Models.Utils.ISptLogger<InheritPickMetaRouter> logger,
    CrateSignatureStore crateStore,
    PickMetadataStore pickStore,
    ProfileHelper profileHelper,
    GoldenPickRelayClient relayClient)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<InheritPickMetaRequest>(
                "/goldenpick/inherit-pickmeta",
                (url, info, sessionId, output) =>
                {
                    try
                    {
                        var crate = crateStore.TryGet(info.SourceCrateId);
                        if (crate == null)
                        {
                            logger.Warning($"[GoldenPick] inherit-pickmeta: source crate {info.SourceCrateId} not in store; pick {info.PickId} gets no number");
                            return new ValueTask<string>("{\"ok\":false,\"reason\":\"crate_missing\"}");
                        }

                        // resolve actual nickname from local profile — crate.ProfileId is the
                        // sessionId, not a human-readable name. nickname becomes the display
                        // label everywhere downstream (leaderboard, kill events, etc).
                        var pmc = profileHelper.GetPmcProfile(sessionId);
                        var nickname = pmc?.Info?.Nickname ?? crate.ProfileId;

                        pickStore.Put(info.PickId, new PickMetadataStore.PickMetadata(
                            OwnerProfileId: sessionId.ToString(),  // stable identity
                            OwnerNickname: nickname,               // refreshed display label
                            AwardedAt: crate.AwardedAt,
                            Signature: crate.Signature,            // re-attest by the crate's signature
                            SheenColorHex: null,                   // crate-derived: default hash-color
                            CustomName: null,                      // crate-derived: default name
                            CustomDescription: null,
                            PickNumber: crate.PickNumber));

                        logger.Info($"[GoldenPick] inherit-pickmeta: pick {info.PickId} ← crate {info.SourceCrateId} (#{crate.PickNumber?.ToString() ?? "none"}) owner={nickname}");

                        // ALSO register the pick with the relay so it shows up on the public
                        // leaderboard. fire-and-forget — failure here doesn't break inherit
                        // (local pickmeta is the source of truth for client display; the
                        // relay just hosts the leaderboard summary).
                        _ = relayClient.RegisterCrateDerivedPick(
                            info.PickId, sessionId.ToString(), nickname, crate.AwardedAt, crate.Signature, crate.PickNumber);

                        return new ValueTask<string>("{\"ok\":true}");
                    }
                    catch (Exception e)
                    {
                        logger.Error($"[GoldenPick] inherit-pickmeta failed: {e.Message}");
                        return new ValueTask<string>("{\"ok\":false,\"reason\":\"exception\"}");
                    }
                }
            ),
        ]
    ) { }
