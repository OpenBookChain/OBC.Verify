using System.Net;
using System.Net.Sockets;

namespace OBC.Verify.Web.Services.Fetching;

public interface IDocumentFetcher
{
    Task<FetchResult> FetchAsync(string url, CancellationToken ct = default);
}

/// <param name="FinalUrl">The URL the content came from after redirects.</param>
public sealed record FetchResult(bool Success, byte[]? Content, string? FinalUrl, string? Error)
{
    public static FetchResult Ok(byte[] content, string finalUrl) => new(true, content, finalUrl, null);
    public static FetchResult Fail(string error) => new(false, null, null, error);
}

public sealed class SafeFetchOptions
{
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaxRedirects { get; set; } = 3;
}

/// <summary>
/// Downloads off-chain documents from URLs found on-chain without letting a memo or location account
/// reach internal services (section 6, step 6): https only, no private/loopback/link-local hosts
/// (checked on every redirect and again on the resolved addresses at connect time), a size cap and a timeout.
/// </summary>
public sealed class SafeHttpFetcher : IDocumentFetcher
{
    private readonly HttpClient _client;
    private readonly SafeFetchOptions _options;

    /// <param name="client">Must use <see cref="CreateHandler"/> (or a test handler) so redirects are not followed automatically.</param>
    public SafeHttpFetcher(HttpClient client, SafeFetchOptions options)
    {
        _client = client;
        _client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        _options = options;
    }

    /// <summary>
    /// Primary handler: no automatic redirects, no proxy, no decompression (the hash is over the raw bytes),
    /// and a connect callback that refuses blocked addresses after DNS resolution (DNS rebinding).
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await ResolveAllowedAsync(context.DnsEndPoint.Host, ct);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            var current = url;
            for (var redirects = 0; ; redirects++)
            {
                var problem = UrlGuard.Check(current);
                if (problem is not null) return FetchResult.Fail($"{problem} ({current})");

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirects >= _options.MaxRedirects) return FetchResult.Fail($"Too many redirects ({current}).");
                    if (response.Headers.Location is null) return FetchResult.Fail($"Redirect without a Location header ({current}).");
                    current = new Uri(new Uri(current), response.Headers.Location).AbsoluteUri;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return FetchResult.Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} ({current}).");
                }

                if (response.Content.Headers.ContentLength > _options.MaxBytes)
                {
                    return FetchResult.Fail($"Document is larger than {_options.MaxBytes:N0} bytes ({current}).");
                }

                var content = await ReadCappedAsync(response.Content, _options.MaxBytes, timeout.Token);
                return content is null
                    ? FetchResult.Fail($"Document is larger than {_options.MaxBytes:N0} bytes ({current}).")
                    : FetchResult.Ok(content, current);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FetchResult.Fail($"Timed out after {_options.Timeout.TotalSeconds:0} s ({url}).");
        }
        catch (HttpRequestException ex)
        {
            var reason = ex.InnerException is BlockedAddressException blocked ? blocked.Message : ex.Message;
            return FetchResult.Fail($"{reason} ({url})");
        }
        catch (BlockedAddressException ex)
        {
            return FetchResult.Fail($"{ex.Message} ({url})");
        }
    }

    /// <summary>Resolves a host and throws <see cref="BlockedAddressException"/> if any address is not public.</summary>
    public static async Task<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken ct = default)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0) throw new BlockedAddressException($"Host {host} did not resolve.");
        var blocked = addresses.FirstOrDefault(UrlGuard.IsBlockedAddress);
        if (blocked is not null) throw new BlockedAddressException($"Host {host} resolves to a non-public address ({blocked}).");
        return addresses;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

public sealed class BlockedAddressException(string message) : Exception(message);

public static class UrlGuard
{
    /// <summary>Returns why a URL may not be fetched, or null when it may.</summary>
    public static string? Check(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Not an absolute URL";
        if (uri.Scheme != Uri.UriSchemeHttps) return "Only https URLs are fetched";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "URLs with credentials are not fetched";

        var host = uri.IdnHost.TrimEnd('.');
        if (host.Length == 0) return "URL has no host";
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return "Local host names are not fetched";
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.DnsSafeHost, out var ip) && IsBlockedAddress(ip))
        {
            return "Non-public addresses are not fetched";
        }

        return null;
    }

    /// <summary>True for loopback, private, link-local, CGNAT, multicast, unspecified and other non-public ranges.</summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 "this network"
                || b[0] == 10                                  // 10.0.0.0/8
                || b[0] == 127                                 // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 CGNAT
                || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0.0/24 IETF
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // 198.18.0.0/15 benchmarking
                || b[0] >= 224;                                // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return true;                 // fc00::/7 unique local
            if (b[0] == 0x20 && b[1] == 0x02) return true;          // 2002::/16 6to4 can embed private IPv4
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b) return true; // 64:ff9b::/96 NAT64
            return false; // IPv4-mapped addresses were mapped to IPv4 above
        }

        return true;
    }
}
