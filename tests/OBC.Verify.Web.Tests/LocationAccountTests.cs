using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class LocationAccountTests
{
    private const string DevnetJson = """{"v":1,"s":4,"b":["https://mvp1.blob.core.windows.net/publicdocs"]}""";

    [Fact]
    public void Derives_devnet_location_address_from_publisher()
    {
        Assert.Equal(TestData.PublisherLocation, LocationAccount.DeriveAddress(TestData.Publisher));
    }

    [Fact]
    public void Rejects_invalid_publisher()
    {
        Assert.Throws<ArgumentException>(() => LocationAccount.DeriveAddress("not-a-key"));
    }

    [Fact]
    public void Parses_devnet_account_data()
    {
        // Header and first bytes of the devnet account as returned by getAccountInfo (authority = publisher).
        var raw = Convert.FromBase64String("AbgPzIXRn5BsNdjkWbv2uP8hfq9mqJ3PiLvomCKpYMeAeyJ2IjoxLCJzIjo0LCJiIjpbImh0dHBzOi8vbXZwMS5ibG9iLmNvcmUud2luZG93cy5uZXQvcHVibGljZG9jcyJdfQAAAAAAAAAA");

        var content = LocationAccount.Parse(LocationAccount.RecordProgramId, raw);

        Assert.Equal(1, content.Version);
        Assert.Equal(4, content.Sequence);
        Assert.Equal(["https://mvp1.blob.core.windows.net/publicdocs"], content.Bases);
        Assert.Null(content.Successor);
        Assert.Equal(TestData.Publisher, content.Authority);
        Assert.Equal(DevnetJson, content.Json);
    }

    [Fact]
    public void Trims_only_trailing_zeros_and_ignores_unknown_fields()
    {
        var data = FakeSolanaReader.LocationData("""{"v":1,"s":1,"b":["label:vault-1","https://a.example/x/"],"tenant":{"x":1}}""");
        var content = LocationAccount.Parse(LocationAccount.RecordProgramId, data);
        Assert.Equal(["label:vault-1", "https://a.example/x/"], content.Bases);
    }

    [Theory]
    [InlineData("11111111111111111111111111111111", 1, DevnetJson, "Owner")]
    [InlineData(LocationAccount.RecordProgramId, 2, DevnetJson, "version byte")]
    [InlineData(LocationAccount.RecordProgramId, 1, "", "empty")]
    [InlineData(LocationAccount.RecordProgramId, 1, "{not json", "not valid JSON")]
    [InlineData(LocationAccount.RecordProgramId, 1, """{"v":2,"s":1,"b":[]}""", "Unsupported location format")]
    [InlineData(LocationAccount.RecordProgramId, 1, """{"v":1,"b":[]}""", "'s'")]
    [InlineData(LocationAccount.RecordProgramId, 1, """{"v":1,"s":1}""", "'b'")]
    [InlineData(LocationAccount.RecordProgramId, 1, """{"v":1,"s":1,"b":[1]}""", "string")]
    [InlineData(LocationAccount.RecordProgramId, 1, """{"v":1,"s":1,"b":[],"succ":"nope"}""", "'succ'")]
    public void Rejects_invalid_accounts(string owner, byte version, string json, string expected)
    {
        var data = FakeSolanaReader.LocationData(json, version);
        var ex = Assert.Throws<LocationAccountException>(() => LocationAccount.Parse(owner, data));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Rejects_data_shorter_than_header()
    {
        Assert.Throws<LocationAccountException>(() => LocationAccount.Parse(LocationAccount.RecordProgramId, new byte[10]));
    }

    [Fact]
    public void Rejects_more_than_ten_bases()
    {
        var bases = string.Join(",", Enumerable.Range(0, 11).Select(i => $"\"https://h{i}.example\""));
        var data = FakeSolanaReader.LocationData($$"""{"v":1,"s":1,"b":[{{bases}}]}""");
        Assert.Throws<LocationAccountException>(() => LocationAccount.Parse(LocationAccount.RecordProgramId, data));
    }

    [Theory]
    [InlineData("https://a.example/x", "https://a.example/x/HASH.json")]
    [InlineData("https://a.example/x/", "https://a.example/x/HASH.json")]
    [InlineData("label:vault-1", null)]
    [InlineData("http://a.example/x", null)]
    public void Builds_candidate_urls(string baseLocation, string? expected)
    {
        Assert.Equal(expected, LocationAccount.BuildUrl(baseLocation, "HASH"));
    }

    [Fact]
    public async Task Resolver_follows_successor()
    {
        var reader = new FakeSolanaReader();
        var l2 = LocationAccount.DeriveAddress(TestData.OtherWallet);
        reader.SetLocation(TestData.Publisher, $$"""{"v":1,"s":9,"b":["https://old.example"],"succ":"{{l2}}"}""");
        reader.SetLocation(TestData.OtherWallet, """{"v":1,"s":1,"b":["https://new.example","https://old.example"]}""");

        var resolution = await LocationResolver.ResolveAsync(reader, TestData.Publisher);

        Assert.Equal([TestData.PublisherLocation, l2], resolution.Hops.Select(h => h.Address));
        Assert.Equal(["https://old.example", "https://new.example"], resolution.Bases);
        Assert.Empty(resolution.Warnings);
    }

    [Fact]
    public async Task Resolver_stops_on_cycle()
    {
        var reader = new FakeSolanaReader();
        var l2 = LocationAccount.DeriveAddress(TestData.OtherWallet);
        reader.SetLocation(TestData.Publisher, $$"""{"v":1,"s":1,"b":["https://a.example"],"succ":"{{l2}}"}""");
        reader.SetLocation(TestData.OtherWallet, $$"""{"v":1,"s":1,"b":["https://b.example"],"succ":"{{TestData.PublisherLocation}}"}""");

        var resolution = await LocationResolver.ResolveAsync(reader, TestData.Publisher);

        Assert.Equal(2, resolution.Hops.Count);
        Assert.Contains(resolution.Warnings, w => w.Contains("cycle"));
    }

    [Fact]
    public async Task Resolver_stops_after_five_successor_hops()
    {
        var reader = new FakeSolanaReader();
        var addresses = Enumerable.Range(0, 8).Select(_ => new Solnet.Wallet.Account().PublicKey.Key).ToList();
        reader.Accounts[TestData.PublisherLocation] = new ChainAccount(LocationAccount.RecordProgramId,
            FakeSolanaReader.LocationData($$"""{"v":1,"s":1,"b":[],"succ":"{{addresses[0]}}"}"""));
        for (var i = 0; i < addresses.Count - 1; i++)
        {
            reader.Accounts[addresses[i]] = new ChainAccount(LocationAccount.RecordProgramId,
                FakeSolanaReader.LocationData($$"""{"v":1,"s":1,"b":["https://h{{i}}.example"],"succ":"{{addresses[i + 1]}}"}"""));
        }

        var resolution = await LocationResolver.ResolveAsync(reader, TestData.Publisher);

        Assert.Equal(1 + LocationResolver.MaxSuccessorHops, resolution.Hops.Count);
        Assert.Contains(resolution.Warnings, w => w.Contains("hops"));
    }

    [Fact]
    public async Task Resolver_reports_missing_and_foreign_accounts()
    {
        var reader = new FakeSolanaReader();
        var missing = await LocationResolver.ResolveAsync(reader, TestData.Publisher);
        Assert.Equal("Account not found.", Assert.Single(missing.Hops).Error);
        Assert.Empty(missing.Bases);

        reader.SetLocation(TestData.Publisher, DevnetJson, owner: "11111111111111111111111111111111");
        var foreign = await LocationResolver.ResolveAsync(reader, TestData.Publisher);
        Assert.Contains("Owner", Assert.Single(foreign.Hops).Error);
        Assert.Empty(foreign.Bases);
    }
}
