using System;
using System.Text;
using Newtonsoft.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SPT.Common.Http;

namespace Manimal.GoldenPick.Unbox
{
    // verifies a crate's relay-issued Ed25519 signature before the unpack flow runs. logic:
    //   1. ask local SPT server for the crate's stored signature record (POST /goldenpick/cratesig)
    //   2. reconstruct the canonical payload string `crate|{crateId}|{profileId}|{awardedAt}`
    //   3. Ed25519-verify the signature with our embedded public key
    //   4. return true only on full success — any missing piece OR a bad signature → counterfeit
    //
    // the public key is hardcoded below. NOT a secret — public keys never are. forging requires
    // the relay's private key (Fly.io secret); without it, no spawned-crate signature can pass
    // step 3.
    //
    // open-source caveat: a user can fork this mod and remove the verify call. their LOCAL crate
    // will then unpack. but their fork can't sign new crates the relay would accept, so other
    // players running the original mod still see fake picks as counterfeit. that's the design
    // ceiling of any open-source anti-cheat; everything below it is real.
    internal static class CounterfeitDetector
    {
        // ed25519 public key matching the relay's GOLDENPAN_PRIVATE_KEY (base64). pulled from
        // the relay startup log + /pubkey endpoint. ROTATE if you ever rotate the relay key.
        private const string PublicKeyBase64 = "D4Qc/M1vQrliqoFxm9Fhc7/ibnhybdUVfrr2XX6bNq0=";

        private static Ed25519PublicKeyParameters _pubKey;
        private static Ed25519PublicKeyParameters PubKey
        {
            get
            {
                if (_pubKey != null) return _pubKey;
                var bytes = Convert.FromBase64String(PublicKeyBase64);
                _pubKey = new Ed25519PublicKeyParameters(bytes, 0);
                return _pubKey;
            }
        }

        // BLOCKS the calling thread on a local-loopback POST to SPT. that's typically <50ms;
        // the cost is acceptable inside an unpack-Prefix because the alternative (async + spin
        // the patch into a coroutine) doubles the complexity for ~30ms savings on the GUI thread.
        public static bool IsLegitimate(string crateId)
        {
            if (string.IsNullOrEmpty(crateId)) return false;
            try
            {
                var body = JsonConvert.SerializeObject(new SigRequest { crateId = crateId });
                var resp = RequestHandler.PostJson("/goldenpick/cratesig", body);
                var parsed = JsonConvert.DeserializeObject<SigResponse>(resp);
                if (parsed == null || !parsed.found ||
                    string.IsNullOrEmpty(parsed.signature) || string.IsNullOrEmpty(parsed.profileId))
                {
                    Plugin.LogSource?.LogInfo($"[GoldenPick] counterfeit: no signature record for crate {crateId}");
                    return false;
                }

                // canonical payload — MUST byte-match what the relay signed (CrateSigner.BuildCrateAwardPayload)
                var payload = Encoding.UTF8.GetBytes($"crate|{crateId}|{parsed.profileId}|{parsed.awardedAt}");
                var sigBytes = Convert.FromBase64String(parsed.signature);

                var verifier = new Ed25519Signer();
                verifier.Init(forSigning: false, PubKey);
                verifier.BlockUpdate(payload, 0, payload.Length);
                var ok = verifier.VerifySignature(sigBytes);

                if (!ok) Plugin.LogSource?.LogWarning($"[GoldenPick] counterfeit: signature INVALID for crate {crateId} (signed payload mismatched or forged)");
                else Plugin.LogSource?.LogInfo($"[GoldenPick] crate {crateId} verified — legit");
                return ok;
            }
            catch (Exception e)
            {
                // could not reach local server, or server returned malformed JSON, or
                // signature didn't parse. treat as counterfeit — fail closed for security.
                Plugin.LogSource?.LogWarning($"[GoldenPick] counterfeit check errored ({e.GetType().Name}): {e.Message} — treating as counterfeit");
                return false;
            }
        }

        private class SigRequest { public string crateId; }
        private class SigResponse
        {
            public bool found;
            public string signature;
            public long awardedAt;
            public string profileId;
        }
    }
}
