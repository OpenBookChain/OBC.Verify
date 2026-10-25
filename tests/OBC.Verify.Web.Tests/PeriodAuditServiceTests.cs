using Microsoft.Extensions.Logging.Abstractions;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services;
using OBC.Verify.Web.Services.Solana;

namespace OBC.Verify.Web.Tests;

/// <summary>The rebuild and completeness check (acc-period-close section 5) against an in-memory chain.</summary>
public class PeriodAuditServiceTests
{
    private const string Sep1 = "2026-09-01";
    private const string Sep30 = "2026-09-30";
    private const string Oct1 = "2026-10-01";
    private const string Oct31 = "2026-10-31";

    private readonly FakeSolanaReader _reader = new();
    private readonly FakeFetcher _fetcher = new();
    private int _documents;

    public PeriodAuditServiceTests()
    {
        _reader.SetLocation(TestData.Publisher, $$"""{"v":1,"s":1,"b":["{{TestData.Base}}"]}""");
    }

    private sealed record Rec(string D, long N, string H, string Date, bool Voided = false, string Sig = "");

    private sealed record Period(string D, long N, string H, string Sig);

    private Task<PeriodAuditReport> Audit(string? start = null, PeriodAuditRequest? request = null)
    {
        request ??= new PeriodAuditRequest();
        request.Network = SolanaNetwork.Devnet;
        request.Publisher = TestData.Publisher;
        request.LatestCloseSignature = start;
        return new PeriodAuditService(_reader, _fetcher, NullLogger<PeriodAuditService>.Instance, new PeriodAuditOptions { Enabled = true, Concurrency = 3, RpcRequestsPerSecond = 10_000 })
            .AuditAsync(request);
    }

    private string Store(string content)
    {
        var bytes = TestData.Bytes(content);
        var h = TestData.Sha256(bytes);
        _fetcher.Files[$"{TestData.Base}/{h}.json"] = bytes;
        return h;
    }

