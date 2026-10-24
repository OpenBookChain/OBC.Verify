using OBC.Verify.Web.Models;
using Solnet.Wallet.Utilities;

namespace OBC.Verify.Web.Services;

/// <summary>
/// The shareable verifier URL <c>/?tx=&lt;signature&gt;&amp;network=&lt;network&gt;</c>. Only the public transaction
/// signature and the network go in the URL, never the expected publisher wallet.
/// </summary>
public static class VerifyLink
{
    public const string TxParameter = "tx";
    public const string NetworkParameter = "network";

    public const SolanaNetwork DefaultNetwork = SolanaNetwork.MainnetBeta;

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const int SignatureLength = 64;

    /// <summary>Parses a <c>network</c> query value, case-insensitively. Unknown or missing values return false.</summary>
    public static bool TryParseNetwork(string? value, out SolanaNetwork network)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "mainnet":
            case "mainnet-beta":
            case "mainnetbeta":
                network = SolanaNetwork.MainnetBeta;
                return true;
            case "devnet":
                network = SolanaNetwork.Devnet;
                return true;
            case "testnet":
                network = SolanaNetwork.Testnet;
                return true;
            default:
                network = DefaultNetwork;
                return false;
        }
    }

    /// <summary>The canonical <c>network</c> query value, matching Solana's cluster names.</summary>
    public static string ToQueryValue(SolanaNetwork network) => network switch
    {
        SolanaNetwork.Devnet => "devnet",
        SolanaNetwork.Testnet => "testnet",
        _ => "mainnet-beta"
    };

    /// <summary>True when the value is base58 that decodes to a 64-byte ed25519 signature.</summary>
    public static bool IsValidSignature(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var signature = value.Trim();
        // 64 bytes are at most 88 base58 characters.
        if (signature.Length > 88 || signature.Any(c => !Base58Alphabet.Contains(c))) return false;
        try
        {
            return new Base58Encoder().DecodeData(signature).Length == SignatureLength;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
