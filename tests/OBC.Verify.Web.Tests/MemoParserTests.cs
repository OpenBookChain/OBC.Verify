using System.Text.Json;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class MemoParserTests
{
    private static readonly string H = new('a', 64);
    private static readonly string P = new('b', 64);

    [Fact]
    public void Parses_v3_memo()
    {
        var memo = MemoParser.Parse(TestData.MemoV3(H, 2, P, a: "https://x.example/a.json"));

        Assert.Equal("3", memo.Version);
        Assert.Equal(H, memo.Hash);
        Assert.Equal(TestData.DocumentId, memo.DocumentId);
        Assert.Equal(2, memo.VersionNumber);
        Assert.Equal(P, memo.PreviousHash);
        Assert.Equal("https://x.example/a.json", memo.FileUrl);
        Assert.Empty(MemoParser.ValidateV3(memo));
    }

    [Fact]
    public void Parses_devnet_first_version()
    {
        var memo = MemoParser.Parse("""{"v":"3","h":"c166b5ed15c756881ce9433885116746eb3ee707096ca1f9c6df2601f4fa2767","d":"1ee6871ee99332e2ffbd5b1ad429d4e450f207c2be600e019c69d3ec4efe7302","n":1}""");

        Assert.Empty(MemoParser.ValidateV3(memo));
        Assert.Null(memo.PreviousHash);
        Assert.Equal(string.Empty, memo.FileUrl);
    }

    [Theory]
    [InlineData("""{"v":"3","h":"HASH","n":1}""", "'d'")]
    [InlineData("""{"v":"3","h":"HASH","d":"x"}""", "'n'")]
    [InlineData("""{"v":"3","h":"HASH","d":"x","n":2}""", "'p'")]
    [InlineData("""{"v":"3","h":"HASH","d":"x","n":1,"p":"PREV"}""", "'p' must be absent")]
    [InlineData("""{"v":"3","h":"HASH","d":"x","n":0}""", "'n' must be 1")]
    [InlineData("""{"v":"3","h":"ABC","d":"x","n":1}""", "'h'")]
    [InlineData("""{"v":"3","h":"HASH","d":"x","n":2,"p":"nothex"}""", "'p' must be a 64-char")]
    public void Rejects_invalid_v3_memo(string template, string expected)
    {
        var memo = MemoParser.Parse(template.Replace("HASH", H).Replace("PREV", P));
        var errors = MemoParser.ValidateV3(memo);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Document_id_limit_is_in_utf8_bytes()
    {
        var fits = MemoParser.Parse(TestData.MemoV3(H, 1, d: new string('é', 64)));   // 64 chars, 128 bytes
        Assert.Empty(MemoParser.ValidateV3(fits));

        var tooLong = MemoParser.Parse(TestData.MemoV3(H, 1, d: new string('é', 65))); // 65 chars, 130 bytes
        Assert.Contains(MemoParser.ValidateV3(tooLong), e => e.Contains("128 UTF-8 bytes"));
    }

    [Theory]
    [InlineData("""{"v":"3","h":"x","d":"x","n":"1"}""")]
    [InlineData("""{"v":"3","h":"x","d":"x","n":1.5}""")]
    [InlineData("""{"v":3,"h":"x"}""")]
    [InlineData("""["v","3"]""")]
    [InlineData("not json")]
    public void Throws_on_wrong_json_types(string json)
    {
        Assert.Throws<FormatException>(() => MemoParser.Parse(json));
    }

    [Fact]
    public void V2_keeps_a1_a2_and_treats_d_n_p_as_tenant_fields()
    {
        var memo = MemoParser.Parse($$"""{"v":"2","h":"{{H}}","a":"https://a/1","a1":"https://a/2","a2":"https://a/3","n":"tenant","amount":"12"}""");

        Assert.Equal("https://a/1", memo.FileUrl);
        Assert.Equal("https://a/2", memo.FileUrl1);
        Assert.Equal("https://a/3", memo.FileUrl2);
        Assert.Null(memo.VersionNumber);
        Assert.Equal("tenant", ((JsonElement)memo.ExtraFields!["n"]!).GetString());
        Assert.True(memo.ExtraFields.ContainsKey("amount"));
    }

    [Fact]
    public void V3_ignores_a1_as_a_location()
    {
        var memo = MemoParser.Parse($$"""{"v":"3","h":"{{H}}","d":"x","n":1,"a1":"https://a/2"}""");
        Assert.Null(memo.FileUrl1);
        Assert.True(memo.ExtraFields!.ContainsKey("a1"));
    }
}
