using System.Text.Json;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class PeriodCloseTests
{
    // The same vectors as OBC's AccountingPeriodTests: members d = doc-{i}, n = i + 1, h = hex(SHA-256("h{i}")).
    [Theory]
    [InlineData(0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData(1, "fe5845309210c9e11800fefff8ebd5f34e5eb0eb7688ac006252d4f8d9ea4164")]
    [InlineData(2, "96b6c2daccf3185ddfc05891f5cbbd76072adc70240e8b6d75b5de9d35bd3e4a")]
    [InlineData(3, "7a5a97bdded18f7ff402b4723915b196e67ef7c823400a143cd022252f42a48b")]
    [InlineData(7, "a18152a88628396c54f324ce65b20a0ec0eca977ae97f8df983ad501720308dd")]
    public void Merkle_root_matches_reference_vectors(int count, string expected)
    {
        var members = Enumerable.Range(0, count)
            .Select(i => new PeriodMember { DocumentId = $"doc-{i}", Number = i + 1, Hash = TestData.Sha256(TestData.Bytes($"h{i}")), Type = "journal", Date = "2026-09-01" })
            .ToList();

        Assert.Equal(expected, PeriodClose.ComputeRoot(members));
    }

    [Fact]
    public void Valid_close_has_no_errors()
    {
        var close = Read(PeriodData.Close("2026-09-01", "2026-09-30", [("b", 1, H("b"), "2026-09-03", false), ("a", 2, H("a"), "2026-09-30", true)]));

        Assert.Empty(close.Check());
        Assert.True(close.IsClosed);
        Assert.Equal(["a", "b"], close.Members!.Select(m => m.DocumentId));
        Assert.True(close.Members![0].IsVoided);
        Assert.True(close.RootMatches);
        Assert.True(close.CountMatches);
    }

    [Fact]
    public void Count_and_root_mismatches_are_reported()
    {
        var json = PeriodData.Close("2026-09-01", "2026-09-30", [("a", 1, H("a"), "2026-09-03", false)])
            .Replace("\"count\":1", "\"count\":2");
        var data = JsonDocument.Parse(json).RootElement;
        var withWrongRoot = JsonSerializerRoot(data, new string('0', 64));

        var errors = Read(withWrongRoot).Check();

        Assert.Contains(errors, e => e.StartsWith("count: 2 does not match the 1 listed members"));
        Assert.Contains(errors, e => e.StartsWith("root: 0000"));
    }

    [Fact]
    public void Members_must_be_sorted_by_utf8_bytes_without_duplicates()
    {
        // UTF-16 ordinal order puts U+1F600 (surrogates D83D…) before U+FF61; UTF-8 byte order puts it after (F0… > EF…).
        var sorted = Read(PeriodData.Close("2026-09-01", "2026-09-30", [("｡", 1, H("x"), "2026-09-03", false), ("\U0001F600", 1, H("y"), "2026-09-03", false)]));
        Assert.Empty(sorted.Check());
        Assert.True(string.CompareOrdinal("\U0001F600", "｡") < 0);

        var unsorted = Read(PeriodData.Close("2026-09-01", "2026-09-30", [("b", 1, H("b"), "2026-09-03", false), ("a", 1, H("a"), "2026-09-03", false)], sort: false));
        Assert.Contains(unsorted.Check(), e => e.Contains("not sorted"));

        var duplicate = Read(PeriodData.Close("2026-09-01", "2026-09-30", [("a", 1, H("a"), "2026-09-03", false), ("a", 2, H("b"), "2026-09-03", false)], sort: false));
        Assert.Contains(duplicate.Check(), e => e.Contains("listed more than once"));
    }

    [Fact]
    public void Member_outside_the_range_is_an_error()
    {
        var close = Read(PeriodData.Close("2026-09-01", "2026-09-30", [("a", 1, H("a"), "2026-10-01", false)]));

        Assert.Contains(close.Check(), e => e.Contains("outside the period"));
    }

    [Fact]
    public void Reopen_has_no_members_and_close_requires_them()
    {
        Assert.Empty(Read(PeriodData.Reopen("2026-09-01", "2026-09-30")).Check());
        Assert.Contains(Read("""{"from":"2026-09-01","to":"2026-09-30","state":"reopened","count":0}""").Check(), e => e.Contains("no members"));
        Assert.Contains(Read("""{"from":"2026-09-01","to":"2026-09-30","state":"closed"}""").Check(), e => e.Contains("must have members"));
        Assert.Contains(Read("""{"from":"2026-09-30","to":"2026-09-01","state":"reopened"}""").Check(), e => e.Contains("before it starts"));
    }

    [Theory]
    [InlineData("7:obc:acc-period/2026-09-01/2026-09-30", "2026-09-01", "2026-09-30")]
    [InlineData("1ee6871ee99332e2ffbd5b1ad429d4e450f207c2be600e019c69d3ec4efe7302", null, null)]
    [InlineData("7:zoho:acc-period/2026-09-01/2026-09-30", null, null)]
    public void Public_ids_name_their_range(string d, string? from, string? to)
    {
        var range = PeriodClose.RangeFromPublicId(d);

        Assert.Equal(from, range?.From);
        Assert.Equal(to, range?.To);
    }

    private static string H(string s) => TestData.Sha256(TestData.Bytes(s));

    private static PeriodClose Read(string json) => PeriodClose.Read(JsonDocument.Parse(json).RootElement);

    private static string JsonSerializerRoot(JsonElement data, string root)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(data.GetRawText())!.AsObject();
        node["root"] = root;
        return node.ToJsonString();
    }
}

/// <summary>Builds acc-period-close data and envelopes the way OBC does.</summary>
internal static class PeriodData
{
    public static string Close(string from, string to, IEnumerable<(string D, long N, string H, string Date, bool Voided)> members, bool sort = true, string type = "journal")
    {
        var list = members.ToList();
        if (sort) list.Sort((a, b) => PeriodClose.CompareUtf8(a.D, b.D));
        var root = PeriodClose.ComputeRoot(list.Select(m => new PeriodMember { DocumentId = m.D, Number = m.N, Hash = m.H }).ToList());
        var items = list.Select(m =>
            $$"""{"d":{{JsonSerializer.Serialize(m.D)}},"n":{{m.N}},"h":"{{m.H}}","type":"{{type}}","date":"{{m.Date}}"{{(m.Voided ? ",\"status\":\"voided\"" : "")}}}""");
        return $$"""{"from":"{{from}}","to":"{{to}}","state":"closed","reason":"Month-end close","closedAt":"2026-10-05T14:02:11Z","count":{{list.Count}},"root":"{{root}}","members":[{{string.Join(",", items)}}]}""";
    }

    public static string Reopen(string from, string to) =>
        $$"""{"from":"{{from}}","to":"{{to}}","state":"reopened","reason":"Audit adjustment"}""";

    public static string Envelope(string data, string? previous = null) =>
        $$"""{"version":3,"type":"acc-period-close","schema":"obc.acc-period-close/1"{{(previous is null ? "" : $$""","links":[{"rel":"previous-period","type":"acc-period-close","h":"{{previous}}"}]""")}},"status":"active","data":{{data}}}""";
}
