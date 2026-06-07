using System.Text;
using LinkshellManagerDiscordApp.Options;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace LinkshellManagerDiscordApp.Services;

// Verifies the Ed25519 signature Discord puts on every inbound HTTP interaction
// request, per https://discord.com/developers/docs/interactions/overview. The
// signed message is (X-Signature-Timestamp + raw request body); the key is the
// application's hex public key (Discord:PublicKey). Discord routinely probes the
// endpoint with deliberately bad signatures, so verification MUST fail closed.
// .NET 8 has no built-in Ed25519, so this uses BouncyCastle.
public sealed class DiscordInteractionVerifier
{
    private readonly DiscordOAuthOptions _options;
    private readonly ILogger<DiscordInteractionVerifier> _logger;

    public DiscordInteractionVerifier(
        IOptions<DiscordOAuthOptions> options, ILogger<DiscordInteractionVerifier> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.PublicKey);

    public bool Verify(string? signatureHex, string? timestamp, string rawBody)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(signatureHex) || string.IsNullOrWhiteSpace(timestamp))
        {
            return false;
        }

        try
        {
            var publicKey = Convert.FromHexString(_options.PublicKey!);
            var signature = Convert.FromHexString(signatureHex);
            var message = Encoding.UTF8.GetBytes(timestamp + rawBody);

            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex)
        {
            // Malformed hex / wrong key length / etc. — treat as not verified.
            _logger.LogWarning(ex, "Discord interaction signature verification threw; rejecting.");
            return false;
        }
    }
}
