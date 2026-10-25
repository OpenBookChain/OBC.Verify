using System.Collections.Concurrent;
using System.Security.Cryptography;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Fetching;
using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;
using Solnet.Wallet;

namespace OBC.Verify.Web.Services;

/// <summary>
/// The rebuild and completeness check of acc-period-close (section 5), for an auditor with off-chain access:
/// reads every v3 record the publisher signed, fetches each file for its booking date, follows the
/// previous-period links and each period's versions, and reports late, changed, omitted and broken members,
/// gaps and overlaps. It never changes the integrity result of a single record.
/// </summary>
public class PeriodAuditService
{
    private const string LabelPrefix = "label:";

    private readonly ISolanaReaderFactory _readerFactory;
    private readonly IDocumentFetcher _fetcher;
    private readonly PeriodAuditOptions _options;
    private readonly PeriodAuditGate _gate;
    private readonly ILogger<PeriodAuditService> _logger;

    /// <param name="gate">Shared by every audit of the application; a private one is created when null (tests).</param>
    public PeriodAuditService(ISolanaReaderFactory readerFactory, IDocumentFetcher fetcher, ILogger<PeriodAuditService> logger,
        PeriodAuditOptions? options = null, PeriodAuditGate? gate = null)
    {
        _readerFactory = readerFactory;
        _fetcher = fetcher;
        _logger = logger;
        _options = options ?? new PeriodAuditOptions();
        _gate = gate ?? new PeriodAuditGate(_options);
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<PeriodAuditReport> AuditAsync(PeriodAuditRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var publisher = request.Publisher.Trim();
        var report = new PeriodAuditReport { Publisher = publisher, Network = request.Network.ToString() };
        if (!_options.Enabled)
        {
            report.Error = "The period audit is not enabled on this server (Verification:Audit:Enabled).";
            return report;
        }
        if (!PublicKey.IsValid(publisher))
        {
            report.Error = $"'{publisher}' is not a valid Solana wallet address.";
            return report;
        }

        using var slot = _gate.TryEnter();
        if (slot is null)
        {
            report.Error = "Another period audit is running on this server. Try again later, or run the verifier yourself.";
            return report;
        }

        return await RunAsync(request, publisher, report, progress, ct);
    }

    private async Task<PeriodAuditReport> RunAsync(PeriodAuditRequest request, string publisher, PeriodAuditReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var reader = _gate.Limit(_readerFactory.Create(request.Network));
        List<AuditRecord> records;
        try
        {
            progress?.Report("Resolving storage locations…");
            await ResolveBasesAsync(reader, request, report, ct);

            // 4. Everything P published
            records = await ScanRecordsAsync(reader, publisher, report, progress, ct);
            var start = request.LatestCloseSignature?.Trim();
            if (!string.IsNullOrEmpty(start) && records.All(r => r.Signature != start))
            {
                var tx = await reader.GetTransactionAsync(start, ct);
                if (ToRecord(tx, publisher) is not { } startRecord)
                {
                    report.Error = $"The starting transaction {start} is not a v3 record signed by {publisher}.";
                    return report;
                }
                records.Add(startRecord);
            }
            report.RecordCount = records.Count;

            await ReadFilesAsync(records, report.Bases, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Period audit failed for {Publisher}", publisher);
            report.Error = $"Could not read the publisher's records: {ex.Message}";
            return report;
        }

        progress?.Report("Checking periods…");
        var byDoc = records
            .GroupBy(r => r.DocumentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Number).ThenBy(r => r.Slot).ToList(), StringComparer.Ordinal);
        CarryTombstoneDates(byDoc.Values);

        var periodDocs = byDoc.Where(g => g.Value.Any(r => r.Type == PeriodClose.Type)).ToList();
        var documents = byDoc.Where(g => g.Value.All(r => r.Type != PeriodClose.Type)).Select(g => g.Value).ToList();
        if (periodDocs.Count == 0)
        {
            report.Error = report.HistoryComplete
                ? "The publisher has no acc-period-close records, or none of their files could be read."
                : "No acc-period-close records were found in the part of the history that was read.";
            AddRecordWarnings(report, documents, []);
            return report;
        }

        var periods = periodDocs.Select(g => BuildPeriod(g.Key, g.Value, report)).OrderBy(p => p.Period.From).ThenBy(p => p.Period.To).ToList();
        report.Periods.AddRange(periods.Select(p => p.Period));
        var active = periods.Where(p => p.Period.Status != PeriodAuditStatus.Voided && p.Period.From is not null && p.Period.To is not null).ToList();

        FollowLinks(request.LatestCloseSignature?.Trim(), records, periods, report);
        CheckCoverage(active, report);

        var documentsById = documents.ToDictionary(d => d[0].DocumentId, StringComparer.Ordinal);
        foreach (var period in active)
        {
            ClassifyRecords(period, documentsById, report);
        }
        foreach (var period in periods.Where(p => p.Period.Status != PeriodAuditStatus.Voided))
        {
            period.Period.Status = period.Period.Findings.Count > 0 ? PeriodAuditStatus.Failed
                : period.Latest?.Close is { IsClosed: true } ? PeriodAuditStatus.Complete
                : PeriodAuditStatus.Open;
        }

        AddRecordWarnings(report, documents, active);
        return report;
    }

    private static async Task ResolveBasesAsync(ISolanaReader reader, PeriodAuditRequest request, PeriodAuditReport report, CancellationToken ct)
    {
        var labels = request.Labels.ToDictionary(
            l => l.Key.StartsWith(LabelPrefix, StringComparison.Ordinal) ? l.Key : LabelPrefix + l.Key,
            l => l.Value.Trim(),
            StringComparer.Ordinal);

        var resolution = await LocationResolver.ResolveAsync(reader, report.Publisher, ct);
        report.Warnings.AddRange(resolution.Warnings);
        foreach (var hop in resolution.Hops.Where(h => h.Error is not null))
        {
            report.Warnings.Add($"Location account {hop.Address}: {hop.Error}");
        }

        foreach (var b in resolution.Bases.Concat(request.ExtraBases.Select(b => b.Trim())).Where(b => b.Length > 0))
        {
            var resolved = b;
            if (b.StartsWith(LabelPrefix, StringComparison.Ordinal))
            {
                if (!labels.TryGetValue(b, out var mapped))
                {
                    report.Warnings.Add($"The location account lists {b}, which isn't in the label map, so it was skipped.");
                    continue;
                }
                resolved = mapped;
            }
            if (LocationAccount.BuildUrl(resolved, new string('0', 64)) is null)
            {
                report.Warnings.Add($"{resolved} is not an https base, so it was skipped.");
                continue;
            }
            if (!report.Bases.Contains(resolved)) report.Bases.Add(resolved);
        }

        if (report.Bases.Count == 0)
        {
            report.Warnings.Add("No usable storage base: only location hints in the memos can be followed. Add the off-chain storage base or a label map.");
        }
    }

    private async Task<List<AuditRecord>> ScanRecordsAsync(ISolanaReader reader, string publisher, PeriodAuditReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var candidates = new List<string>();
        string? before = null;
        for (var pages = 0; ; pages++)
        {
            if (pages == _options.MaxPages)
            {
                report.Warnings.Add($"History incomplete: the scan of {publisher} stopped after {report.ScannedSignatures:N0} transactions. Older records weren't checked, and members published before them show as not found.");
                break;
            }

            progress?.Report($"Reading the publisher's history ({report.ScannedSignatures:N0} transactions so far)…");
            var page = await reader.GetSignaturesAsync(publisher, before, VersionChainResolver.PageSize, ct);
            report.ScannedSignatures += page.Count;

            // Every v3 memo has a "d" key; v1/v2 memos and location updates don't. A missing summary is never trusted.
            candidates.AddRange(page.Where(e => !e.Failed && (e.Memo is null || e.Memo.Contains("\"d\"", StringComparison.Ordinal))).Select(e => e.Signature));

            if (page.Count < VersionChainResolver.PageSize)
            {
                report.HistoryComplete = true;
                break;
            }
            before = page[^1].Signature;
        }

        var records = new ConcurrentBag<AuditRecord>();
        var read = 0;
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Concurrency), CancellationToken = ct }, async (signature, token) =>
        {
            var tx = await reader.GetTransactionAsync(signature, token);
            if (ToRecord(tx, publisher) is { } record) records.Add(record);
            var done = Interlocked.Increment(ref read);
            if (done % 100 == 0) progress?.Report($"Reading transactions ({done:N0} of {candidates.Count:N0})…");
        });
        return records.ToList();
    }

    /// <summary>A valid v3 memo signed by the publisher on the memo instruction, or null.</summary>
    private static AuditRecord? ToRecord(ChainTransaction? tx, string publisher)
    {
        if (tx is null || tx.Error is not null || tx.Memo is null || !tx.MemoSigners.Contains(publisher)) return null;

        OnChainMemo memo;
        try
        {
            memo = MemoParser.Parse(tx.Memo);
        }
        catch (FormatException)
        {
            return null;
        }
        if (memo.Version != "3" || MemoParser.ValidateV3(memo).Count > 0 || !MemoParser.IsHash(memo.Hash)) return null;

        return new AuditRecord
        {
            Signature = tx.Signature,
            Slot = tx.Slot,
            BlockTime = tx.BlockTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null,
            DocumentId = memo.DocumentId!,
            Number = memo.VersionNumber!.Value,
            Hash = memo.Hash,
            Hint = string.IsNullOrEmpty(memo.FileUrl) ? null : memo.FileUrl
        };
    }

    private async Task ReadFilesAsync(List<AuditRecord> records, List<string> bases, IProgress<string>? progress, CancellationToken ct)
    {
        var read = 0;
        await Parallel.ForEachAsync(records, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.Concurrency), CancellationToken = ct }, async (record, token) =>
        {
            await ReadFileAsync(record, bases, token);
            var done = Interlocked.Increment(ref read);
            if (done % 50 == 0) progress?.Report($"Fetching off-chain files ({done:N0} of {records.Count:N0})…");
        });
    }

    private async Task ReadFileAsync(AuditRecord record, List<string> bases, CancellationToken ct)
    {
        var urls = bases.Select(b => LocationAccount.BuildUrl(b, record.Hash)).OfType<string>().ToList();
        if (record.Hint is not null) urls.Add(record.Hint);

        byte[]? content = null;
        string? lastError = null;
        foreach (var url in urls.Distinct())
        {
            var fetched = await _fetcher.FetchAsync(url, ct);
            if (!fetched.Success)
            {
                lastError = fetched.Error;
                continue;
            }
            if (Convert.ToHexStringLower(SHA256.HashData(fetched.Content!)) == record.Hash)
            {
                content = fetched.Content;
                break;
            }
            lastError = $"The copy at {url} doesn't match the on-chain hash.";
        }

        if (content is null)
        {
            record.FileError = urls.Count == 0 ? "no storage location" : lastError ?? "not found";
            return;
        }

        var envelope = EnvelopeValidator.Validate(content);
        record.Type = envelope.Type;
        record.IsVoided = envelope.IsVoided;
        if (!envelope.EnvelopeValid)
        {
            record.FileError = $"not a valid v3 envelope ({string.Join("; ", envelope.EnvelopeErrors)})";
            return;
        }
        record.FileRead = true;
        record.BookingDate = envelope.BookingDate;
        record.DataErrors = envelope.DataErrors;
        record.Close = envelope.PeriodClose;
    }

    /// <summary>A tombstone's data is empty, so it keeps the booking date of the version it voids (old or new date, rule 4).</summary>
    private static void CarryTombstoneDates(IEnumerable<List<AuditRecord>> chains)
    {
        foreach (var chain in chains)
        {
            DateOnly? last = null;
            foreach (var record in chain)
            {
                if (record.IsVoided && record.BookingDate is null) record.BookingDate = last;
                last = record.BookingDate ?? last;
            }
        }
    }

    private static AuditPeriod BuildPeriod(string documentId, List<AuditRecord> versions, PeriodAuditReport report)
    {
        var period = new AuditPeriod(new PeriodAuditPeriod { DocumentId = documentId });
        var p = period.Period;

        foreach (var v in versions)
        {
            p.Versions.Add(new PeriodAuditVersion
            {
                Number = v.Number,
                Signature = v.Signature,
                Hash = v.Hash,
                Slot = v.Slot,
                BlockTime = v.BlockTime,
                State = v.IsVoided ? "voided" : v.Close?.State,
                Reason = v.Close?.Reason,
                Count = v.Close?.Count
            });

            if (!v.FileRead)
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {v.Number}: the file couldn't be read ({v.FileError}).", v.Signature));
            }
            else if (v.Type != PeriodClose.Type)
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {v.Number} is a '{v.Type}' record, not a period record.", v.Signature));
            }
            else if (!v.IsVoided && (v.DataErrors.Count > 0 || v.Close is null))
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {v.Number}: {string.Join(" ", v.DataErrors.DefaultIfEmpty("its schema isn't known to this verifier."))}", v.Signature));
            }
        }

        var valid = versions.Where(v => v.Close is not null && v.DataErrors.Count == 0).ToList();
        period.Timeline = versions.OrderBy(v => v.Slot).ToList();
        period.Latest = versions.Where(v => v.Number == versions.Max(x => x.Number)).MaxBy(v => v.Slot);

        // The range is part of d, so every version must name the same one. An invalid version still names its range.
        var named = valid.Count > 0 ? valid : versions.Where(v => v.Close is not null).ToList();
        var idRange = PeriodClose.RangeFromPublicId(documentId);
        if (named.Count > 0)
        {
            var range = named[^1].Close!;
            p.From = range.From;
            p.To = range.To;
            foreach (var v in named.Where(v => v.Close!.From != range.From || v.Close.To != range.To))
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {v.Number} names the range {v.Close!.Range}, version {named[^1].Number} names {range.Range}.", v.Signature));
            }
            if (idRange is { } id && (id.From != PeriodClose.FormatDate(range.From) || id.To != PeriodClose.FormatDate(range.To)))
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"The document id names {id.From} – {id.To}, but the records say {range.Range}."));
            }
        }
        else if (idRange is { } id)
        {
            p.From = PeriodClose.ParseDate(id.From);
            p.To = PeriodClose.ParseDate(id.To);
        }

        // Versioning rules (section 3.4): n = 1 closes, then closed and reopened alternate.
        foreach (var group in versions.GroupBy(v => v.Number).Where(g => g.Select(v => v.Hash).Distinct().Count() > 1))
        {
            p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Fork: {group.Count()} different records claim version {group.Key}."));
        }
        var maxNumber = versions.Max(v => v.Number);
        for (var n = 1L; n <= maxNumber; n++)
        {
            var current = versions.FirstOrDefault(v => v.Number == n);
            if (current is null)
            {
                if (report.HistoryComplete) p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {n} of the period was not found."));
                continue;
            }
            if (current.IsVoided || current.Close is null) continue;
            string? expected = null;
            if (n == 1)
            {
                expected = PeriodClose.ClosedState;
            }
            else if (versions.FirstOrDefault(v => v.Number == n - 1)?.Close is { } previous)
            {
                expected = previous.IsClosed ? PeriodClose.ReopenedState : PeriodClose.ClosedState;
            }
            if (expected is not null && current.Close.State != expected)
            {
                p.Findings.Add(Finding(AuditFindingKind.InvalidClose, $"Version {n} is '{current.Close.State}', expected '{expected}'.", current.Signature));
            }
        }

        if (period.Latest?.IsVoided == true)
        {
            p.Status = PeriodAuditStatus.Voided;
        }
        p.MemberCount = valid.LastOrDefault(v => v.Close!.IsClosed)?.Close!.Count;
        return period;
    }

    /// <summary>Step 1: from the latest close, follow previous-period links back.</summary>
    private static void FollowLinks(string? startSignature, List<AuditRecord> records, List<AuditPeriod> periods, PeriodAuditReport report)
    {
        var byDoc = periods.ToDictionary(p => p.Period.DocumentId, StringComparer.Ordinal);
        var byHash = records.GroupBy(r => r.Hash).ToDictionary(g => g.Key, g => g.First());

        AuditRecord? current;
        if (!string.IsNullOrEmpty(startSignature))
        {
            current = records.First(r => r.Signature == startSignature);
            if (current.Type != PeriodClose.Type)
            {
                report.ChainFindings.Add(Finding(AuditFindingKind.BrokenLink, $"The starting transaction {startSignature} is not a readable acc-period-close record.", startSignature));
                return;
            }
        }
        else
        {
            current = periods
                .Where(p => p.Period.Status != PeriodAuditStatus.Voided && p.Period.To is not null)
                .OrderBy(p => p.Period.To)
                .LastOrDefault()?.Timeline.LastOrDefault(v => v.Close is { IsClosed: true });
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current.DocumentId))
        {
            var period = byDoc[current.DocumentId];
            period.Period.InLinkChain = true;

            var link = current.Close?.PreviousPeriodHash;
            if (link is null)
            {
                // Only the publisher's first period has no link (section 3.3).
                var earlier = periods.FirstOrDefault(p => p.Period.Status != PeriodAuditStatus.Voided && p.Period.From < period.Period.From);
                if (current.Close is not null && earlier is not null)
                {
                    report.ChainFindings.Add(Finding(AuditFindingKind.BrokenLink,
                        $"Period {period.Period.Range} (version {current.Number}) has no previous-period link, but period {earlier.Period.Range} comes before it.", current.Signature));
                }
                break;
            }

            if (!byHash.TryGetValue(link, out var target) || target.Type != PeriodClose.Type)
            {
                report.ChainFindings.Add(Finding(AuditFindingKind.BrokenLink,
                    $"Period {period.Period.Range} links to the previous period {link}, which is not a readable period record of this publisher" +
                    (report.HistoryComplete ? "." : " in the part of the history that was read."), current.Signature));
                break;
            }

            var previousPeriod = byDoc[target.DocumentId];
            var previous = previousPeriod.Period;
            if (period.Period.From is { } from && previous.To is { } to && to.AddDays(1) != from)
            {
                report.ChainFindings.Add(Finding(AuditFindingKind.BrokenLink,
                    $"Period {period.Period.Range} links to period {previous.Range}, which doesn't end the day before it starts.", current.Signature));
            }

            // The link must name a close. Otherwise keep following from that period's latest close.
            if (target.Close is not { IsClosed: true })
            {
                var what = target.IsVoided ? "a tombstone" : target.Close is null ? "unreadable" : $"a {target.Close.State} version";
                report.ChainFindings.Add(Finding(AuditFindingKind.BrokenLink,
                    $"Period {period.Period.Range} links to version {target.Number} of period {previous.Range}, which is {what}, not a close.", current.Signature));
                target = previousPeriod.Timeline.LastOrDefault(v => v.Close is { IsClosed: true });
                if (target is null) break;
            }
            current = target;
        }

        foreach (var p in periods.Where(p => !p.Period.InLinkChain && p.Period.Status != PeriodAuditStatus.Voided))
        {
            report.Warnings.Add($"Period {p.Period.Range} isn't reached through previous-period links from the starting close.");
        }
    }

    /// <summary>Step 2: ranges don't overlap and each period starts the day after the previous one ends.</summary>
    private static void CheckCoverage(List<AuditPeriod> active, PeriodAuditReport report)
    {
        DateOnly? end = null;
        string? endRange = null;
        foreach (var p in active.Select(a => a.Period))
        {
            if (end is { } e)
            {
                if (p.From <= e)
                {
                    report.ChainFindings.Add(Finding(AuditFindingKind.PeriodOverlap, $"Period {p.Range} overlaps period {endRange}."));
                }
                else if (p.From > e.AddDays(1))
                {
                    report.ChainFindings.Add(Finding(AuditFindingKind.PeriodGap,
                        $"No period covers {PeriodClose.FormatDate(e.AddDays(1))} – {PeriodClose.FormatDate(p.From!.Value.AddDays(-1))}, between {endRange} and {p.Range}."));
                }
            }
            if (end is null || p.To > end)
            {
                end = p.To;
                endRange = p.Range;
            }
        }
    }

    /// <summary>Steps 3 and 5: check each close's members and classify every record against the period's versions.</summary>
    private static void ClassifyRecords(AuditPeriod period, Dictionary<string, List<AuditRecord>> documents, PeriodAuditReport report)
    {
        var p = period.Period;
        var from = p.From!.Value;
        var to = p.To!.Value;
        bool InRange(DateOnly? date) => date is { } d && d >= from && d <= to;

        // Members of each valid close by document id. A close may list hundreds of thousands.
        var closes = period.Timeline
            .Where(v => v.Close is { IsClosed: true } && v.DataErrors.Count == 0)
            .ToDictionary(v => v, v => v.Close!.Members!
                .GroupBy(m => m.DocumentId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));

        // Published while the period was closed: late entries and changes after close.
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in documents.Values.SelectMany(d => d))
        {
            var governing = period.Timeline.LastOrDefault(v => v.Slot < record.Slot);
            if (governing is null || !closes.TryGetValue(governing, out var governingMembers)) continue;

            var listed = governingMembers.GetValueOrDefault(record.DocumentId);
            if (listed is not null && listed.Number < record.Number)
            {
                flagged.Add(record.Signature);
                p.Findings.Add(Finding(AuditFindingKind.ChangedAfterClose,
                    $"Document {record.DocumentId} is listed at version {listed.Number} by close version {governing.Number}, but version {record.Number} was published on {VerificationService.FormatDate(record.BlockTime)} while the period was closed" +
                    (InRange(record.BookingDate) ? "." : $" (it moved the document to {PeriodClose.FormatDate(record.BookingDate)})."), record.Signature));
            }
            else if (listed is null && InRange(record.BookingDate))
            {
                flagged.Add(record.Signature);
                p.Findings.Add(Finding(AuditFindingKind.LateEntry,
                    $"Document {record.DocumentId} version {record.Number}, dated {PeriodClose.FormatDate(record.BookingDate)}, was published on {VerificationService.FormatDate(record.BlockTime)} while the period was closed (close version {governing.Number}).", record.Signature));
            }
        }

        foreach (var (closeRecord, members) in closes)
        {
            // Every document whose latest version at the close is booked in the period must be listed at that version.
            foreach (var chain in documents.Values)
            {
                var latest = chain.Where(r => r.Slot <= closeRecord.Slot).MaxBy(r => (r.Number, r.Slot));
                if (latest is null || !InRange(latest.BookingDate) || flagged.Contains(latest.Signature)) continue;

                if (!members.TryGetValue(latest.DocumentId, out var listed))
                {
                    p.Findings.Add(Finding(AuditFindingKind.Omitted,
                        $"Document {latest.DocumentId} version {latest.Number}, dated {PeriodClose.FormatDate(latest.BookingDate)}, was published on {VerificationService.FormatDate(latest.BlockTime)} but is missing from close version {closeRecord.Number}.", latest.Signature));
                }
                else if (listed.Number < latest.Number)
                {
                    p.Findings.Add(Finding(AuditFindingKind.Omitted,
                        $"Close version {closeRecord.Number} lists document {latest.DocumentId} at version {listed.Number}, but version {latest.Number} was published before it ({VerificationService.FormatDate(latest.BlockTime)}).", latest.Signature));
                }
            }

            // Every listed member must be a record of this publisher, as listed.
            foreach (var member in closeRecord.Close!.Members!)
            {
                var problem = CheckMember(member, documents, closeRecord, from, to, report.HistoryComplete);
                if (problem is not null)
                {
                    p.Findings.Add(Finding(AuditFindingKind.BrokenMember, $"Close version {closeRecord.Number}: {problem}", closeRecord.Signature));
                }
            }
        }
    }

    private static string? CheckMember(PeriodMember member, Dictionary<string, List<AuditRecord>> documents, AuditRecord close, DateOnly from, DateOnly to, bool historyComplete)
    {
        var name = $"member {member.DocumentId} version {member.Number}";
        var chain = documents.GetValueOrDefault(member.DocumentId);
        var candidates = chain?.Where(r => r.Number == member.Number).ToList() ?? [];
        if (candidates.Count == 0)
        {
            return $"{name} is not a record signed by the publisher{(historyComplete ? "" : " in the part of the history that was read")}.";
        }

        var record = candidates.FirstOrDefault(r => r.Hash == member.Hash);
        if (record is null) return $"{name} is listed with hash {member.Hash}, but the record on-chain has {candidates[0].Hash}.";
        if (record.Slot > close.Slot) return $"{name} was published after the close that lists it.";
        if (!record.FileRead) return $"{name}: the file couldn't be read ({record.FileError}).";
        if (record.Type != member.Type) return $"{name} is listed as '{member.Type}', but its file is '{record.Type}'.";
        if (record.IsVoided != member.IsVoided) return $"{name} is listed as {(member.IsVoided ? "voided" : "active")}, but its file is {(record.IsVoided ? "a tombstone" : "active")}.";
        if (record.BookingDate is not { } date) return $"{name}: its file has no booking date.";
        if (PeriodClose.FormatDate(date) != member.Date) return $"{name} is listed as dated {member.Date}, but its file says {PeriodClose.FormatDate(date)}.";
        if (date < from || date > to) return $"{name} is dated {member.Date}, outside the period.";
        return null;
    }

    private static void AddRecordWarnings(PeriodAuditReport report, List<List<AuditRecord>> documents, List<AuditPeriod> active)
    {
        var all = documents.SelectMany(d => d).ToList();
        var unread = all.Where(r => !r.FileRead || r.BookingDate is null).ToList();
        report.UnreadRecords = unread.Count;
        if (unread.Count > 0)
        {
            report.Warnings.Add($"{unread.Count:N0} records couldn't be placed in a period: their file couldn't be read or has no booking date (e.g. {unread[0].Signature}: {unread[0].FileError ?? $"type '{unread[0].Type}' has no booking date"}). A late entry among them would go unnoticed.");
        }

        var dated = all.Where(r => r.BookingDate is not null).ToList();
        var outside = dated.Where(r => !active.Any(a => r.BookingDate >= a.Period.From && r.BookingDate <= a.Period.To)).ToList();
        report.OutsidePeriods = outside.Count;
        if (active.Count == 0) return;

        var first = active.Min(a => a.Period.From!.Value);
        var last = active.Max(a => a.Period.To!.Value);
        var before = outside.Count(r => r.BookingDate < first);
        var after = outside.Count(r => r.BookingDate > last);
        if (before > 0)
        {
            report.Warnings.Add($"{before:N0} record versions are dated before the first period ({PeriodClose.FormatDate(first)}), so no close covers them.");
        }
        if (after > 0)
        {
            report.Warnings.Add($"{after:N0} record versions are dated after the last closed period ({PeriodClose.FormatDate(last)}). They belong to periods that aren't closed yet.");
        }
    }

    private static AuditFinding Finding(AuditFindingKind kind, string detail, string? signature = null) =>
        new() { Kind = kind, Detail = detail, Signature = signature };

    private sealed class AuditPeriod(PeriodAuditPeriod period)
    {
        public PeriodAuditPeriod Period { get; } = period;
        /// <summary>The period's versions in slot order.</summary>
        public List<AuditRecord> Timeline { get; set; } = new();
        public AuditRecord? Latest { get; set; }
    }

    private sealed class AuditRecord
    {
        public string Signature { get; init; } = string.Empty;
        public ulong Slot { get; init; }
        public DateTimeOffset? BlockTime { get; init; }
        public string DocumentId { get; init; } = string.Empty;
        public long Number { get; init; }
        public string Hash { get; init; } = string.Empty;
        public string? Hint { get; init; }

        public bool FileRead { get; set; }
        public string? FileError { get; set; }
        public string? Type { get; set; }
        public bool IsVoided { get; set; }
        public DateOnly? BookingDate { get; set; }
        public List<string> DataErrors { get; set; } = new();
        public PeriodClose? Close { get; set; }
    }
}
