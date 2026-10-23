using OBC.Verify.Web.Services.Solana;

namespace OBC.Verify.Web.Services.Standard;

/// <summary>One location account visited while resolving locations.</summary>
public sealed class LocationHop
{
    public string Address { get; init; } = string.Empty;
    public LocationContent? Content { get; init; }
    /// <summary>Why this account gave no locations, null when it was read.</summary>
    public string? Error { get; init; }
}

public sealed class LocationResolution
{
    public List<LocationHop> Hops { get; } = new();
    /// <summary>Bases of every valid account visited, in order, without duplicates.</summary>
    public List<string> Bases { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Section 6, step 4: derive L from the signer, read it and follow <c>succ</c>.</summary>
public static class LocationResolver
{
    public const int MaxSuccessorHops = 5;

    public static async Task<LocationResolution> ResolveAsync(ISolanaReader reader, string publisher, CancellationToken ct = default)
    {
        var resolution = new LocationResolution();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var address = LocationAccount.DeriveAddress(publisher);

        for (var hop = 0; address is not null; hop++)
        {
            if (!visited.Add(address))
            {
                resolution.Warnings.Add($"Location successor cycle at {address}; stopped.");
                break;
            }
            if (hop > MaxSuccessorHops)
            {
                resolution.Warnings.Add($"More than {MaxSuccessorHops} location successor hops; stopped before {address}.");
                break;
            }

            ChainAccount? account;
            try
            {
                account = await reader.GetAccountAsync(address, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolution.Hops.Add(new LocationHop { Address = address, Error = $"RPC error: {ex.Message}" });
                break;
            }

            if (account is null)
            {
                resolution.Hops.Add(new LocationHop { Address = address, Error = "Account not found." });
                break;
            }

            LocationContent content;
            try
            {
                content = LocationAccount.Parse(account.Owner, account.Data);
            }
            catch (LocationAccountException ex)
            {
                resolution.Hops.Add(new LocationHop { Address = address, Error = ex.Message });
                break;
            }

            resolution.Hops.Add(new LocationHop { Address = address, Content = content });
            foreach (var b in content.Bases)
            {
                if (!resolution.Bases.Contains(b)) resolution.Bases.Add(b);
            }

            address = content.Successor;
        }

        return resolution;
    }
}
