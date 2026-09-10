using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public static class DnsRecordSemantics
{
    public static string CanonicalMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return "";
        try
        {
            using var document = JsonDocument.Parse(metadata);
            var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in document.RootElement.EnumerateObject())
            {
                if (p.Name == "proxied" && p.Value.ValueKind == JsonValueKind.False) continue;
                if (p.Name == "comment" && (p.Value.ValueKind == JsonValueKind.Null || p.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(p.Value.GetString()))) continue;
                if (p.Name == "tags" && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0) continue;
                if (p.Name == "settings" && p.Value.ValueKind == JsonValueKind.Object && p.Value.EnumerateObject().All(v => v.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null)) continue;
                properties[p.Name] = p.Value.Clone();
            }
            return properties.Count == 0 ? "" : JsonSerializer.Serialize(properties);
        }
        catch (JsonException) { return metadata; }
        catch (InvalidOperationException) { return metadata; }
    }

    public static string CanonicalValue(DnsRecord record)
    {
        if (record.IsReadOnly) return record.Type.ToUpperInvariant() is "SOA" or "RRSIG" or "NSEC" or "NSEC3" ? "Provider-maintained value" : record.Value;
        var value = record.Value.Trim();
        return record.Type.ToUpperInvariant() switch
        {
            "A" or "AAAA" when IPAddress.TryParse(value, out var ip) => ip.ToString(),
            "CNAME" or "MX" or "NS" or "PTR" => value.TrimEnd('.').ToLowerInvariant(),
            "SRV" => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.').ToLowerInvariant(),
            "TXT" => DecodeText(record.Value),
            _ => value
        };
    }

    public static string DecodeText(string value)
    {
        if (!Regex.IsMatch(value, "\\A\\s*(?:\"(?:\\\\.|[^\"\\\\])*\"\\s*)+\\z")) return value;
        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(value, "\"((?:\\\\.|[^\"\\\\])*)\""))
            builder.Append(Regex.Replace(match.Groups[1].Value, @"\\([0-9]{3}|.)", m => m.Groups[1].Value.Length == 3 && int.TryParse(m.Groups[1].Value, out var number) ? ((char)number).ToString() : m.Groups[1].Value));
        return builder.ToString();
    }

    public static IReadOnlyList<string> TextChunks(string value)
    {
        var chunks = new List<string>(); var builder = new StringBuilder(); var bytes = 0;
        foreach (var rune in DecodeText(value).EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 255) { chunks.Add(builder.ToString()); builder.Clear(); bytes = 0; }
            builder.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        chunks.Add(builder.ToString()); return chunks;
    }
    public static string EncodeText(string value) => string.Join(' ', TextChunks(value).Select(s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
}
