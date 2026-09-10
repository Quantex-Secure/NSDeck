using System.Net;
using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public sealed record ValidationIssue(string Message, bool IsError = true);

public sealed record ZoneValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => !issue.IsError);
    public string ErrorSummary => string.Join(Environment.NewLine, Issues.Where(issue => issue.IsError).Select(issue => "• " + issue.Message));
}

public static class ZoneValidator
{
    public static ZoneValidationResult Validate(IReadOnlyCollection<DnsRecord> records)
    {
        var issues = new List<ValidationIssue>();

        if (records.Count == 0)
        {
            issues.Add(new ValidationIssue("The zone is empty. Empty-zone writes are blocked."));
            return new ZoneValidationResult(issues);
        }

        foreach (var record in records.Where(r => !r.IsReadOnly))
        {
            var label = $"{record.Name} {record.Type}";

            if (string.IsNullOrWhiteSpace(record.Name))
            {
                issues.Add(new ValidationIssue("Every record must have a host name. Use @ for the zone apex."));
            }

            if (!DnsRecordTypes.All.Contains(record.Type, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue($"{label} uses an unsupported record type."));
            }

            if (string.IsNullOrWhiteSpace(record.Value))
            {
                issues.Add(new ValidationIssue($"{label} must have a value."));
            }

            if (record.TtlSeconds <= 0)
            {
                issues.Add(new ValidationIssue($"{label} must have a positive TTL."));
            }

            if (record.Type.Equals("A", StringComparison.OrdinalIgnoreCase) &&
                (!IPAddress.TryParse(record.Value, out var address) ||
                 address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            {
                issues.Add(new ValidationIssue($"{label} must contain a valid IPv4 address."));
            }

            if (record.Type.Equals("AAAA", StringComparison.OrdinalIgnoreCase) &&
                (!IPAddress.TryParse(record.Value, out var ipv6) || ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6))
            {
                issues.Add(new ValidationIssue($"{label} must contain a valid IPv6 address."));
            }

            if (record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) && record.Priority is null)
            {
                issues.Add(new ValidationIssue($"{label} requires a priority."));
            }
        }

        foreach (var record in records.Where(r => !r.IsReadOnly))
        {
            if (record.Name.Length > 253 || record.Name.Any(char.IsWhiteSpace) || record.Name.Split('.').Any(label => label.Length > 63))
                issues.Add(new ValidationIssue($"{record.Name}: use a DNS name with labels of at most 63 characters."));
            if (record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) && record.Priority is < 0 or > 65535)
                issues.Add(new ValidationIssue($"{record.Name}: MX priority must be 0–65535."));
            if (record.Type.Equals("SRV", StringComparison.OrdinalIgnoreCase))
            {
                var parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 4 || parts.Take(3).Any(p => !ushort.TryParse(p, out _)))
                    issues.Add(new ValidationIssue($"{record.Name}: SRV requires priority weight port target; numbers must be 0–65535."));
            }
            if (record.Type.Equals("CAA", StringComparison.OrdinalIgnoreCase))
            {
                var parts = record.Value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !byte.TryParse(parts[0], out _) || parts[1].Any(c => !char.IsAsciiLetterOrDigit(c)))
                    issues.Add(new ValidationIssue($"{record.Name}: CAA requires flags (0–255), tag, and value."));
            }
        }

        foreach (var group in records.GroupBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Any(record => !record.IsReadOnly && record.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase)) && group.Count(r => !r.IsReadOnly) > 1)
            {
                issues.Add(new ValidationIssue($"{group.Key} has a CNAME and another record. A CNAME must be the only record at its name."));
            }
        }

        var duplicates = records
            .GroupBy(record => string.Join('|', record.Name.ToLowerInvariant(), record.Type.ToUpperInvariant(), record.Value, record.Priority))
            .Where(group => group.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            var record = duplicate.First();
            issues.Add(new ValidationIssue($"Duplicate record: {record.Name} {record.Type} {record.Value}."));
        }

        return new ZoneValidationResult(issues);
    }
}
