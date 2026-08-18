using System.Globalization;
using NSDeck.Core.Models;

namespace NSDeck.Providers.Cloud;

internal static class DnsProviderHelpers
{
    public static string ToRelativeName(string absoluteName, string domain)
    {
        var name = absoluteName.Trim().TrimEnd('.');
        var zone = domain.Trim().TrimEnd('.');
        if (name.Equals(zone, StringComparison.OrdinalIgnoreCase)) return "@";
        var suffix = "." + zone;
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    public static string ToAbsoluteName(string relativeName, string domain) =>
        relativeName.Trim() is "" or "@"
            ? domain.Trim().TrimEnd('.')
            : $"{relativeName.Trim().TrimEnd('.')}.{domain.Trim().TrimEnd('.')}";

    public static (int? Priority, string Value) ParsePriorityValue(string type, string value)
    {
        if (!type.Equals("MX", StringComparison.OrdinalIgnoreCase)) return (null, value);
        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority)
            ? (priority, parts[1])
            : (null, value);
    }

    public static string FormatValue(DnsRecord record) =>
        record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) && record.Priority is not null
            ? $"{record.Priority.Value.ToString(CultureInfo.InvariantCulture)} {record.Value}"
            : record.Value;

    public static string GroupKey(string name, string type) =>
        $"{name.Trim().TrimEnd('.').ToLowerInvariant()}|{type.Trim().ToUpperInvariant()}";

    public static string GroupKey(DnsRecord record) => GroupKey(record.Name, record.Type);

    public static bool GroupsEqual(IEnumerable<DnsRecord> left, IEnumerable<DnsRecord> right)
    {
        var a = left.Select(Canonical).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var b = right.Select(Canonical).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    private static string Canonical(DnsRecord record) => string.Join('|',
        record.Name.Trim().TrimEnd('.').ToLowerInvariant(),
        record.Type.Trim().ToUpperInvariant(),
        record.Value.Trim(),
        record.TtlSeconds.ToString(CultureInfo.InvariantCulture),
        record.Priority?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
}
