using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OBC.Verify.Web.Services.Standard;

/// <summary>One record listed in a period close.</summary>
public sealed class PeriodMember
{
    public string DocumentId { get; init; } = string.Empty;
    public long Number { get; init; }
    public string Hash { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public bool IsVoided { get; init; }
}

/// <summary>The data of an <c>obc.acc-period-close/1</c> document: a close or a reopen of one accounting period.</summary>
public sealed class PeriodClose
{
    public const string Type = "acc-period-close";
    public const string SchemaId = "obc.acc-period-close/1";
    public const string ClosedState = "closed";
    public const string ReopenedState = "reopened";
    public const string PreviousPeriodRel = "previous-period";

    /// <summary>The <c>sourceKey</c> prefix of a period's document id (<c>acc-period/{from}/{to}</c>).</summary>
    public const string SourceKeyPrefix = "acc-period/";

    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string State { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public string? ClosedAt { get; init; }
    public long? Count { get; init; }
    public string? Root { get; init; }
    /// <summary>Null when the record lists no members (a reopen).</summary>
    public List<PeriodMember>? Members { get; init; }
    /// <summary>The Merkle root recomputed from <see cref="Members"/>, null without members.</summary>
    public string? ComputedRoot { get; init; }
    /// <summary><c>h</c> of the previous period's close, from the envelope's <c>previous-period</c> link.</summary>
    public string? PreviousPeriodHash { get; set; }

    public bool IsClosed => State == ClosedState;
    public bool CountMatches => Count is { } c && Members is not null && c == Members.Count;
    public bool RootMatches => Root is not null && Root == ComputedRoot;
    public string Range => $"{FormatDate(From)} – {FormatDate(To)}";

    /// <summary>Reads the data member leniently. Use <see cref="Check"/> for the rules the schema can't express.</summary>
    public static PeriodClose Read(JsonElement data)
    {
        List<PeriodMember>? members = null;
        if (data.TryGetProperty("members", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            members = new List<PeriodMember>();
            foreach (var m in array.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) continue;
                members.Add(new PeriodMember
                {
                    DocumentId = GetString(m, "d") ?? string.Empty,
                    Number = m.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var nv) ? nv : 0,
                    Hash = GetString(m, "h") ?? string.Empty,
                    Type = GetString(m, "type") ?? string.Empty,
                    Date = GetString(m, "date") ?? string.Empty,
                    IsVoided = GetString(m, "status") == "voided"
                });
            }
        }

        return new PeriodClose
        {
            From = ParseDate(GetString(data, "from")),
            To = ParseDate(GetString(data, "to")),
            State = GetString(data, "state") ?? string.Empty,
            Reason = GetString(data, "reason"),
            ClosedAt = GetString(data, "closedAt"),
            Count = data.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var cv) ? cv : null,
            Root = GetString(data, "root"),
            Members = members,
            ComputedRoot = members is null ? null : ComputeRoot(members)
        };
    }

    /// <summary>
    /// The rules of section 3 that the JSON schema doesn't express: the range, which fields a close or a reopen has,
    /// member order and range, and the recomputed count and root. Returns one message per broken rule.
    /// </summary>
    public List<string> Check()
    {
        var errors = new List<string>();
        if (From is null) errors.Add("from: not a valid date (yyyy-MM-dd).");
        if (To is null) errors.Add("to: not a valid date (yyyy-MM-dd).");
        if (From is { } f && To is { } t && t < f) errors.Add($"to: the period ends ({FormatDate(To)}) before it starts ({FormatDate(From)}).");

        if (State == ReopenedState)
        {
            if (Members is not null || Count is not null || Root is not null)
            {
                errors.Add("A reopened period has no members, count or root.");
            }
            return errors;
        }
        if (State != ClosedState) return errors; // the schema reports it

        if (Members is null || Count is null || Root is null)
        {
            errors.Add("A closed period must have members, count and root.");
            return errors;
        }

        if (Count != Members.Count)
        {
            errors.Add($"count: {Count} does not match the {Members.Count} listed members.");
        }
        if (!RootMatches)
        {
            errors.Add($"root: {Root} does not match the Merkle root of the listed members ({ComputedRoot}).");
        }

        for (var i = 1; i < Members.Count; i++)
        {
            var order = CompareUtf8(Members[i - 1].DocumentId, Members[i].DocumentId);
            if (order == 0)
            {
                errors.Add($"members: document {Members[i].DocumentId} is listed more than once.");
            }
            else if (order > 0)
            {
                errors.Add($"members: not sorted by d (UTF-8 byte order) at entry {i + 1} ({Members[i].DocumentId}).");
            }
        }

        if (From is { } from && To is { } to)
        {
            foreach (var m in Members)
            {
                if (ParseDate(m.Date) is { } date && (date < from || date > to))
                {
                    errors.Add($"members: {m.DocumentId} is dated {m.Date}, outside the period {Range}.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// RFC 6962 Merkle tree hash over the members in their listed order, as lowercase hex.
    /// leaf = SHA-256(0x00 ‖ utf8(d) ‖ 0x0A ‖ utf8(decimal n) ‖ 0x0A ‖ utf8(h)), node = SHA-256(0x01 ‖ left ‖ right),
    /// split at the largest power of two smaller than the count. No members hash to SHA-256 of the empty string.
    /// </summary>
    public static string ComputeRoot(IReadOnlyList<PeriodMember> members)
    {
        if (members.Count == 0) return Convert.ToHexStringLower(SHA256.HashData([]));

        var leaves = members.Select(LeafHash).ToArray();
        return Convert.ToHexStringLower(TreeHash(leaves, 0, leaves.Length));
    }

    public static byte[] LeafHash(PeriodMember member)
    {
        var text = Encoding.UTF8.GetBytes($"{member.DocumentId}\n{member.Number.ToString(CultureInfo.InvariantCulture)}\n{member.Hash}");
        var input = new byte[text.Length + 1];
        input[0] = 0x00;
        text.CopyTo(input, 1);
        return SHA256.HashData(input);
    }

    private static byte[] TreeHash(byte[][] leaves, int start, int count)
    {
        if (count == 1) return leaves[start];

        var split = 1;
        while (split * 2 < count) split *= 2;

        var left = TreeHash(leaves, start, split);
        var right = TreeHash(leaves, start + split, count - split);

        var input = new byte[1 + left.Length + right.Length];
        input[0] = 0x01;
        left.CopyTo(input, 1);
        right.CopyTo(input, 1 + left.Length);
        return SHA256.HashData(input);
    }

    /// <summary>Ordinal order of the UTF-8 bytes. UTF-16 ordinal order differs from it above U+D7FF.</summary>
    public static int CompareUtf8(string x, string y) =>
        Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y));

    /// <summary>
    /// The range a public-mode document id names (<c>{org}:obc:acc-period/{from}/{to}</c>), or null for an
    /// opaque (private mode) id, which can't be checked without the tenant key.
    /// </summary>
    public static (string From, string To)? RangeFromPublicId(string documentId)
    {
        var marker = ":obc:" + SourceKeyPrefix;
        var at = documentId.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        var parts = documentId[(at + marker.Length)..].Split('/');
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    public static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static string FormatDate(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
