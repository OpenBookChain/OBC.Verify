using System.Text;
using System.Text.Json;
using Solnet.Wallet;

namespace OBC.Verify.Web.Services.Standard;

/// <summary>Content of a v3 location account (section 5.3).</summary>
public sealed class LocationContent
{
    public int Version { get; init; }
    public long Sequence { get; init; }
    public List<string> Bases { get; init; } = new();
    public string? Successor { get; init; }
    public string Authority { get; init; } = string.Empty;
    public string Json { get; init; } = string.Empty;
}

public sealed class LocationAccountException(string message) : Exception(message);

/// <summary>The publisher's location account: an SPL Record account derived from the publisher wallet (section 5).</summary>
public static class LocationAccount
{
    public const string RecordProgramId = "recr1L3PCGKLbckBqMNcJhuuyU1zgo8nBhfLVsJNwr5";
    public const string Seed = "obc-meta";
    public const int HeaderSize = 33;
    public const byte RecordVersion = 1;
    public const int MaxBases = 10;

    /// <summary><c>CreateWithSeed(publisher, "obc-meta", RecordProgram)</c>.</summary>
    public static string DeriveAddress(string publisher)
    {
        if (!PublicKey.IsValid(publisher))
        {
            throw new ArgumentException($"'{publisher}' is not a valid public key.", nameof(publisher));
        }

        if (!PublicKey.TryCreateWithSeed(new PublicKey(publisher), Seed, new PublicKey(RecordProgramId), out var address))
        {
            throw new InvalidOperationException("Could not derive the location account address.");
        }

        return address.Key;
    }

    /// <summary>
    /// Checks the owner and record version, then parses the JSON after the 33-byte header with trailing zeros trimmed.
    /// Unknown fields are ignored. Throws <see cref="LocationAccountException"/> when the account is not a valid location account.
    /// </summary>
    public static LocationContent Parse(string owner, byte[] data)
    {
        if (owner != RecordProgramId)
        {
            throw new LocationAccountException($"Owner is {owner}, not the SPL Record program.");
        }
        if (data.Length < HeaderSize)
        {
            throw new LocationAccountException($"Account data is {data.Length} bytes, shorter than the {HeaderSize}-byte record header.");
        }
        if (data[0] != RecordVersion)
        {
            throw new LocationAccountException($"Record version byte is {data[0]}, expected {RecordVersion}.");
        }

        var authority = new PublicKey(data.AsSpan(1, 32).ToArray()).Key;

        var end = data.Length;
        while (end > HeaderSize && data[end - 1] == 0) end--;
        if (end == HeaderSize)
        {
            throw new LocationAccountException("The location account is empty.");
        }

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(data, HeaderSize, end - HeaderSize);
        }
        catch (DecoderFallbackException)
        {
            throw new LocationAccountException("The location account content is not valid UTF-8.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new LocationAccountException($"The location account content is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new LocationAccountException("The location account content is not a JSON object.");
            }

            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
            {
                throw new LocationAccountException("The location format version 'v' is missing or not an integer.");
            }
            if (version != 1)
            {
                throw new LocationAccountException($"Unsupported location format version {version}.");
            }

            if (!root.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var sequence))
            {
                throw new LocationAccountException("The sequence number 's' is missing or not an integer.");
            }

            if (!root.TryGetProperty("b", out var b) || b.ValueKind != JsonValueKind.Array)
            {
                throw new LocationAccountException("The base list 'b' is missing or not an array.");
            }

            var bases = new List<string>();
            foreach (var item in b.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new LocationAccountException("Every entry of 'b' must be a string.");
                }
                bases.Add(item.GetString()!);
            }
            if (bases.Count > MaxBases)
            {
                throw new LocationAccountException($"'b' lists {bases.Count} bases, more than the maximum of {MaxBases}.");
            }

            string? successor = null;
            if (root.TryGetProperty("succ", out var succ) && succ.ValueKind != JsonValueKind.Null)
            {
                if (succ.ValueKind != JsonValueKind.String || !PublicKey.IsValid(succ.GetString()))
                {
                    throw new LocationAccountException("'succ' is not a valid account address.");
                }
                successor = succ.GetString();
            }

            return new LocationContent
            {
                Version = version,
                Sequence = sequence,
                Bases = bases,
                Successor = successor,
                Authority = authority,
                Json = json
            };
        }
    }

    /// <summary>
    /// <c>{base}/{h}.json</c> for an https base, null for an opaque <c>label:</c> base or any other scheme.
    /// </summary>
    public static string? BuildUrl(string baseLocation, string hash)
    {
        if (!baseLocation.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        return $"{baseLocation.TrimEnd('/')}/{hash}.json";
    }
}
