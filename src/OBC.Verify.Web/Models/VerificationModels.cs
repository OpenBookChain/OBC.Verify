using System.Text.Json.Serialization;

namespace OBC.Verify.Web.Models;

public class OnChainMemo
{
    [JsonPropertyName("v")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("h")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    public string FileUrl { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtraFields { get; set; }
}

public class VerificationResult
{
    public bool IsVerified { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TransactionId { get; set; }
    public string? StandardVersion { get; set; }
    public string? OnChainHash { get; set; }
    public string? ComputedHash { get; set; }
    public string? FileUrl { get; set; }
    public string? Network { get; set; }
    public OnChainMemo? OnChainMemo { get; set; }
    public string? OffChainDocumentJson { get; set; }
    public List<VerificationStep> Steps { get; set; } = new();
}

public class VerificationStep
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

public enum SolanaNetwork
{
    MainnetBeta,
    Devnet,
    Testnet
}
