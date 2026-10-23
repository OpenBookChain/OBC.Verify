using Microsoft.Extensions.Logging.Abstractions;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services;

namespace OBC.Verify.Web.Tests;

public class VerificationServiceTests
{
    private readonly FakeSolanaReader _reader = new();
    private readonly FakeFetcher _fetcher = new();
    private VerificationService Service => new(_reader, _fetcher, NullLogger<VerificationService>.Instance);

    private Task<VerificationResult> Verify(string signature, string? expectedSigner = null) =>
        Service.VerifyAsync(signature, SolanaNetwork.Devnet, expectedSigner);

    private string Store(string content, string? url = null)
    {
        var bytes = TestData.Bytes(content);
        var h = TestData.Sha256(bytes);
        _fetcher.Files[url ?? $"{TestData.Base}/{h}.json"] = bytes;
        return h;
    }

    [Fact]
    public async Task V3_verifies_through_location_account()
    {
        _reader.SetLocation(TestData.Publisher, """{"v":1,"s":4,"b":["label:vault","https://dead.example","https://mvp1.blob.core.windows.net/publicdocs"]}""");
        var h = Store(TestData.Envelope());
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal(TestData.Publisher, result.Signer);
        Assert.Equal(TestData.PublisherLocation, result.LocationAccount);
        Assert.Equal($"{TestData.Base}/{h}.json", result.FileUrl);
        Assert.Equal(["https://dead.example/" + h + ".json", $"{TestData.Base}/{h}.json"], result.CandidateUrls);
        Assert.Equal("journal", result.DocumentType);
        Assert.Equal("obc.journal/2", result.SchemaId);
        Assert.True(result.TypeRecognized);
        Assert.False(result.Chain!.IsSuperseded);
    }

    [Fact]
    public async Task V3_falls_back_to_memo_hint_without_location_account()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store(TestData.Envelope(), hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal(hint, result.FileUrl);
        Assert.Contains(result.Steps, s => s.Name == "Resolve Location Account" && s.IsWarning);
    }

