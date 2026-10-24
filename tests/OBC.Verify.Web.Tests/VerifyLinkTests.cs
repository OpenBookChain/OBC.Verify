using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services;

namespace OBC.Verify.Web.Tests;

public class VerifyLinkTests
{
    private const string DevnetSample = "2dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP95";

    [Theory]
    [InlineData("devnet", SolanaNetwork.Devnet)]
    [InlineData("DevNet", SolanaNetwork.Devnet)]
    [InlineData("testnet", SolanaNetwork.Testnet)]
    [InlineData("TESTNET", SolanaNetwork.Testnet)]
    [InlineData("mainnet", SolanaNetwork.MainnetBeta)]
    [InlineData("mainnet-beta", SolanaNetwork.MainnetBeta)]
    [InlineData("Mainnet-Beta", SolanaNetwork.MainnetBeta)]
    [InlineData("MainnetBeta", SolanaNetwork.MainnetBeta)]
    [InlineData(" devnet ", SolanaNetwork.Devnet)]
    public void Parses_supported_networks(string value, SolanaNetwork expected)
    {
        Assert.True(VerifyLink.TryParseNetwork(value, out var network));
        Assert.Equal(expected, network);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localnet")]
    [InlineData("1")]
    [InlineData("<script>")]
    public void Unknown_network_falls_back_to_default(string? value)
    {
        Assert.False(VerifyLink.TryParseNetwork(value, out var network));
        Assert.Equal(VerifyLink.DefaultNetwork, network);
        Assert.Equal(SolanaNetwork.MainnetBeta, VerifyLink.DefaultNetwork);
    }

    [Theory]
    [InlineData(SolanaNetwork.MainnetBeta, "mainnet-beta")]
    [InlineData(SolanaNetwork.Devnet, "devnet")]
    [InlineData(SolanaNetwork.Testnet, "testnet")]
    public void Query_value_round_trips(SolanaNetwork network, string expected)
    {
        Assert.Equal(expected, VerifyLink.ToQueryValue(network));
        Assert.True(VerifyLink.TryParseNetwork(expected, out var parsed));
        Assert.Equal(network, parsed);
    }

    [Fact]
    public void Accepts_real_signature()
    {
        Assert.True(VerifyLink.IsValidSignature(DevnetSample));
        Assert.True(VerifyLink.IsValidSignature($"  {DevnetSample} "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5U1q...Kj7R")]
    [InlineData("abc")]
    [InlineData("2dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvyb")] // decodes to fewer than 64 bytes
    [InlineData("2dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP95z")] // too long
    [InlineData("0dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP95")] // '0' is not base58
    [InlineData("Odkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP95")] // 'O' is not base58
    [InlineData("2dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYW hUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP9")]
    public void Rejects_malformed_signature(string? value)
    {
        Assert.False(VerifyLink.IsValidSignature(value));
    }
}
