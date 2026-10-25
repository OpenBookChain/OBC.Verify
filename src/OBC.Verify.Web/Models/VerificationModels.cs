using System.Text.Json.Serialization;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Models;

public class OnChainMemo
{
    [JsonPropertyName("v")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("h")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("a")]
    public string FileUrl { get; set; } = string.Empty;

    /// <summary>v1/v2 fallback URL.</summary>
    [JsonPropertyName("a1")]
    public string? FileUrl1 { get; set; }

    /// <summary>v1/v2 fallback URL.</summary>
    [JsonPropertyName("a2")]
    public string? FileUrl2 { get; set; }

    /// <summary>v3 document id.</summary>
    [JsonPropertyName("d")]
    public string? DocumentId { get; set; }

    /// <summary>v3 version number.</summary>
    [JsonPropertyName("n")]
    public long? VersionNumber { get; set; }

    /// <summary>v3 hash of the previous version.</summary>
    [JsonPropertyName("p")]
    public string? PreviousHash { get; set; }

    [JsonExtensionData]
    public Dictionary<string, object?>? ExtraFields { get; set; }
}

public class VerificationResult
{
    /// <summary>The record is authentic: the off-chain bytes match the on-chain hash and the document conforms to its standard.</summary>
    public bool IsVerified { get; set; }
    public string? ErrorMessage { get; set; }
    public string? TransactionId { get; set; }
    public string? StandardVersion { get; set; }
    public string? OnChainHash { get; set; }
    public string? ComputedHash { get; set; }
    /// <summary>The URL the matching document was downloaded from (or the first candidate when none matched).</summary>
    public string? FileUrl { get; set; }
    public string? Network { get; set; }
    public DateTimeOffset? BlockTime { get; set; }
    public OnChainMemo? OnChainMemo { get; set; }
    public string? OffChainDocumentJson { get; set; }

    /// <summary>The wallet that signed the memo instruction (publisher wallet P).</summary>
    public string? Signer { get; set; }
    public List<string> Signers { get; set; } = new();
    /// <summary>The memo program the record was read from (SPL Memo or p-memo).</summary>
    public string? MemoProgram { get; set; }
    public string? LocationAccount { get; set; }
    public List<string> CandidateUrls { get; set; } = new();

    // v3 envelope
    public string? DocumentType { get; set; }
    public string? SchemaId { get; set; }
    /// <summary>False when the hash matched but the verifier doesn't know the schema id ("integrity verified, type not recognized").</summary>
    public bool TypeRecognized { get; set; } = true;
    public bool IsVoided { get; set; }
    public string? VoidReason { get; set; }
    public List<RecordLink> Links { get; set; } = new();
    /// <summary>Set for an <c>acc-period-close</c> record.</summary>
    public PeriodClose? PeriodClose { get; set; }

    // v3 version chain
    public VersionChainReport? Chain { get; set; }

    public List<string> Warnings { get; set; } = new();
    public List<VerificationStep> Steps { get; set; } = new();
}

public class VerificationStep
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    /// <summary>A non-fatal problem, shown as a warning instead of a failure.</summary>
    public bool IsWarning { get; set; }
    public string? Detail { get; set; }
}

public class RecordLink
{
    public string Rel { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string Hash { get; set; } = string.Empty;
    /// <summary>Where the linked record was found with a matching hash, null when it could not be resolved.</summary>
    public string? ResolvedUrl { get; set; }
}

/// <summary>A signed v3 memo of one document (same d, same signer).</summary>
public class ChainVersion
{
    public string Signature { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public long Number { get; set; }
    public string Hash { get; set; } = string.Empty;
    public string? PreviousHash { get; set; }
    public string? LocationHint { get; set; }
    public ulong Slot { get; set; }
    public DateTimeOffset? BlockTime { get; set; }
}

public class VersionChainReport
{
    /// <summary>Every version found, ordered by number then slot. Includes the verified record.</summary>
    public List<ChainVersion> Versions { get; set; } = new();
    public ChainVersion? Previous { get; set; }
    /// <summary>The newest version when it is newer than the verified record.</summary>
    public ChainVersion? Latest { get; set; }
    public bool IsSuperseded => Latest is not null;
    /// <summary>True when the latest version is a tombstone, null when that could not be determined.</summary>
    public bool? LatestIsVoided { get; set; }
    public string? LatestVoidReason { get; set; }
    /// <summary>The document lookup key k derived from the signer and d (section 3.3).</summary>
    public string? LookupKey { get; set; }
    public ChainDiscovery DiscoveredVia { get; set; }
    public bool ScanComplete { get; set; }
    public int ScannedSignatures { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public enum ChainDiscovery
{
    /// <summary>getSignaturesForAddress(k) listed the versions.</summary>
    LookupKey,
    /// <summary>k listed nothing, so the signer's history was scanned (records published without k).</summary>
    SignerHistory,
    /// <summary>k listed some versions and the signer's history was scanned for the missing ones.</summary>
    LookupKeyAndSignerHistory
}

public enum SolanaNetwork
{
    MainnetBeta,
    Devnet,
    Testnet
}
