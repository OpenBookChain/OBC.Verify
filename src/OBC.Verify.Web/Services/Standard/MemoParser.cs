using System.Text.Json;
using OBC.Verify.Web.Models;

namespace OBC.Verify.Web.Services.Standard;

public static class MemoParser
{
    /// <summary>
    /// Parses an on-chain memo. Throws <see cref="FormatException"/> when it is not a JSON object
    /// or a standard field has the wrong JSON type. v3 rules (section 6, step 2) are checked by <see cref="ValidateV3"/>.
    /// </summary>
    public static OnChainMemo Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException(ex.Message, ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The memo is not a JSON object.");
            }

            var memo = new OnChainMemo
            {
                Version = GetString(root, "v") ?? throw new FormatException("The memo has no 'v' field."),
                Hash = GetString(root, "h") ?? string.Empty,
                FileUrl = GetString(root, "a") ?? string.Empty
            };

            var isV3 = memo.Version == "3";
            var known = new HashSet<string>(StringComparer.Ordinal) { "v", "h", "a" };
            if (isV3)
            {
                // a1/a2 are not used in v3, and d/n/p only have a meaning in v3. In v1/v2 they are tenant fields.
                memo.DocumentId = GetString(root, "d");
                memo.PreviousHash = GetString(root, "p");
                if (root.TryGetProperty("n", out var n))
                {
                    memo.VersionNumber = n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var value)
                        ? value
                        : throw new FormatException("'n' must be a JSON integer.");
                }
                known.UnionWith(["d", "n", "p"]);
            }
            else
            {
                memo.FileUrl1 = GetString(root, "a1");
                memo.FileUrl2 = GetString(root, "a2");
                known.UnionWith(["a1", "a2"]);
            }

            foreach (var property in root.EnumerateObject())
            {
                if (known.Contains(property.Name)) continue;
                memo.ExtraFields ??= new Dictionary<string, object?>();
                memo.ExtraFields[property.Name] = property.Value.Clone();
            }

            return memo;
        }
    }

    /// <summary>Returns the problems with a v3 memo (section 3 and section 6, step 2). Empty when valid.</summary>
    public static List<string> ValidateV3(OnChainMemo memo)
    {
        var errors = new List<string>();
        if (memo.Version != "3") errors.Add($"'v' must be \"3\", not \"{memo.Version}\".");
        if (!IsHash(memo.Hash)) errors.Add("'h' must be a 64-char lowercase hex SHA-256.");
        if (string.IsNullOrEmpty(memo.DocumentId)) errors.Add("'d' (document id) is missing.");
        else if (System.Text.Encoding.UTF8.GetByteCount(memo.DocumentId) > 128) errors.Add("'d' (document id) is longer than 128 UTF-8 bytes.");

        if (memo.VersionNumber is null) errors.Add("'n' (version number) is missing.");
        else if (memo.VersionNumber < 1) errors.Add("'n' must be 1 or higher.");
        else if (memo.VersionNumber == 1 && memo.PreviousHash is not null) errors.Add("'p' must be absent when n = 1.");
        else if (memo.VersionNumber > 1 && memo.PreviousHash is null) errors.Add("'p' (previous hash) is required when n > 1.");

        if (memo.PreviousHash is not null && !IsHash(memo.PreviousHash)) errors.Add("'p' must be a 64-char lowercase hex SHA-256.");
        return errors;
    }

    public static bool IsHash(string? s) =>
        s is { Length: 64 } && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new FormatException($"'{name}' must be a JSON string.");
    }
}
