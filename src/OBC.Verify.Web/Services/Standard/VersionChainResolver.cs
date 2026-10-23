using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Solana;

namespace OBC.Verify.Web.Services.Standard;

/// <summary>Bounds for the version chain lookup (configuration section "Verification:ChainScan").</summary>
public sealed class ChainScanOptions
{
    /// <summary>Pages of 1000 signatures read per address (k, then the signer's history) before stopping with "history incomplete".</summary>
    public int MaxPages { get; set; } = 3;
}

/// <summary>
/// Section 6, step 11: finds the other versions of a document. Lists the document lookup key k first
/// and falls back to scanning the signer's history for records published without k.
/// Only valid v3 memos signed by the same signer with the same <c>d</c> count.
/// </summary>
public sealed class VersionChainResolver
{
    public const int PageSize = 1000;

    private readonly ISolanaReader _reader;
    private readonly int _maxPages;

    public VersionChainResolver(ISolanaReader reader, int maxPages = 3)
    {
        _reader = reader;
        _maxPages = Math.Max(1, maxPages);
    }

    /// <param name="signer">The publisher wallet P that signed <paramref name="current"/>.</param>
    /// <param name="tombstoneProbe">
    /// Fetches a version's off-chain file and returns whether it is a tombstone and its reason,
    /// or null when the file could not be fetched with a matching hash.
    /// </param>
    public async Task<VersionChainReport> ResolveAsync(
        string signer,
        ChainVersion current,
        Func<ChainVersion, CancellationToken, Task<(bool IsVoided, string? Reason)?>>? tombstoneProbe,
        CancellationToken ct = default)
    {
        var report = new VersionChainReport { LookupKey = DocumentLookupKey.Derive(signer, current.DocumentId) };
        var versions = new Dictionary<string, ChainVersion> { [current.Signature] = current };
        var warnings = new List<string>();

        // 11.1: every version published with k is listed under k.
        var viaKey = await ScanAsync(report.LookupKey, signer, current, versions, warnings, ct);
        report.ScannedSignatures = viaKey.Scanned;
        report.ScanComplete = viaKey.Complete;
        report.DiscoveredVia = ChainDiscovery.LookupKey;

        // 11.2: records published without k (before the p-memo switch) are only found in the signer's history.
        // Also scan when k leaves a hole below the newest version, e.g. a chain that started before the switch.
        if (viaKey.Found == 0 || HasMissingVersions(versions.Values))
        {
            var viaScan = await ScanAsync(signer, signer, current, versions, warnings, ct);
            report.ScannedSignatures += viaScan.Scanned;
            report.ScanComplete = viaKey.Complete && viaScan.Complete;
            report.DiscoveredVia = viaKey.Found == 0 ? ChainDiscovery.SignerHistory : ChainDiscovery.LookupKeyAndSignerHistory;
        }

        report.Versions = versions.Values
            .OrderBy(v => v.Number).ThenBy(v => v.Slot)
            .ToList();

        report.Warnings.AddRange(warnings.Distinct());
        Analyse(report, current);

        if (report.Latest is not null && tombstoneProbe is not null)
        {
            var status = await tombstoneProbe(report.Latest, ct);
            report.LatestIsVoided = status?.IsVoided;
            report.LatestVoidReason = status?.Reason;
            if (status is null)
            {
                report.Warnings.Add($"Could not fetch version {report.Latest.Number} to check whether it is a tombstone.");
            }
        }

        return report;
    }

    private static bool HasMissingVersions(IEnumerable<ChainVersion> versions)
    {
        var numbers = versions.Select(v => v.Number).ToHashSet();
        return numbers.Count < numbers.Max();
    }

    private sealed record ScanResult(int Scanned, int Found, bool Complete);

    /// <summary>
    /// Pages through the signatures of <paramref name="address"/>. Entries whose RPC memo summary is present and
    /// does not mention d are skipped. A missing summary is never trusted (providers may not fill it for p-memo),
    /// so those transactions are read in full.
    /// </summary>
    private async Task<ScanResult> ScanAsync(
        string address,
        string signer,
        ChainVersion current,
        Dictionary<string, ChainVersion> versions,
        List<string> warnings,
        CancellationToken ct)
    {
        string? before = null;
        var scanned = 0;
        var found = 0;
        for (var pages = 0; ; pages++)
        {
            if (pages == _maxPages)
            {
                warnings.Add($"History incomplete: the scan of {address} stopped after {scanned:N0} transactions, so some versions may not have been found.");
                return new ScanResult(scanned, found, false);
            }

            var page = await _reader.GetSignaturesAsync(address, before, PageSize, ct);
            scanned += page.Count;

            foreach (var entry in page)
            {
                if (entry.Failed) continue;
                if (entry.Signature == current.Signature || versions.ContainsKey(entry.Signature))
                {
                    found++;
                    continue;
                }
                if (entry.Memo is not null && !entry.Memo.Contains(current.DocumentId, StringComparison.Ordinal)) continue;

                var version = await ReadVersionAsync(entry.Signature, signer, current.DocumentId, warnings, ct);
                if (version is null) continue;
                versions[version.Signature] = version;
                found++;
            }

            if (page.Count < PageSize) return new ScanResult(scanned, found, true);
            before = page[^1].Signature;
        }
    }

