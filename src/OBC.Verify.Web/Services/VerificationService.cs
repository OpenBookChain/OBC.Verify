using System.Security.Cryptography;
using System.Text;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Fetching;
using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Services;

public class VerificationService
{
    private static readonly HashSet<string> SupportedStandardVersions = new(StringComparer.Ordinal)
    {
        "1",
        "2",
        "3"
    };

    private const int MaxLinksToResolve = 10;

    private readonly ISolanaReaderFactory _readerFactory;
    private readonly IDocumentFetcher _fetcher;
    private readonly ILogger<VerificationService> _logger;
    private readonly ChainScanOptions _chainScan;

    public VerificationService(ISolanaReaderFactory readerFactory, IDocumentFetcher fetcher, ILogger<VerificationService> logger, ChainScanOptions? chainScan = null)
    {
        _readerFactory = readerFactory;
        _fetcher = fetcher;
        _logger = logger;
        _chainScan = chainScan ?? new ChainScanOptions();
    }

    /// <param name="expectedSigner">The organisation's publisher wallet, when the user knows it (section 6, step 3).</param>
    public async Task<VerificationResult> VerifyAsync(string transactionId, SolanaNetwork network, string? expectedSigner = null, CancellationToken ct = default)
    {
        var result = new VerificationResult
        {
            TransactionId = transactionId,
            Network = network.ToString()
        };

        // Step 1: Fetch transaction from Solana at finalized commitment
        var reader = _readerFactory.Create(network);
        result.Steps.Add(new VerificationStep { Name = "Connect to Solana RPC", Passed = true, Detail = reader.RpcUrl });

        ChainTransaction? tx;
        try
        {
            tx = await reader.GetTransactionAsync(transactionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch Solana transaction {TxId}", transactionId);
            return Fail(result, "Fetch Transaction", $"RPC error: {ex.Message}");
        }

        if (tx is null)
        {
            return Fail(result, "Fetch Transaction", "Transaction not found at finalized commitment, or it has no instructions.");
        }
        result.BlockTime = ToTime(tx.BlockTime);

        if (tx.Error is not null)
        {
            return Fail(result, "Fetch Transaction", $"The transaction failed on-chain (meta.err = {tx.Error}), so it anchors nothing.");
        }
        result.Steps.Add(new VerificationStep
        {
            Name = "Fetch Transaction",
            Passed = true,
            Detail = $"Slot: {tx.Slot}, finalized{(result.BlockTime is { } t ? $", {t:yyyy-MM-dd HH:mm:ss} UTC" : "")}"
        });

        // Step 2: Extract memo instruction
        if (string.IsNullOrEmpty(tx.Memo))
        {
            return Fail(result, "Extract Memo", "No memo instruction found in transaction.");
        }
        result.MemoProgram = tx.MemoProgramId;
        result.Steps.Add(new VerificationStep
        {
            Name = "Extract Memo Instruction",
            Passed = true,
            Detail = tx.MemoProgramId is null ? tx.Memo : $"{tx.Memo} (program {MemoProgramName(tx.MemoProgramId)})"
        });

        // Parse memo JSON
        OnChainMemo memo;
        try
        {
            memo = MemoParser.Parse(tx.Memo);
        }
        catch (FormatException ex)
        {
            return Fail(result, "Parse Memo JSON", $"Invalid JSON in memo: {ex.Message}");
        }

        result.OnChainMemo = memo;
        result.StandardVersion = memo.Version;
        result.OnChainHash = memo.Hash;
        result.FileUrl = string.IsNullOrEmpty(memo.FileUrl) ? null : memo.FileUrl;

        // Assert version
        if (!SupportedStandardVersions.Contains(memo.Version))
        {
            return Fail(
                result,
                "Check Standard Version",
                $"Unsupported standard version '{memo.Version}'. Only '1', '2' and '3' are supported by this verifier.");
        }
        result.Steps.Add(new VerificationStep
        {
            Name = "Check Standard Version",
            Passed = true,
            Detail = $"v{memo.Version} (obc-v{memo.Version})"
        });

        // Step 3: Identify the signer of the memo instruction
        var signerFailure = IdentifySigner(tx, memo.Version == "3", expectedSigner, result);
        if (signerFailure is not null)
        {
            return Fail(result, "Identify Signer", signerFailure);
        }

        // Validate hash format
        if (!MemoParser.IsHash(memo.Hash))
        {
            return Fail(result, "Validate Hash Format", "Hash is missing or not a valid 64-char lowercase hex string.");
        }
        result.Steps.Add(new VerificationStep { Name = "Validate Hash Format", Passed = true, Detail = memo.Hash });

        var isV3 = memo.Version == "3";
        List<string> bases = [];
        if (isV3)
        {
            var memoErrors = MemoParser.ValidateV3(memo);
            if (memoErrors.Count > 0)
            {
                return Fail(result, "Validate v3 Memo", string.Join(" ", memoErrors));
            }
            result.Steps.Add(new VerificationStep
            {
                Name = "Validate v3 Memo",
                Passed = true,
                Detail = $"d = {memo.DocumentId}, n = {memo.VersionNumber}{(memo.PreviousHash is null ? "" : $", p = {memo.PreviousHash}")}"
            });

            // Step 4: Resolve locations
            bases = await ResolveLocationsAsync(reader, result, ct);
        }

        // Step 5: Build candidate URLs
        result.CandidateUrls = BuildCandidates(memo, bases, result);
        if (result.CandidateUrls.Count == 0)
        {
            return Fail(result, "Download Off-Chain Document",
                isV3
                    ? "No storage location: the signer's location account lists no usable https base and the memo has no 'a' hint."
                    : "The memo has no file URL.");
        }
        result.FileUrl ??= result.CandidateUrls[0];

        // Steps 6 and 7: Fetch each candidate until one matches the on-chain hash
        byte[]? documentBytes = null;
        string? computedHash = null;
        foreach (var url in result.CandidateUrls)
        {
            var fetched = await _fetcher.FetchAsync(url, ct);
            if (!fetched.Success)
            {
                result.Steps.Add(new VerificationStep { Name = "Download Off-Chain Document", Passed = false, IsWarning = true, Detail = fetched.Error });
                continue;
            }

            var hash = ComputeSha256Hex(fetched.Content!);
            computedHash ??= hash;
            result.Steps.Add(new VerificationStep
            {
                Name = "Download Off-Chain Document",
                Passed = true,
                Detail = $"{fetched.Content!.Length:N0} bytes from {fetched.FinalUrl}"
            });

            if (hash == memo.Hash)
            {
                documentBytes = fetched.Content;
                computedHash = hash;
                result.FileUrl = url;
                break;
            }

            result.Steps.Add(new VerificationStep
            {
                Name = "Compare Hashes",
                Passed = false,
                IsWarning = true,
                Detail = $"MISMATCH at {url} — on-chain: {memo.Hash} / computed: {hash}"
            });
            result.Warnings.Add($"The copy at {url} does not match the on-chain hash.");
        }

        result.ComputedHash = computedHash;
        if (computedHash is null)
        {
            return Fail(result, "Download Off-Chain Document", "Failed to download the off-chain document from every candidate location.");
        }

        if (documentBytes is null)
        {
            result.IsVerified = false;
            result.ErrorMessage = "Hash mismatch — the off-chain document does not match the on-chain fingerprint.";
            return result;
        }

        result.OffChainDocumentJson = Encoding.UTF8.GetString(documentBytes);
        result.Steps.Add(new VerificationStep { Name = "Compute SHA-256 Hash", Passed = true, Detail = computedHash });
        result.Steps.Add(new VerificationStep
        {
            Name = "Compare Hashes",
            Passed = true,
            Detail = $"On-chain hash matches computed hash ✅ (source: {result.FileUrl})"
        });

        if (!isV3)
        {
            result.IsVerified = true;
            return result;
        }

        // Steps 8–10: envelope, data and links
        if (!ValidateEnvelope(documentBytes, result))
        {
            return result;
        }
        await ResolveLinksAsync(bases, result, ct);
        result.IsVerified = true;

        // Step 11: version chain
        await ResolveChainAsync(reader, memo, tx, bases, result, ct);
        return result;
    }

    /// <summary>
    /// Section 6, step 3. v3 memos must list the publisher as a signer of the memo instruction.
    /// v1/v2 memos may list none; then the fee payer is reported instead, as before.
    /// Returns why verification fails, or null.
    /// </summary>
    private static string? IdentifySigner(ChainTransaction tx, bool isV3, string? expectedSigner, VerificationResult result)
    {
        result.Signers = tx.MemoSigners.ToList();
        var expected = string.IsNullOrWhiteSpace(expectedSigner) ? null : expectedSigner.Trim();

        string detail;
        if (tx.MemoSigners.Count == 0)
        {
            if (isV3)
            {
                return "The memo instruction lists no signer. A v3 record must be signed by the publisher wallet.";
            }
            if (tx.FeePayer is null)
            {
                return "The transaction has no signer.";
            }
            result.Signer = tx.FeePayer;
            detail = $"{tx.FeePayer} (fee payer; the memo lists no signer)";
        }
        else
        {
            result.Signer = expected is not null && tx.MemoSigners.Contains(expected) ? expected : tx.MemoSigners[0];
            detail = tx.MemoSigners.Count == 1
                ? result.Signer
                : $"{string.Join(", ", tx.MemoSigners)} (the memo instruction has {tx.MemoSigners.Count} signers; using {result.Signer})";
        }

        if (expected is not null)
        {
            if (result.Signer != expected)
            {
                return $"Signed by {detail}, not by the expected wallet {expected}.";
            }
            detail += " — matches the expected wallet";
        }

        result.Steps.Add(new VerificationStep { Name = "Identify Signer", Passed = true, IsWarning = tx.MemoSigners.Count == 0, Detail = detail });
        return null;
    }

    private static async Task<List<string>> ResolveLocationsAsync(ISolanaReader reader, VerificationResult result, CancellationToken ct)
    {
        LocationResolution resolution;
        try
        {
            resolution = await LocationResolver.ResolveAsync(reader, result.Signer!, ct);
        }
        catch (ArgumentException ex)
        {
            result.Steps.Add(new VerificationStep { Name = "Resolve Location Account", Passed = false, IsWarning = true, Detail = ex.Message });
            return [];
        }

        result.LocationAccount = resolution.Hops.FirstOrDefault()?.Address;
        foreach (var hop in resolution.Hops)
        {
            result.Steps.Add(hop.Content is { } content
                ? new VerificationStep
                {
                    Name = "Resolve Location Account",
                    Passed = true,
                    Detail = $"{hop.Address} (s = {content.Sequence}, authority {content.Authority}): {content.Json}"
                }
                : new VerificationStep
                {
                    Name = "Resolve Location Account",
                    Passed = false,
                    IsWarning = true,
                    Detail = $"{hop.Address}: {hop.Error}"
                });
        }
        foreach (var warning in resolution.Warnings)
        {
            result.Steps.Add(new VerificationStep { Name = "Resolve Location Account", Passed = false, IsWarning = true, Detail = warning });
            result.Warnings.Add(warning);
        }

        return resolution.Bases;
    }

    private static List<string> BuildCandidates(OnChainMemo memo, List<string> bases, VerificationResult result)
    {
        var candidates = new List<string>();
        foreach (var b in bases)
        {
            var url = LocationAccount.BuildUrl(b, memo.Hash);
            if (url is null)
            {
                var why = b.StartsWith("label:", StringComparison.Ordinal)
                    ? "opaque label, resolved only by a verifier with local configuration"
                    : "not an https base";
                result.Steps.Add(new VerificationStep { Name = "Skip Location", Passed = true, IsWarning = true, Detail = $"{b} ({why})" });
                continue;
            }
            if (!candidates.Contains(url)) candidates.Add(url);
        }

        // v3: the memo's a is a hint after the bases. v1/v2: a, then a1 and a2.
        foreach (var url in new[] { memo.FileUrl, memo.FileUrl1, memo.FileUrl2 })
        {
            if (!string.IsNullOrWhiteSpace(url) && !candidates.Contains(url)) candidates.Add(url);
        }
        return candidates;
    }

    private static bool ValidateEnvelope(byte[] documentBytes, VerificationResult result)
    {
        var envelope = EnvelopeValidator.Validate(documentBytes);
        result.DocumentType = envelope.Type;
        result.SchemaId = envelope.SchemaId;

        if (!envelope.EnvelopeValid)
        {
            Fail(result, "Validate Envelope", $"The document matches the on-chain hash but is not a valid v3 envelope: {string.Join("; ", envelope.EnvelopeErrors)}");
            return false;
        }
        result.Steps.Add(new VerificationStep { Name = "Validate Envelope", Passed = true, Detail = $"type = {envelope.Type}, schema = {envelope.SchemaId}" });

        if (envelope.IsVoided)
        {
            result.IsVoided = true;
            result.VoidReason = envelope.Reason;
            result.Steps.Add(new VerificationStep
            {
                Name = "Voided Record",
                Passed = true,
                IsWarning = true,
                Detail = $"Tombstone: the document was voided{(string.IsNullOrEmpty(envelope.Reason) ? "" : $" ({envelope.Reason})")}. Data validation skipped."
            });
        }
        else if (!envelope.TypeRecognized)
        {
            result.TypeRecognized = false;
            result.Steps.Add(new VerificationStep
            {
                Name = "Validate Data",
                Passed = true,
                IsWarning = true,
                Detail = $"Integrity verified, type not recognized: this verifier doesn't know schema '{envelope.SchemaId}' (known: {string.Join(", ", EnvelopeValidator.KnownSchemaIds)})."
            });
        }
        else if (envelope.DataErrors.Count > 0)
        {
            Fail(result, "Validate Data", $"The document matches the on-chain hash but its data does not conform to {envelope.SchemaId}: {string.Join("; ", envelope.DataErrors)}");
            return false;
        }
        else
        {
            result.Steps.Add(new VerificationStep { Name = "Validate Data", Passed = true, Detail = $"data conforms to {envelope.SchemaId}" });
        }

        result.Links = envelope.Links;
        return true;
    }

    /// <summary>Step 10: a broken link doesn't change the result for this record.</summary>
    private async Task ResolveLinksAsync(List<string> bases, VerificationResult result, CancellationToken ct)
    {
        foreach (var link in result.Links.Take(MaxLinksToResolve))
        {
            link.ResolvedUrl = await FindVerifiedCopyAsync(bases, link.Hash, null, ct) is { } found ? found.Url : null;
        }
        var notChecked = result.Links.Count - MaxLinksToResolve;
        if (notChecked > 0)
        {
            result.Warnings.Add($"{notChecked} more links not checked (only the first {MaxLinksToResolve} are resolved).");
        }

        if (result.Links.Count > 0)
        {
            result.Steps.Add(new VerificationStep
            {
                Name = "Report Links",
                Passed = true,
                Detail = string.Join("; ", result.Links.Select(l =>
                    $"{l.Rel}{(l.Type is null ? "" : $" ({l.Type})")} → {l.Hash}{(l.ResolvedUrl is null ? " [not resolved]" : " [resolved]")}"))
                    + (notChecked > 0 ? $"; {notChecked} more links not checked" : "")
            });
        }
    }

    private async Task ResolveChainAsync(ISolanaReader reader, OnChainMemo memo, ChainTransaction tx, List<string> bases, VerificationResult result, CancellationToken ct)
    {
        var current = new ChainVersion
        {
            Signature = tx.Signature,
            DocumentId = memo.DocumentId!,
            Number = memo.VersionNumber!.Value,
            Hash = memo.Hash,
            PreviousHash = memo.PreviousHash,
            Slot = tx.Slot,
            BlockTime = ToTime(tx.BlockTime)
        };

        VersionChainReport chain;
        try
        {
            chain = await new VersionChainResolver(reader, _chainScan.MaxPages).ResolveAsync(
                result.Signer!,
                current,
                async (version, token) =>
                {
                    var copy = await FindVerifiedCopyAsync(bases, version.Hash, version.LocationHint, token);
                    return copy is null ? null : EnvelopeValidator.ReadStatus(copy.Value.Content);
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version chain lookup failed for {TxId}", tx.Signature);
            result.Steps.Add(new VerificationStep { Name = "Check Version Chain", Passed = false, IsWarning = true, Detail = $"Could not read the signer's history: {ex.Message}" });
            result.Warnings.Add("The version history could not be checked.");
            return;
        }

        result.Chain = chain;
        result.Warnings.AddRange(chain.Warnings);

        var parts = new List<string> { $"version {current.Number} of {chain.Versions.Select(v => v.Number).Distinct().Count()} found" };
        if (chain.Previous is { } previous)
        {
            parts.Add($"previous version {previous.Number} ({FormatDate(previous.BlockTime)}, {previous.Signature})");
        }
        if (chain.Latest is { } latest)
        {
            parts.Add($"superseded by version {latest.Number} ({FormatDate(latest.BlockTime)})");
            if (chain.LatestIsVoided == true)
            {
                parts.Add($"voided{(string.IsNullOrEmpty(chain.LatestVoidReason) ? "" : $" ({chain.LatestVoidReason})")}");
            }
        }
        else if (chain.ScanComplete)
        {
            parts.Add("this is the latest version");
        }
        else
        {
            parts.Add("no newer version found, but the history is incomplete");
        }

        result.Steps.Add(new VerificationStep
        {
            Name = "Check Version Chain",
            Passed = true,
            IsWarning = chain.Warnings.Count > 0,
            Detail = string.Join("; ", parts) + ". " + chain.DiscoveredVia switch
                     {
                         ChainDiscovery.LookupKey => $"Listed through the document lookup key {chain.LookupKey} ({chain.ScannedSignatures:N0} transactions).",
                         ChainDiscovery.SignerHistory => $"The lookup key {chain.LookupKey} lists no records, so {chain.ScannedSignatures:N0} transactions of the signer were scanned.",
                         _ => $"Listed through the lookup key {chain.LookupKey}, plus a scan of the signer's history for versions published without it ({chain.ScannedSignatures:N0} transactions)."
                     }
                     + (chain.Warnings.Count > 0 ? " " + string.Join(" ", chain.Warnings) : "")
        });
    }

    /// <summary>Fetches <c>{base}/{h}.json</c> (then the hint) and returns the first copy whose SHA-256 is <paramref name="hash"/>.</summary>
    private async Task<(string Url, byte[] Content)?> FindVerifiedCopyAsync(List<string> bases, string hash, string? hint, CancellationToken ct)
    {
        var urls = bases.Select(b => LocationAccount.BuildUrl(b, hash)).OfType<string>().ToList();
        if (!string.IsNullOrWhiteSpace(hint)) urls.Add(hint);

        foreach (var url in urls.Distinct())
        {
            var fetched = await _fetcher.FetchAsync(url, ct);
            if (fetched.Success && ComputeSha256Hex(fetched.Content!) == hash)
            {
                return (url, fetched.Content!);
            }
        }
        return null;
    }

    private static string MemoProgramName(string programId) => programId switch
    {
        MemoExtractor.PMemoProgramId => $"p-memo {programId}",
        MemoExtractor.SplMemoProgramId => $"SPL Memo {programId}",
        _ => programId
    };

    private static DateTimeOffset? ToTime(long? unixSeconds) =>
        unixSeconds is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null;

    public static string FormatDate(DateTimeOffset? time) => time is { } t ? t.ToString("yyyy-MM-dd HH:mm 'UTC'") : "date unknown";

    private static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static VerificationResult Fail(VerificationResult result, string stepName, string message)
    {
        result.Steps.Add(new VerificationStep { Name = stepName, Passed = false, Detail = message });
        result.IsVerified = false;
        result.ErrorMessage = message;
        return result;
    }
}
