using System.Net;
using OBC.Verify.Web.Services.Fetching;

namespace OBC.Verify.Web.Tests;

public class SafeHttpFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("ff02::1")]
    public void Blocks_non_public_addresses(string address)
    {
        Assert.True(UrlGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("20.60.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700::1111")]
    public void Allows_public_addresses(string address)
    {
        Assert.False(UrlGuard.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://example.com/a.json", "https")]
    [InlineData("ftp://example.com/a.json", "https")]
    [InlineData("file:///etc/passwd", "https")]
    [InlineData("https://user:pass@example.com/a.json", "credentials")]
    [InlineData("https://localhost/a.json", "Local")]
    [InlineData("https://foo.localhost/a.json", "Local")]
    [InlineData("https://127.0.0.1/a.json", "Non-public")]
    [InlineData("https://[::1]/a.json", "Non-public")]
    [InlineData("https://169.254.169.254/latest/meta-data", "Non-public")]
    [InlineData("https://0x7f000001/a.json", "Non-public")]
    [InlineData("relative/a.json", "absolute")]
    public void Rejects_unsafe_urls(string url, string expected)
    {
        var problem = UrlGuard.Check(url);
        Assert.NotNull(problem);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Accepts_public_https_url()
    {
        Assert.Null(UrlGuard.Check("https://mvp1.blob.core.windows.net/publicdocs/x.json"));
    }

    [Fact]
    public async Task Resolving_localhost_is_blocked()
    {
        await Assert.ThrowsAsync<BlockedAddressException>(() => SafeHttpFetcher.ResolveAllowedAsync("localhost"));
        await Assert.ThrowsAsync<BlockedAddressException>(() => SafeHttpFetcher.ResolveAllowedAsync("127.0.0.1"));
    }

    [Fact]
    public async Task Downloads_raw_bytes()
    {
        var handler = new StubHandler(_ => StubHandler.Ok([1, 2, 3]));
        var result = await Fetcher(handler).FetchAsync("https://files.example/a.json");

        Assert.True(result.Success);
        Assert.Equal([1, 2, 3], result.Content);
    }

    [Fact]
    public async Task Follows_safe_redirects_and_reports_final_url()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath == "/a.json"
            ? StubHandler.Redirect("/b.json")
            : StubHandler.Ok([9]));

        var result = await Fetcher(handler).FetchAsync("https://files.example/a.json");

        Assert.True(result.Success);
        Assert.Equal("https://files.example/b.json", result.FinalUrl);
    }

    [Theory]
    [InlineData("http://files.example/b.json")]
    [InlineData("https://127.0.0.1/b.json")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://localhost/b.json")]
    public async Task Rechecks_redirect_targets(string target)
    {
        var handler = new StubHandler(_ => StubHandler.Redirect(target));

        var result = await Fetcher(handler).FetchAsync("https://files.example/a.json");

        Assert.False(result.Success);
        Assert.Single(handler.Requests); // the redirect target was never requested
    }

    [Fact]
    public async Task Stops_after_too_many_redirects()
    {
        var handler = new StubHandler(r => StubHandler.Redirect(r.RequestUri!.AbsoluteUri + "x"));
        var result = await Fetcher(handler).FetchAsync("https://files.example/a");

        Assert.False(result.Success);
        Assert.Contains("redirects", result.Error);
    }

    [Fact]
    public async Task Enforces_size_cap()
    {
        var handler = new StubHandler(_ => StubHandler.Ok(new byte[2048]));
        var result = await Fetcher(handler, new SafeFetchOptions { MaxBytes = 1024 }).FetchAsync("https://files.example/a.json");

        Assert.False(result.Success);
        Assert.Contains("larger", result.Error);
    }

    [Fact]
    public async Task Enforces_size_cap_without_content_length()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[4096]))
        });
        var result = await Fetcher(handler, new SafeFetchOptions { MaxBytes = 1024 }).FetchAsync("https://files.example/a.json");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Requires_200()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await Fetcher(handler).FetchAsync("https://files.example/a.json");

        Assert.False(result.Success);
        Assert.Contains("404", result.Error);
    }

    [Fact]
    public async Task Times_out()
    {
        var handler = new DelayHandler();
        var result = await new SafeHttpFetcher(new HttpClient(handler), new SafeFetchOptions { Timeout = TimeSpan.FromMilliseconds(100) })
            .FetchAsync("https://files.example/a.json");

        Assert.False(result.Success);
        Assert.Contains("Timed out", result.Error);
    }

    [Fact]
    public async Task Real_handler_refuses_to_connect_to_loopback()
    {
        // The connect callback re-checks the resolved addresses (DNS rebinding), independently of UrlGuard.
        using var client = new HttpInvoker(SafeHttpFetcher.CreateHandler());
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://localhost:1/x"), default));
        Assert.IsType<BlockedAddressException>(ex.InnerException);
    }

    private static SafeHttpFetcher Fetcher(HttpMessageHandler handler, SafeFetchOptions? options = null) =>
        new(new HttpClient(handler), options ?? new SafeFetchOptions());

    private sealed class HttpInvoker(HttpMessageHandler handler) : HttpMessageInvoker(handler);

    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
