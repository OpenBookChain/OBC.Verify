using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class VersionChainResolverTests
{
    private static readonly string H1 = new('1', 64);
    private static readonly string H2 = new('2', 64);
    private static readonly string H3 = new('3', 64);

    private readonly FakeSolanaReader _reader = new();

    private ChainVersion Current(string signature)
    {
        var tx = _reader.Transactions[signature];
        var memo = MemoParser.Parse(tx.Memo!);
        return new ChainVersion
        {
            Signature = signature,
            DocumentId = memo.DocumentId!,
            Number = memo.VersionNumber!.Value,
            Hash = memo.Hash,
            PreviousHash = memo.PreviousHash,
            Slot = tx.Slot,
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(tx.BlockTime!.Value)
        };
    }

    private Task<VersionChainReport> Resolve(string signature, Func<ChainVersion, CancellationToken, Task<(bool, string?)?>>? probe = null, int maxPages = 20) =>
        new VersionChainResolver(_reader, maxPages).ResolveAsync(TestData.Publisher, Current(signature), probe);

    private (string S1, string S2, string S3) PublishChainOfThree()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        _reader.AddMemo(TestData.Publisher, """{"v":"2","h":"0000000000000000000000000000000000000000000000000000000000000000","a":"https://x"}""");
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1));
        var s3 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 3, H2));
        return (s1, s2, s3);
    }

    [Fact]
    public async Task First_version_is_superseded_by_latest()
    {
        var (s1, _, s3) = PublishChainOfThree();

        var report = await Resolve(s1, (_, _) => Task.FromResult<(bool, string?)?>((false, null)));

        Assert.Equal([1L, 2L, 3L], report.Versions.Select(v => v.Number));
        Assert.Null(report.Previous);
        Assert.True(report.IsSuperseded);
        Assert.Equal(3, report.Latest!.Number);
        Assert.Equal(s3, report.Latest.Signature);
        Assert.False(report.LatestIsVoided);
        Assert.True(report.ScanComplete);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Latest_version_reports_previous()
    {
        var (_, s2, s3) = PublishChainOfThree();

        var report = await Resolve(s3);

        Assert.Equal(s2, report.Previous!.Signature);
        Assert.Equal(2, report.Previous.Number);
        Assert.False(report.IsSuperseded);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Ignores_other_signers_failed_transactions_and_other_documents()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        // Another wallet reuses d and mentions the publisher's address so it shows up in its history.
        var spam = _reader.AddMemo(TestData.OtherWallet, TestData.MemoV3(H2, 2, H1));
        _reader.AddHistory(TestData.Publisher, new ChainSignature(spam, 5000, null, false, $"[1] {TestData.MemoV3(H2, 2, H1)}"));
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 2, H1), error: "{\"InstructionError\":[0,\"Custom\"]}");
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 2, H1, d: "other-document"));

        var report = await Resolve(s1);

        Assert.Single(report.Versions);
        Assert.False(report.IsSuperseded);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Skips_transactions_whose_memo_does_not_mention_d()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        for (var i = 0; i < 50; i++) _reader.AddMemo(TestData.Publisher, $"spam {i}");

        await Resolve(s1);

        Assert.Equal(0, _reader.TransactionReads);
    }

    [Fact]
    public async Task Reports_chain_gap_when_previous_is_missing()
    {
        var s3 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 3, H2));

        var report = await Resolve(s3);

        Assert.Null(report.Previous);
        Assert.Contains(report.Warnings, w => w.Contains("Chain gap") && w.Contains("previous version 2"));
        Assert.Contains(report.Warnings, w => w.Contains("Chain gap: version 1"));
    }

    [Fact]
    public async Task Reports_gap_when_previous_hash_does_not_match()
    {
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H3));

        var report = await Resolve(s2);

        Assert.Null(report.Previous);
        Assert.Contains(report.Warnings, w => w.Contains("Chain gap"));
    }

    [Fact]
    public async Task Reports_fork_on_same_number()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1));
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 2, H1));

        var report = await Resolve(s1);

        Assert.Single(report.Warnings, w => w.StartsWith("Fork"));
        Assert.Contains("version 2", report.Warnings.Single(w => w.StartsWith("Fork")));
        Assert.Equal(2, report.Latest!.Number);
    }

    [Fact]
    public async Task Reports_fork_on_same_previous_hash()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1));
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 3, H1));

        var report = await Resolve(s1);

        Assert.Contains(report.Warnings, w => w.Contains("share the previous hash"));
    }

    [Fact]
    public async Task Reports_voided_latest()
    {
        var (s1, _, _) = PublishChainOfThree();
        ChainVersion? probed = null;

        var report = await Resolve(s1, (v, _) =>
        {
            probed = v;
            return Task.FromResult<(bool, string?)?>((true, "deleted on tenant request"));
        });

        Assert.Equal(3, probed!.Number);
        Assert.True(report.LatestIsVoided);
        Assert.Equal("deleted on tenant request", report.LatestVoidReason);
    }

    [Fact]
    public async Task Warns_when_latest_cannot_be_probed()
    {
        var (s1, _, _) = PublishChainOfThree();
        var report = await Resolve(s1, (_, _) => Task.FromResult<(bool, string?)?>(null));

        Assert.Null(report.LatestIsVoided);
        Assert.Contains(report.Warnings, w => w.Contains("tombstone"));
    }

    [Fact]
    public async Task Pages_through_history()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        for (var i = 0; i < VersionChainResolver.PageSize + 10; i++) _reader.AddMemo(TestData.Publisher, "noise");
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1));

        var report = await Resolve(s1);

        Assert.True(report.ScanComplete);
        Assert.Equal(VersionChainResolver.PageSize + 12, report.ScannedSignatures);
        Assert.Equal(2, report.Latest!.Number);
    }

    private static string K => DocumentLookupKey.Derive(TestData.Publisher, TestData.DocumentId);

    [Fact]
    public async Task Finds_versions_through_lookup_key()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1), lookupKey: K, withSummary: false);
        for (var i = 0; i < 20; i++) _reader.AddMemo(TestData.Publisher, $"unrelated {i}");
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1), lookupKey: K, withSummary: false);
        _reader.History[TestData.Publisher].Clear(); // only k knows these records

        var report = await Resolve(s1);

        Assert.Equal(ChainDiscovery.LookupKey, report.DiscoveredVia);
        Assert.Equal(K, report.LookupKey);
        Assert.Equal([s1, s2], report.Versions.Select(v => v.Signature));
        Assert.Equal(2, report.Latest!.Number);
        Assert.Equal(2, report.ScannedSignatures);
        Assert.Equal(1, _reader.TransactionReads); // only s2 is read; the memo summary isn't needed
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Lookup_key_entries_with_unrelated_memo_summary_are_not_read()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1), lookupKey: K);
        // Spam sent to k by anyone, with a memo summary that doesn't mention d.
        for (var i = 0; i < 30; i++) _reader.AddMemo(TestData.OtherWallet, $"spam {i}", lookupKey: K);

        var report = await Resolve(s1);

        Assert.Equal(ChainDiscovery.LookupKey, report.DiscoveredVia);
        Assert.Equal(0, _reader.TransactionReads);
        Assert.Single(report.Versions);
    }

    [Fact]
    public async Task Default_scan_bound_is_three_pages()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        for (var i = 0; i < VersionChainResolver.PageSize * 4; i++) _reader.AddMemo(TestData.Publisher, "noise");

        var report = await new VersionChainResolver(_reader).ResolveAsync(TestData.Publisher, Current(s1), null);

        Assert.False(report.ScanComplete);
        Assert.Equal(3 * VersionChainResolver.PageSize, report.ScannedSignatures);
        Assert.Equal(3, new ChainScanOptions().MaxPages);
    }

    [Fact]
    public async Task Lookup_key_ignores_records_of_other_signers()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1), lookupKey: K);
        // Anyone can send to k; a memo with the same d signed by another wallet doesn't count.
        _reader.AddMemo(TestData.OtherWallet, TestData.MemoV3(H2, 2, H1), lookupKey: K);

        var report = await Resolve(s1);

        Assert.Equal(ChainDiscovery.LookupKey, report.DiscoveredVia);
        Assert.Single(report.Versions);
        Assert.False(report.IsSuperseded);
    }

    [Fact]
    public async Task Falls_back_to_signer_history_when_lookup_key_lists_nothing()
    {
        // Records published before the p-memo switch carry no k.
        var (s1, _, s3) = PublishChainOfThree();

        var report = await Resolve(s1);

        Assert.Equal(ChainDiscovery.SignerHistory, report.DiscoveredVia);
        Assert.Equal(3, report.Versions.Count);
        Assert.Equal(s3, report.Latest!.Signature);
    }

    [Fact]
    public async Task Fallback_scan_reads_transactions_without_memo_summary()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1), withSummary: false);

        var report = await Resolve(s1);

        Assert.Equal(s2, report.Latest!.Signature);
    }

    [Fact]
    public async Task Scans_signer_history_for_versions_published_before_the_lookup_key()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1)); // SPL Memo, no k
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2, H1), lookupKey: K);
        var s3 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H3, 3, H2), lookupKey: K);

        var report = await Resolve(s3);

        Assert.Equal(ChainDiscovery.LookupKeyAndSignerHistory, report.DiscoveredVia);
        Assert.Equal([s1, s2, s3], report.Versions.Select(v => v.Signature));
        Assert.Equal(s2, report.Previous!.Signature);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public async Task Warns_about_invalid_memo_for_the_same_document()
    {
        // Like the devnet k test records: n = 2 without p.
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1), lookupKey: K);
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H2, 2), lookupKey: K);

        var report = await Resolve(s1);

        Assert.Single(report.Versions);
        Assert.Contains(report.Warnings, w => w.Contains("not a valid v3 memo") && w.Contains("'p'"));
    }

    [Fact]
    public async Task Warns_when_scan_is_cut_short()
    {
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(H1, 1));
        for (var i = 0; i < VersionChainResolver.PageSize * 2; i++) _reader.AddMemo(TestData.Publisher, "noise");

        var report = await Resolve(s1, maxPages: 1);

        Assert.False(report.ScanComplete);
        Assert.Contains(report.Warnings, w => w.StartsWith("History incomplete") && w.Contains("stopped after"));
    }
}