    private async Task<ChainVersion?> ReadVersionAsync(string signature, string signer, string documentId, List<string> warnings, CancellationToken ct)
    {
        var tx = await _reader.GetTransactionAsync(signature, ct);
        if (tx is null || tx.Error is not null || tx.Memo is null) return null;
        if (!tx.MemoSigners.Contains(signer)) return null;

        OnChainMemo memo;
        try
        {
            memo = MemoParser.Parse(tx.Memo);
        }
        catch (FormatException)
        {
            return null;
        }

        if (memo.Version != "3" || memo.DocumentId != documentId) return null;

        var errors = MemoParser.ValidateV3(memo);
        if (errors.Count > 0)
        {
            warnings.Add($"Ignored record {signature}: it is signed by the publisher and names this document, but is not a valid v3 memo ({string.Join(" ", errors)})");
            return null;
        }

        return new ChainVersion
        {
            Signature = signature,
            DocumentId = documentId,
            Number = memo.VersionNumber!.Value,
            Hash = memo.Hash,
            PreviousHash = memo.PreviousHash,
            LocationHint = string.IsNullOrEmpty(memo.FileUrl) ? null : memo.FileUrl,
            Slot = tx.Slot,
            BlockTime = tx.BlockTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null
        };
    }

    internal static void Analyse(VersionChainReport report, ChainVersion current)
    {
        var versions = report.Versions;

        // Backwards (11.3)
        if (current.Number > 1)
        {
            report.Previous = versions.FirstOrDefault(v => v.Number == current.Number - 1 && v.Hash == current.PreviousHash);
            if (report.Previous is null)
            {
                report.Warnings.Add(report.ScanComplete
                    ? $"Chain gap: previous version {current.Number - 1} (h = {current.PreviousHash}) was not found."
                    : $"Previous version {current.Number - 1} (h = {current.PreviousHash}) was not found in the scanned part of the history.");
            }
        }

        // Gaps and broken links anywhere in the chain. The scan reads newest first, so a scan that stopped early
        // misses the oldest versions: a missing number is only a gap when the scan was complete.
        var maxNumber = versions.Max(v => v.Number);
        var byNumber = versions.GroupBy(v => v.Number).ToDictionary(g => g.Key, g => g.ToList());
        for (var n = 1L; n <= maxNumber; n++)
        {
            if (!byNumber.ContainsKey(n))
            {
                if (report.ScanComplete && n != current.Number - 1) report.Warnings.Add($"Chain gap: version {n} was not found.");
                continue;
            }
            if (n == 1 || !byNumber.TryGetValue(n - 1, out var previous)) continue;
            foreach (var v in byNumber[n].Where(v => previous.All(p => p.Hash != v.PreviousHash)))
            {
                if (v == current) continue; // already reported above
                report.Warnings.Add($"Chain gap: version {n} ({v.Signature}) points to a previous hash that no version {n - 1} has.");
            }
        }

        // Forks (11.5)
        foreach (var group in byNumber.Where(g => g.Value.Select(v => v.Hash).Distinct().Count() > 1))
        {
            report.Warnings.Add($"Fork: {group.Value.Select(v => v.Hash).Distinct().Count()} different records claim version {group.Key}: {string.Join(", ", group.Value.Select(v => v.Signature))}.");
        }
        foreach (var group in versions.Where(v => v.PreviousHash is not null).GroupBy(v => v.PreviousHash!)
                     .Where(g => g.Select(v => v.Hash).Distinct().Count() > 1))
        {
            // Same-number forks are already reported above.
            if (group.Select(v => v.Number).Distinct().Count() == 1) continue;
            report.Warnings.Add($"Fork: {group.Count()} records share the previous hash {group.Key}: {string.Join(", ", group.Select(v => v.Signature))}.");
        }

        // Forwards (11.4)
        if (maxNumber > current.Number)
        {
            report.Latest = byNumber[maxNumber].OrderByDescending(v => v.Slot).First();
        }
    }
}