    [Fact]
    public async Task V3_without_any_location_fails()
    {
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(new string('a', 64), 1));

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Contains("No storage location", result.ErrorMessage);
    }

    [Fact]
    public async Task V3_superseded_record_is_still_authentic()
    {
        _reader.SetLocation(TestData.Publisher, """{"v":1,"s":1,"b":["https://mvp1.blob.core.windows.net/publicdocs"]}""");
        var h1 = Store(TestData.Envelope());
        var h2 = Store(TestData.Envelope(extra: ",\"sourceModifiedAt\":\"2026-10-01T00:00:00Z\""));
        var h3 = Store("""{"version":3,"type":"journal","schema":"obc.journal/2","status":"voided","reason":"voided in source system","data":{}}""");
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h1, 1));
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h2, 2, h1));
        var s3 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h3, 3, h2));

        var first = await Verify(s1);
        Assert.True(first.IsVerified, first.ErrorMessage);
        Assert.Equal(3, first.Chain!.Latest!.Number);
        Assert.True(first.Chain.LatestIsVoided);

        var last = await Verify(s3);
        Assert.True(last.IsVerified, last.ErrorMessage);
        Assert.True(last.IsVoided);
        Assert.Equal("voided in source system", last.VoidReason);
        Assert.Equal(s2, last.Chain!.Previous!.Signature);
        Assert.False(last.Chain.IsSuperseded);
    }

    [Fact]
    public async Task V3_unknown_type_is_integrity_verified()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store("""{"version":3,"type":"invoice","schema":"obc.invoice/1","data":{"x":1}}""", hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.False(result.TypeRecognized);
        Assert.Contains(result.Steps, s => s.Detail?.Contains("type not recognized") == true);
    }

    [Fact]
    public async Task V3_invalid_envelope_fails_after_hash_match()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store("""{"version":3,"type":"journal","data":{}}""", hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint));

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Equal(h, result.ComputedHash);
        Assert.Contains("not a valid v3 envelope", result.ErrorMessage);
    }

    [Fact]
    public async Task V3_rejects_invalid_memo()
    {
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"3","h":"{{new string('a', 64)}}","d":"x","n":2}""");

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Contains("'p'", result.ErrorMessage);
    }

    [Fact]
    public async Task V3_tampered_copy_is_skipped_for_a_matching_one()
    {
        _reader.SetLocation(TestData.Publisher, """{"v":1,"s":1,"b":["https://evil.example","https://mvp1.blob.core.windows.net/publicdocs"]}""");
        var h = Store(TestData.Envelope());
        _fetcher.Files[$"https://evil.example/{h}.json"] = TestData.Bytes("{}");
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1));

        var result = await Verify(sig);

        Assert.True(result.IsVerified);
        Assert.Contains(result.Warnings, w => w.Contains("evil.example"));
    }

    [Fact]
    public async Task Rejects_failed_transaction()
    {
        var sig = _reader.AddMemo(TestData.Publisher, """{"v":"2","h":"x","a":"https://x"}""", error: "{\"InstructionError\":[0,\"Custom\"]}");

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Contains("meta.err", result.ErrorMessage);
        Assert.Empty(_fetcher.Requests);
    }

    [Fact]
    public async Task Rejects_unknown_transaction()
    {
        var result = await Verify("missing");
        Assert.False(result.IsVerified);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task Rejects_unexpected_signer()
    {
        var h = Store("{}", "https://a.example/a.json");
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"2","h":"{{h}}","a":"https://a.example/a.json"}""");

        var mismatch = await Verify(sig, TestData.OtherWallet);
        Assert.False(mismatch.IsVerified);
        Assert.Contains("not by the expected wallet", mismatch.ErrorMessage);

        var match = await Verify(sig, TestData.Publisher);
        Assert.True(match.IsVerified, match.ErrorMessage);
    }

    [Fact]
    public async Task V3_without_memo_signer_fails()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store(TestData.Envelope(), hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint), memoSigners: []);

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Contains("lists no signer", result.ErrorMessage);
        Assert.Null(result.Signer);
        Assert.Empty(_fetcher.Requests);
    }

    [Fact]
    public async Task V2_without_memo_signer_falls_back_to_fee_payer()
    {
        var url = "https://files.example/f.json";
        var h = Store("{}", url);
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"2","h":"{{h}}","a":"{{url}}"}""", memoSigners: []);

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal(TestData.Publisher, result.Signer);
        var step = Assert.Single(result.Steps, s => s.Name == "Identify Signer");
        Assert.True(step.IsWarning);
        Assert.Contains("fee payer; the memo lists no signer", step.Detail);

        // For v1/v2 the fee payer may stand in for the expected wallet when the memo lists no signer.
        var expected = await Verify(sig, TestData.Publisher);
        Assert.True(expected.IsVerified, expected.ErrorMessage);
    }

    [Fact]
    public async Task Expected_signer_must_sign_the_memo_not_just_pay_the_fee()
    {
        // The publisher pays the fee, but the memo instruction is signed by another wallet.
        var url = "https://files.example/f.json";
        var h = Store("{}", url);
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"2","h":"{{h}}","a":"{{url}}"}""", memoSigners: [TestData.OtherWallet]);

        var result = await Verify(sig, TestData.Publisher);

        Assert.False(result.IsVerified);
        Assert.Contains("not by the expected wallet", result.ErrorMessage);
        Assert.Equal(TestData.OtherWallet, result.Signer);
    }

    [Fact]
    public async Task Expected_signer_is_chosen_among_several_memo_signers()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store(TestData.Envelope(), hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint), memoSigners: [TestData.OtherWallet, TestData.Publisher]);

        var result = await Verify(sig, TestData.Publisher);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal(TestData.Publisher, result.Signer);
        Assert.Equal(TestData.PublisherLocation, result.LocationAccount);
    }

    [Fact]
    public async Task Reports_links_beyond_the_resolve_limit()
    {
        var hint = "https://hint.example/doc.json";
        var links = string.Join(",", Enumerable.Range(0, 12).Select(i => $$"""{"rel":"source","h":"{{i.ToString("x").PadLeft(64, '0')}}"}"""));
        var h = Store(TestData.Envelope(extra: $",\"links\":[{links}]"), hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal(12, result.Links.Count);
        Assert.Contains(result.Warnings, w => w.StartsWith("2 more links not checked"));
        Assert.Contains("2 more links not checked", result.Steps.Single(s => s.Name == "Report Links").Detail);
    }

    [Fact]
    public async Task Reports_incomplete_history_instead_of_latest()
    {
        var hint = "https://hint.example/doc.json";
        var h = Store(TestData.Envelope(), hint);
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, a: hint));
        for (var i = 0; i < 1000; i++) _reader.AddMemo(TestData.Publisher, "noise");

        var result = await new VerificationService(_reader, _fetcher, NullLogger<VerificationService>.Instance, new() { MaxPages = 1 })
            .VerifyAsync(sig, SolanaNetwork.Devnet);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.False(result.Chain!.ScanComplete);
        var step = result.Steps.Single(s => s.Name == "Check Version Chain");
        Assert.Contains("history is incomplete", step.Detail);
        Assert.DoesNotContain("this is the latest version", step.Detail);
        Assert.Contains(result.Warnings, w => w.StartsWith("History incomplete"));
    }

    [Fact]
    public async Task V2_verifies_as_before_and_reports_signer()
    {
        // v2 documents have no envelope and are not schema-validated, as before.
        var url = "https://files.example/file-1.json";
        var h = Store("""{"version":2,"anything":"goes"}""", url);
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"2","h":"{{h}}","a":"{{url}}","amount":"12"}""");

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal("2", result.StandardVersion);
        Assert.Equal(url, result.FileUrl);
        Assert.Equal(TestData.Publisher, result.Signer);
        Assert.Null(result.Chain);
        Assert.Null(result.LocationAccount);
        Assert.Equal([url], _fetcher.Requests);
    }

    [Fact]
    public async Task V2_tries_a1_and_a2()
    {
        var h = Store("""{"version":2}""", "https://replica2.example/f.json");
        var sig = _reader.AddMemo(TestData.Publisher,
            $$"""{"v":"2","h":"{{h}}","a":"https://gone.example/f.json","a1":"https://gone2.example/f.json","a2":"https://replica2.example/f.json"}""");

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Equal("https://replica2.example/f.json", result.FileUrl);
        Assert.Equal(3, _fetcher.Requests.Count);
    }

    [Fact]
    public async Task V1_hash_mismatch_still_fails()
    {
        _fetcher.Files["https://files.example/f.json"] = TestData.Bytes("tampered");
        var sig = _reader.AddMemo(TestData.Publisher, $$"""{"v":"1","h":"{{new string('a', 64)}}","a":"https://files.example/f.json"}""");

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.StartsWith("Hash mismatch", result.ErrorMessage);
        Assert.Equal(TestData.Sha256(TestData.Bytes("tampered")), result.ComputedHash);
    }

    [Fact]
    public async Task Unsupported_version_fails()
    {
        var sig = _reader.AddMemo(TestData.Publisher, """{"v":"4","h":"x"}""");
        var result = await Verify(sig);
        Assert.False(result.IsVerified);
        Assert.Contains("Unsupported standard version", result.ErrorMessage);
    }
}
