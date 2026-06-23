using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace GoldenPickRelay;

// Ed25519 signer for crate awards. private key lives ONLY here (env var GOLDENPAN_PRIVATE_KEY,
// base64 of 32 raw key bytes). public key is exposed via /pubkey for the user to copy-paste
// into the BepInEx client mod source. asymmetric signing means the open-source client can
// VERIFY signatures but cannot FORGE them — the whole anti-cheat backbone rides on that.
//
// payload format for crate awards: SHA256("crate|" + crateId + "|" + profileId + "|" + awardedAt)
// (canonical, deterministic; the client must reconstruct the exact same bytes to verify).
public sealed class CrateSigner
{
    public readonly Ed25519PrivateKeyParameters PrivateKey;
    public readonly Ed25519PublicKeyParameters  PublicKey;

    public CrateSigner(Ed25519PrivateKeyParameters priv)
    {
        PrivateKey = priv;
        PublicKey = priv.GeneratePublicKey();
    }

    // initialize from env var (production) OR generate a fresh keypair and log it
    // (first-run dev). LOGGED PRIVATE KEY MUST be moved to a Fly secret before deploy.
    public static CrateSigner LoadOrGenerate(ILogger logger)
    {
        var b64 = Environment.GetEnvironmentVariable("GOLDENPAN_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(b64))
        {
            try
            {
                var bytes = Convert.FromBase64String(b64.Trim());
                if (bytes.Length != Ed25519PrivateKeyParameters.KeySize)
                    throw new InvalidOperationException($"private key must be {Ed25519PrivateKeyParameters.KeySize} bytes, got {bytes.Length}");
                var priv = new Ed25519PrivateKeyParameters(bytes, 0);
                var signer = new CrateSigner(priv);
                logger.LogInformation("[crate-signer] loaded private key from GOLDENPAN_PRIVATE_KEY env var");
                logger.LogInformation("[crate-signer] public key (base64): {pub}", Convert.ToBase64String(signer.PublicKey.GetEncoded()));
                return signer;
            }
            catch (Exception e)
            {
                logger.LogError(e, "[crate-signer] GOLDENPAN_PRIVATE_KEY failed to parse — falling back to generating a fresh pair");
            }
        }

        // first-run dev fallback: generate, log BOTH so the user can stash one as a secret
        var rnd = new SecureRandom();
        var generated = new Ed25519PrivateKeyParameters(rnd);
        var signer2 = new CrateSigner(generated);
        logger.LogWarning("[crate-signer] NO PRIVATE KEY in env — generated a fresh keypair. SET BOTH VALUES BEFORE PRODUCTION:");
        logger.LogWarning("[crate-signer]   PRIVATE (Fly secret GOLDENPAN_PRIVATE_KEY): {priv}", Convert.ToBase64String(generated.GetEncoded()));
        logger.LogWarning("[crate-signer]   PUBLIC  (hardcode in BepInEx mod source):  {pub}",  Convert.ToBase64String(signer2.PublicKey.GetEncoded()));
        return signer2;
    }

    // canonical award payload — both signer and verifier MUST construct these bytes the same way.
    public static byte[] BuildCrateAwardPayload(string crateId, string profileId, long awardedAt)
    {
        var s = $"crate|{crateId}|{profileId}|{awardedAt}";
        return System.Text.Encoding.UTF8.GetBytes(s);
    }

    public string SignCrateAward(string crateId, string profileId, long awardedAt)
    {
        var msg = BuildCrateAwardPayload(crateId, profileId, awardedAt);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, PrivateKey);
        signer.BlockUpdate(msg, 0, msg.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }
}
