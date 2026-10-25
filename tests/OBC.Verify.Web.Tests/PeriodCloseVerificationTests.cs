using Microsoft.Extensions.Logging.Abstractions;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

/// <summary>A single close or reopen verifies like any record: integrity, envelope, schema and version chain.</summary>
public class PeriodCloseVerificationTests
{
    private const string PeriodId = "7:obc:acc-period/2026-09-01/2026-09-30";
    private static readonly string MemberHash = TestData.Sha256(TestData.Bytes("member"));

    private readonly FakeSolanaReader _reader = new();
    private readonly FakeFetcher _fetcher = new();

    public PeriodCloseVerificationTests()
    {
        _reader.SetLocation(TestData.Publisher, $$"""{"v":1,"s":1,"b":["{{TestData.Base}}"]}""");
    }

    private Task<VerificationResult> Verify(string signature) =>
        new VerificationService(_reader, _fetcher, NullLogger<VerificationService>.Instance).VerifyAsync(signature, SolanaNetwork.Devnet);

    private string Store(string content)
    {
        var bytes = TestData.Bytes(content);
        var h = TestData.Sha256(bytes);
        _fetcher.Files[$"{TestData.Base}/{h}.json"] = bytes;
        return h;
    }

    private static string SeptemberClose(string? previous = null) =>
        PeriodData.Envelope(PeriodData.Close("2026-09-01", "2026-09-30", [("doc-a", 1, MemberHash, "2026-09-03", false)]), previous);

    [Fact]
    public void Envelope_validator_recognizes_a_close()
    {
        var result = EnvelopeValidator.Validate(TestData.Bytes(SeptemberClose(previous: new string('c', 64))));

        Assert.True(result.EnvelopeValid, string.Join("; ", result.EnvelopeErrors));
        Assert.True(result.TypeRecognized);
        Assert.Empty(result.DataErrors);
        Assert.Contains(PeriodClose.SchemaId, EnvelopeValidator.KnownSchemaIds);
        Assert.Equal(new DateOnly(2026, 9, 1), result.PeriodClose!.From);
        Assert.Equal(new string('c', 64), result.PeriodClose.PreviousPeriodHash);
        Assert.Null(result.BookingDate);
    }

    [Fact]
    public void Envelope_validator_reports_schema_and_root_errors()
    {
        var badRoot = SeptemberClose().Replace(PeriodClose.ComputeRoot([new PeriodMember { DocumentId = "doc-a", Number = 1, Hash = MemberHash }]), new string('0', 64));
        Assert.Contains(EnvelopeValidator.Validate(TestData.Bytes(badRoot)).DataErrors, e => e.StartsWith("root:"));

        var badState = PeriodData.Envelope("""{"from":"2026-09-01","to":"2026-09-30","state":"pending"}""");
        Assert.NotEmpty(EnvelopeValidator.Validate(TestData.Bytes(badState)).DataErrors);

        var badDate = PeriodData.Envelope("""{"from":"September","to":"2026-09-30","state":"reopened"}""");
        Assert.NotEmpty(EnvelopeValidator.Validate(TestData.Bytes(badDate)).DataErrors);
    }

    [Fact]
    public void Journal_booking_date_comes_from_the_registry()
    {
        var result = EnvelopeValidator.Validate(TestData.Bytes(TestData.Envelope()));

        Assert.Equal(new DateOnly(2026, 4, 16), result.BookingDate);
    }

    [Fact]
    public async Task Close_verifies_with_count_root_and_previous_period_link()
    {
        var august = Store(PeriodData.Envelope(PeriodData.Close("2026-08-01", "2026-08-31", [])));
        var h = Store(SeptemberClose(previous: august));
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, d: PeriodId));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.True(result.TypeRecognized);
        Assert.Equal("acc-period-close", result.DocumentType);
        Assert.Equal(1, result.PeriodClose!.Count);
        Assert.True(result.PeriodClose.RootMatches);
        Assert.Equal(august, result.PeriodClose.PreviousPeriodHash);
        Assert.Contains(result.Steps, s => s.Name == "Check Period Close" && s.Passed && s.Detail!.Contains("recomputed and match"));
        Assert.Equal($"{TestData.Base}/{august}.json", Assert.Single(result.Links).ResolvedUrl);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Close_and_reopen_form_a_version_chain()
    {
        var h1 = Store(SeptemberClose());
        var h2 = Store(PeriodData.Envelope(PeriodData.Reopen("2026-09-01", "2026-09-30")));
        var s1 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h1, 1, d: PeriodId));
        var s2 = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h2, 2, h1, d: PeriodId));

        var close = await Verify(s1);
        Assert.True(close.IsVerified, close.ErrorMessage);
        Assert.Equal(2, close.Chain!.Latest!.Number);

        var reopen = await Verify(s2);
        Assert.True(reopen.IsVerified, reopen.ErrorMessage);
        Assert.Equal(PeriodClose.ReopenedState, reopen.PeriodClose!.State);
        Assert.Null(reopen.PeriodClose.Members);
        Assert.Equal(s1, reopen.Chain!.Previous!.Signature);
    }

    [Fact]
    public async Task Root_mismatch_fails_verification()
    {
        var data = PeriodData.Close("2026-09-01", "2026-09-30", [("doc-a", 1, MemberHash, "2026-09-03", false)]).Replace("\"count\":1", "\"count\":5");
        var h = Store(PeriodData.Envelope(data));
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, d: PeriodId));

        var result = await Verify(sig);

        Assert.False(result.IsVerified);
        Assert.Equal(h, result.ComputedHash);
        Assert.Contains("count: 5 does not match", result.ErrorMessage);
    }

    [Fact]
    public async Task Public_id_naming_another_range_is_a_warning()
    {
        var h = Store(SeptemberClose());
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h, 1, d: "7:obc:acc-period/2026-10-01/2026-10-31"));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.Contains(result.Warnings, w => w.Contains("names the period 2026-10-01 – 2026-10-31"));
    }

    [Fact]
    public async Task Voided_period_is_a_tombstone()
    {
        var h1 = Store(SeptemberClose());
        var h2 = Store("""{"version":3,"type":"acc-period-close","schema":"obc.acc-period-close/1","status":"voided","reason":"wrong range","data":{}}""");
        _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h1, 1, d: PeriodId));
        var sig = _reader.AddMemo(TestData.Publisher, TestData.MemoV3(h2, 2, h1, d: PeriodId));

        var result = await Verify(sig);

        Assert.True(result.IsVerified, result.ErrorMessage);
        Assert.True(result.IsVoided);
        Assert.Null(result.PeriodClose);
    }
}