    /// <summary>Publishes a journal version dated <paramref name="date"/> (a new document unless <paramref name="previous"/> is given).</summary>
    private Rec Journal(string date, Rec? previous = null, bool voided = false)
    {
        var d = previous?.D ?? $"doc-{++_documents:D3}";
        var n = (previous?.N ?? 0) + 1;
        var content = voided
            ? """{"version":3,"type":"journal","schema":"obc.journal/2","status":"voided","reason":"voided in source system","data":{}}"""
            : TestData.Envelope(TestData.JournalData.Replace("2026-04-16T00:00:00Z", $"{date}T00:00:00Z").Replace("\"JE-1\"", $"\"{d}-{n}\""));
        var h = Store(content);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, n, previous?.H, d));
        return new Rec(d, n, h, voided ? previous!.Date : date, voided, sig);
    }

    private static string PeriodId(string from, string to) => $"7:obc:acc-period/{from}/{to}";

    private Period Close(string from, string to, Rec[] members, Period? previousPeriod = null, Period? previousVersion = null) =>
        PublishPeriod(from, to, PeriodData.Envelope(
            PeriodData.Close(from, to, members.Select(m => (m.D, m.N, m.H, m.Date, m.Voided))), previousPeriod?.H), previousVersion);

    private Period Reopen(string from, string to, Period previousVersion) =>
        PublishPeriod(from, to, PeriodData.Envelope(PeriodData.Reopen(from, to)), previousVersion);

    private Period PublishPeriod(string from, string to, string envelope, Period? previousVersion)
    {
        var d = PeriodId(from, to);
        var n = (previousVersion?.N ?? 0) + 1;
        var h = Store(envelope);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, n, previousVersion?.H, d));
        return new Period(d, n, h, sig);
    }

    private static PeriodAuditPeriod Find(PeriodAuditReport report, string from) =>
        report.Periods.Single(p => p.Range.StartsWith(from, StringComparison.Ordinal));

    [Fact]
    public async Task Untouched_periods_are_complete()
    {
        var a = Journal("2026-09-03");
        var b = Journal("2026-09-30");
        var sept = Close(Sep1, Sep30, [a, b]);
        var c = Journal("2026-10-02");
        var oct = Close(Oct1, Oct31, [c], previousPeriod: sept);

        var report = await Audit(oct.Sig);

        Assert.Null(report.Error);
        Assert.True(report.HistoryComplete);
        Assert.Equal(5, report.RecordCount);
        Assert.Empty(report.ChainFindings);
        Assert.Equal([PeriodAuditStatus.Complete, PeriodAuditStatus.Complete], report.Periods.Select(p => p.Status));
        Assert.All(report.Periods, p => Assert.True(p.InLinkChain));
        Assert.Equal(2, Find(report, Sep1).MemberCount);
        Assert.True(report.AllComplete);
        Assert.Equal(0, report.UnreadRecords);
    }

    [Fact]
    public async Task Without_a_start_the_period_that_ends_last_is_used()
    {
        var sept = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        Close(Oct1, Oct31, [Journal("2026-10-02")], previousPeriod: sept);

        var report = await Audit();

        Assert.True(report.AllComplete, string.Join(" ", report.Periods.SelectMany(p => p.Findings).Select(f => f.Detail)));
        Assert.All(report.Periods, p => Assert.True(p.InLinkChain));
    }

    [Fact]
    public async Task Record_published_into_a_closed_period_is_a_late_entry()
    {
        var sept = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        var late = Journal("2026-09-20");

        var report = await Audit(sept.Sig);

        var period = Find(report, Sep1);
        Assert.Equal(PeriodAuditStatus.Failed, period.Status);
        var finding = Assert.Single(period.Findings);
        Assert.Equal(AuditFindingKind.LateEntry, finding.Kind);
        Assert.Equal(late.Sig, finding.Signature);
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task New_version_of_a_member_after_close_is_a_change_after_close()
    {
        var a = Journal("2026-09-03");
        Close(Sep1, Sep30, [a]);
        var changed = Journal("2026-09-03", previous: a);

        var report = await Audit();

        var finding = Assert.Single(Find(report, Sep1).Findings);
        Assert.Equal(AuditFindingKind.ChangedAfterClose, finding.Kind);
        Assert.Equal(changed.Sig, finding.Signature);
    }

    [Fact]
    public async Task Voiding_a_member_after_close_is_a_change_after_close()
    {
        var a = Journal("2026-09-03");
        Close(Sep1, Sep30, [a]);
        Journal("2026-09-03", previous: a, voided: true);

        var report = await Audit();

        Assert.Equal(AuditFindingKind.ChangedAfterClose, Assert.Single(Find(report, Sep1).Findings).Kind);
    }

    [Fact]
    public async Task Record_published_while_reopened_but_missing_from_the_next_close_is_omitted()
    {
        var a = Journal("2026-09-03");
        var v1 = Close(Sep1, Sep30, [a]);
        var v2 = Reopen(Sep1, Sep30, v1);
        var missing = Journal("2026-09-25");
        Close(Sep1, Sep30, [a], previousVersion: v2);

        var report = await Audit();

        var period = Find(report, Sep1);
        var finding = Assert.Single(period.Findings);
        Assert.Equal(AuditFindingKind.Omitted, finding.Kind);
        Assert.Equal(missing.Sig, finding.Signature);
        Assert.Equal(2, period.TimesClosed);
        Assert.Equal(1, period.TimesReopened);
        Assert.Equal(3, period.Versions.Count);
    }

    [Fact]
    public async Task Correction_while_reopened_and_listed_again_is_complete()
    {
        var a = Journal("2026-09-03");
        var b = Journal("2026-09-10");
        var v1 = Close(Sep1, Sep30, [a, b]);
        var v2 = Reopen(Sep1, Sep30, v1);
        var a2 = Journal("2026-09-04", previous: a);
        var b2 = Journal("2026-09-10", previous: b, voided: true);
        Close(Sep1, Sep30, [a2, b2], previousVersion: v2);

        var report = await Audit();

        var period = Find(report, Sep1);
        Assert.Empty(period.Findings);
        Assert.Equal(PeriodAuditStatus.Complete, period.Status);
    }

    [Fact]
    public async Task Document_moving_to_another_period_is_complete()
    {
        var a = Journal("2026-09-03");
        var sept1 = Close(Sep1, Sep30, [a]);
        var sept2 = Reopen(Sep1, Sep30, sept1);
        var moved = Journal("2026-10-05", previous: a);
        var sept3 = Close(Sep1, Sep30, [], previousVersion: sept2);
        Close(Oct1, Oct31, [moved], previousPeriod: sept3);

        var report = await Audit();

        Assert.True(report.AllComplete, string.Join(" ", report.Periods.SelectMany(p => p.Findings).Select(f => f.Detail)));
    }

    [Fact]
    public async Task Close_listing_a_record_that_does_not_exist_has_a_broken_member()
    {
        var a = Journal("2026-09-03");
        var ghost = new Rec("doc-ghost", 1, TestData.Sha256(TestData.Bytes("ghost")), "2026-09-04");
        var wrongHash = a with { H = TestData.Sha256(TestData.Bytes("other")) };
        Close(Sep1, Sep30, [ghost, wrongHash]);

        var report = await Audit();

        var findings = Find(report, Sep1).Findings;
        Assert.Equal(2, findings.Count(f => f.Kind == AuditFindingKind.BrokenMember));
        Assert.Contains(findings, f => f.Detail.Contains("doc-ghost") && f.Detail.Contains("not a record signed by the publisher"));
        Assert.Contains(findings, f => f.Detail.Contains("but the record on-chain has"));
        // The real version 1 of a is not listed at its hash, so it's not reported as omitted twice.
        Assert.DoesNotContain(findings, f => f.Kind == AuditFindingKind.Omitted);
    }

    [Fact]
    public async Task Member_listed_with_a_wrong_date_is_broken()
    {
        var a = Journal("2026-09-03");
        Close(Sep1, Sep30, [a with { Date = "2026-09-05" }]);

        var report = await Audit();

        Assert.Contains(Find(report, Sep1).Findings, f => f.Kind == AuditFindingKind.BrokenMember && f.Detail.Contains("its file says 2026-09-03"));
    }

    [Fact]
    public async Task Reopened_period_is_open_not_failed()
    {
        var v1 = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        Reopen(Sep1, Sep30, v1);
        Journal("2026-09-20");

        var report = await Audit();

        var period = Find(report, Sep1);
        Assert.Equal(PeriodAuditStatus.Open, period.Status);
        Assert.Empty(period.Findings);
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task Missing_period_is_a_gap_and_a_link_that_skips_it_is_reported()
    {
        var sept = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        var nov = Close("2026-11-01", "2026-11-30", [Journal("2026-11-03")], previousPeriod: sept);

        var report = await Audit(nov.Sig);

        Assert.Contains(report.ChainFindings, f => f.Kind == AuditFindingKind.PeriodGap && f.Detail.Contains("2026-10-01 – 2026-10-31"));
        Assert.Contains(report.ChainFindings, f => f.Kind == AuditFindingKind.BrokenLink && f.Detail.Contains("doesn't end the day before"));
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task Overlapping_periods_are_reported()
    {
        var sept = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        Close("2026-09-15", Oct31, [], previousPeriod: sept);

        var report = await Audit();

        Assert.Contains(report.ChainFindings, f => f.Kind == AuditFindingKind.PeriodOverlap);
    }

    [Fact]
    public async Task Link_to_an_unknown_record_is_broken()
    {
        Close(Sep1, Sep30, [Journal("2026-09-03")], previousPeriod: new Period("x", 1, new string('a', 64), "none"));

        var report = await Audit();

        Assert.Contains(report.ChainFindings, f => f.Kind == AuditFindingKind.BrokenLink && f.Detail.Contains("not a readable period record"));
    }

    [Fact]
    public async Task Period_without_a_link_after_the_first_is_broken()
    {
        Close(Sep1, Sep30, [Journal("2026-09-03")]);
        var oct = Close(Oct1, Oct31, [Journal("2026-10-03")]);
        var nov = Close("2026-11-01", "2026-11-30", [Journal("2026-11-03")], previousPeriod: oct);

        var report = await Audit(nov.Sig);

        var finding = Assert.Single(report.ChainFindings);
        Assert.Equal(AuditFindingKind.BrokenLink, finding.Kind);
        Assert.Contains("2026-10-01 – 2026-10-31 (version 1) has no previous-period link", finding.Detail);
        Assert.Equal(oct.Sig, finding.Signature);
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task Link_to_a_reopen_is_broken_and_followed_to_the_latest_close()
    {
        var a = Journal("2026-09-03");
        var s1 = Close(Sep1, Sep30, [a]);
        var s2 = Reopen(Sep1, Sep30, s1);
        var oct = Close(Oct1, Oct31, [Journal("2026-10-03")], previousPeriod: s2);
        Close(Sep1, Sep30, [a], previousVersion: s2);

        var report = await Audit(oct.Sig);

        var finding = Assert.Single(report.ChainFindings);
        Assert.Contains("links to version 2 of period 2026-09-01 – 2026-09-30, which is a reopened version, not a close", finding.Detail);
        Assert.All(report.Periods, p => Assert.True(p.InLinkChain));
        Assert.All(report.Periods, p => Assert.Equal(PeriodAuditStatus.Complete, p.Status));
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task Voided_period_counts_as_never_closed()
    {
        var a = Journal("2026-09-03");
        var v1 = Close(Sep1, Sep30, [a]);
        PublishPeriod(Sep1, Sep30, """{"version":3,"type":"acc-period-close","schema":"obc.acc-period-close/1","status":"voided","reason":"wrong range","data":{}}""", v1);
        Journal("2026-09-20");

        var report = await Audit();

        Assert.Equal(PeriodAuditStatus.Voided, Find(report, Sep1).Status);
        Assert.Empty(Find(report, Sep1).Findings);
        Assert.Equal(2, report.OutsidePeriods);
        Assert.False(report.AllComplete);
    }

    [Fact]
    public async Task Close_with_a_wrong_root_is_an_invalid_period_record()
    {
        var a = Journal("2026-09-03");
        var data = PeriodData.Close(Sep1, Sep30, [(a.D, a.N, a.H, a.Date, false)]).Replace("\"count\":1", "\"count\":3");
        PublishPeriod(Sep1, Sep30, PeriodData.Envelope(data), null);

        var report = await Audit();

        Assert.Contains(Find(report, Sep1).Findings, f => f.Kind == AuditFindingKind.InvalidClose && f.Detail.Contains("count: 3"));
    }

    [Fact]
    public async Task Version_one_must_be_a_close()
    {
        Reopen(Sep1, Sep30, null!);

        var report = await Audit();

        Assert.Contains(Find(report, Sep1).Findings, f => f.Kind == AuditFindingKind.InvalidClose && f.Detail.Contains("expected 'closed'"));
    }

    [Fact]
    public async Task Label_bases_resolve_through_the_label_map()
    {
        _reader.SetLocation(TestData.Publisher, """{"v":1,"s":2,"b":["label:vault-1"]}""");
        Close(Sep1, Sep30, [Journal("2026-09-03")]);

        var without = await Audit();
        Assert.NotNull(without.Error);
        Assert.Contains(without.Warnings, w => w.Contains("label:vault-1") && w.Contains("isn't in the label map"));

        var mapped = await Audit(request: new PeriodAuditRequest { Labels = new() { ["vault-1"] = TestData.Base } });
        Assert.Null(mapped.Error);
        Assert.Equal([TestData.Base], mapped.Bases);
        Assert.True(mapped.AllComplete);
    }

    [Fact]
    public async Task Extra_bases_give_off_chain_access()
    {
        _reader.Accounts.Clear();
        Close(Sep1, Sep30, [Journal("2026-09-03")]);

        var report = await Audit(request: new PeriodAuditRequest { ExtraBases = [TestData.Base, "http://insecure.example"] });

        Assert.True(report.AllComplete);
        Assert.Contains(report.Warnings, w => w.Contains("http://insecure.example is not an https base"));
    }

    [Fact]
    public async Task Records_of_other_signers_are_ignored()
    {
        var sept = Close(Sep1, Sep30, [Journal("2026-09-03")]);
        // Anyone can list P in a transaction, so it shows up in P's history without P's signature.
        var h = Store(TestData.Envelope(TestData.JournalData.Replace("2026-04-16", "2026-09-21")));
        var sig = _reader.AddMemo(TestData.OtherWallet, TestData.MemoV3(h, 1, d: "intruder"));
        var tx = _reader.Transactions[sig];
        _reader.AddHistory(TestData.Publisher, new ChainSignature(sig, tx.Slot, tx.BlockTime, false, null));

        var report = await Audit(sept.Sig);

        Assert.True(report.AllComplete);
        Assert.Equal(2, report.RecordCount);
    }

    [Fact]
    public async Task Records_before_the_first_period_are_reported()
    {
        Journal("2026-08-15");
        Close(Sep1, Sep30, [Journal("2026-09-03")]);

        var report = await Audit();

        Assert.True(report.AllComplete);
        Assert.Equal(1, report.OutsidePeriods);
        Assert.Contains(report.Warnings, w => w.Contains("before the first period"));
    }

    [Fact]
    public async Task Invalid_publisher_is_an_error()
    {
        var report = await new PeriodAuditService(_reader, _fetcher, NullLogger<PeriodAuditService>.Instance, new PeriodAuditOptions { Enabled = true })
            .AuditAsync(new PeriodAuditRequest { Publisher = "not-a-wallet" });

        Assert.Contains("not a valid", report.Error);
    }

    [Fact]
    public async Task Disabled_audit_reads_nothing()
    {
        Close(Sep1, Sep30, [Journal("2026-09-03")]);

        var report = await new PeriodAuditService(_reader, _fetcher, NullLogger<PeriodAuditService>.Instance)
            .AuditAsync(new PeriodAuditRequest { Network = SolanaNetwork.Devnet, Publisher = TestData.Publisher });

        Assert.Contains("not enabled", report.Error);
        Assert.Equal(0, _reader.TransactionReads);
        Assert.Empty(_fetcher.Requests);
    }

    [Fact]
    public async Task Audit_is_refused_while_all_slots_are_busy()
    {
        Close(Sep1, Sep30, [Journal("2026-09-03")]);
        var options = new PeriodAuditOptions { Enabled = true, MaxConcurrentAudits = 1, RpcRequestsPerSecond = 10_000 };
        using var gate = new PeriodAuditGate(options);
        var service = new PeriodAuditService(_reader, _fetcher, NullLogger<PeriodAuditService>.Instance, options, gate);

        using (gate.TryEnter())
        {
            var busy = await service.AuditAsync(new PeriodAuditRequest { Network = SolanaNetwork.Devnet, Publisher = TestData.Publisher });
            Assert.Contains("Another period audit is running", busy.Error);
        }

        var after = await service.AuditAsync(new PeriodAuditRequest { Network = SolanaNetwork.Devnet, Publisher = TestData.Publisher });
        Assert.True(after.AllComplete, after.Error);
    }

    [Fact]
    public async Task Rpc_calls_wait_for_the_shared_rate_limit()
    {
        using var gate = new PeriodAuditGate(new PeriodAuditOptions { RpcRequestsPerSecond = 2 });
        var reader = gate.Limit(_reader);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) await reader.GetAccountAsync(TestData.Publisher);

        // 2 tokens at once, then 2 per second: the 5th call waits for the second refill.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Incomplete_history_is_reported()
    {
        for (var i = 0; i < 3; i++) Journal("2026-09-03");
        var sept = Close(Sep1, Sep30, []);

        var report = await new PeriodAuditService(_reader, _fetcher, NullLogger<PeriodAuditService>.Instance, new PeriodAuditOptions { Enabled = true, MaxPages = 0, RpcRequestsPerSecond = 10_000 })
            .AuditAsync(new PeriodAuditRequest { Network = SolanaNetwork.Devnet, Publisher = TestData.Publisher, LatestCloseSignature = sept.Sig });

        Assert.False(report.HistoryComplete);
        Assert.Contains(report.Warnings, w => w.StartsWith("History incomplete"));
    }
}
