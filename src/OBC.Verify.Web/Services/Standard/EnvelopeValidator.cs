using System.Reflection;
using System.Text;
using System.Text.Json;
using NJsonSchema;
using OBC.Verify.Web.Models;

namespace OBC.Verify.Web.Services.Standard;

public sealed class EnvelopeResult
{
    public bool EnvelopeValid => EnvelopeErrors.Count == 0;
    public List<string> EnvelopeErrors { get; } = new();
    public string? Type { get; set; }
    public string? SchemaId { get; set; }
    public bool IsVoided { get; set; }
    public string? Reason { get; set; }
    public bool TypeRecognized { get; set; }
    /// <summary>False when data was not validated (invalid envelope, voided or unknown type).</summary>
    public bool DataValidated { get; set; }
    public List<string> DataErrors { get; } = new();
    public List<RecordLink> Links { get; } = new();
}

/// <summary>Section 6, steps 8–10: validate the v3 envelope, then data against the schema it names.</summary>
public static class EnvelopeValidator
{
    private const string ResourcePrefix = "standards/v3/";
    private static readonly Lazy<Schemas> _schemas = new(LoadSchemas);

    public static IReadOnlyCollection<string> KnownSchemaIds => _schemas.Value.Types.Keys;

    public static EnvelopeResult Validate(byte[] content)
    {
        var result = new EnvelopeResult();

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            result.EnvelopeErrors.Add("The document is not valid UTF-8.");
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            result.EnvelopeErrors.Add($"The document is not valid JSON: {ex.Message}");
            return result;
        }

        using (doc)
        {
            var root = doc.RootElement;
            foreach (var error in _schemas.Value.Envelope.Validate(json))
            {
                result.EnvelopeErrors.Add(FormatError(error));
            }

            if (root.ValueKind != JsonValueKind.Object) return result;

            // Checks the schema library may not enforce on its own.
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != 3)
            {
                AddOnce(result.EnvelopeErrors, "version: must be the integer 3.");
            }

            result.Type = GetString(root, "type");
            result.SchemaId = GetString(root, "schema");
            if (result.Type is not null && result.SchemaId is not null && !result.SchemaId.StartsWith($"obc.{result.Type}/", StringComparison.Ordinal))
            {
                result.EnvelopeErrors.Add($"schema: '{result.SchemaId}' does not match type '{result.Type}'.");
            }

            var status = GetString(root, "status");
            if (status is not null && status != "active" && status != "voided")
            {
                AddOnce(result.EnvelopeErrors, $"status: '{status}' is not 'active' or 'voided'.");
            }
            result.IsVoided = status == "voided";
            result.Reason = GetString(root, "reason");

            if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
            {
                foreach (var link in links.EnumerateArray())
                {
                    if (link.ValueKind != JsonValueKind.Object) continue;
                    var rel = GetString(link, "rel");
                    var h = GetString(link, "h");
                    if (rel is null || !MemoParser.IsHash(h)) continue;
                    result.Links.Add(new RecordLink { Rel = rel, Type = GetString(link, "type"), Hash = h! });
                }
            }

            if (!result.EnvelopeValid || result.SchemaId is null) return result;

            result.TypeRecognized = _schemas.Value.Types.TryGetValue(result.SchemaId, out var dataSchema);
            if (result.IsVoided || dataSchema is null) return result;

            result.DataValidated = true;
            foreach (var error in dataSchema.Validate(root.GetProperty("data").GetRawText()))
            {
                result.DataErrors.Add(FormatError(error));
            }
        }

        return result;
    }

    /// <summary>Reads only status and reason, without validating. Used for the latest version of a chain.</summary>
    public static (bool IsVoided, string? Reason)? ReadStatus(byte[] content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return (GetString(doc.RootElement, "status") == "voided", GetString(doc.RootElement, "reason"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddOnce(List<string> errors, string error)
    {
        var field = error[..error.IndexOf(':')];
        if (!errors.Any(e => e.StartsWith(field, StringComparison.OrdinalIgnoreCase))) errors.Add(error);
    }

    private static string FormatError(NJsonSchema.Validation.ValidationError error)
    {
        var path = string.IsNullOrEmpty(error.Path) ? "#" : error.Path;
        return $"{path}: {error.Kind}";
    }

    private sealed record Schemas(JsonSchema Envelope, Dictionary<string, JsonSchema> Types);

    private static Schemas LoadSchemas()
    {
        var envelope = LoadSchema("envelope-schema.json");

        var types = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        using var registry = JsonDocument.Parse(ReadResource("types.json"));
        foreach (var entry in registry.RootElement.GetProperty("schemas").EnumerateArray())
        {
            if (entry.GetProperty("schemaUrl").ValueKind != JsonValueKind.String) continue; // reserved
            var id = entry.GetProperty("id").GetString()!;
            types[id] = LoadSchema(entry.GetProperty("schemaUrl").GetString()!);
        }

        return new Schemas(envelope, types);
    }

    /// <summary>
    /// The published schemas are draft 2020-12. NJsonSchema only resolves draft-07 style
    /// "definitions" references, so "$defs" is renamed when loading.
    /// </summary>
    private static JsonSchema LoadSchema(string relativePath)
    {
        var json = ReadResource(relativePath)
            .Replace("\"$defs\"", "\"definitions\"", StringComparison.Ordinal)
            .Replace("\"#/$defs/", "\"#/definitions/", StringComparison.Ordinal);
        return JsonSchema.FromJsonAsync(json).GetAwaiter().GetResult();
    }

    private static string ReadResource(string relativePath)
    {
        var assembly = typeof(EnvelopeValidator).Assembly;
        var wanted = ResourcePrefix + relativePath;
        // RecursiveDir uses the OS path separator at build time.
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == wanted)
                   ?? throw new InvalidOperationException($"Embedded standard file '{wanted}' not found.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
