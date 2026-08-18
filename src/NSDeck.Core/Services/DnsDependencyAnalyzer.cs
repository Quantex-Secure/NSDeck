using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public sealed record DnsDependency(
    DnsInventoryRecord Source,
    string Relationship,
    DnsInventoryRecord? RelatedRecord,
    string Target);

public static class DnsDependencyAnalyzer
{
    public static IReadOnlyList<DnsDependency> Analyze(
        IReadOnlyCollection<DnsInventoryRecord> inventory,
        IReadOnlyCollection<DnsInventoryRecord> selected)
    {
        var dependencies = new List<DnsDependency>();
        var selectedNames = selected.Select(record => NormalizeName(record.Fqdn)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedValues = selected.Select(record => record.Record.Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in selected)
        {
            foreach (var target in GetTargets(source.Record))
            {
                var related = inventory.FirstOrDefault(candidate => NormalizeName(candidate.Fqdn) == NormalizeName(target));
                dependencies.Add(new DnsDependency(source, "points to", related, target));
            }
        }

        foreach (var candidate in inventory.Except(selected))
        {
            foreach (var target in GetTargets(candidate.Record))
            {
                if (selectedNames.Contains(NormalizeName(target)))
                    dependencies.Add(new DnsDependency(candidate, "depends on selected record", selected.First(record => NormalizeName(record.Fqdn) == NormalizeName(target)), target));
            }

            if (selectedValues.Contains(candidate.Record.Value.Trim()))
                dependencies.Add(new DnsDependency(candidate, "shares the same value", null, candidate.Record.Value));
        }

        return dependencies
            .DistinctBy(item => $"{item.Source.ProviderName}|{item.Source.Domain}|{item.Source.Record.LocalId}|{item.Relationship}|{item.Target}")
            .OrderBy(item => item.Source.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Source.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetTargets(DnsRecord record)
    {
        var type = record.Type.ToUpperInvariant();
        if (type is "CNAME" or "NS" or "PTR" or "MX")
            yield return record.Value.Trim().TrimEnd('.');
        else if (type == "SRV")
        {
            var parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) yield return parts[^1].TrimEnd('.');
        }
        else if (type == "TXT" && record.Value.Contains("v=spf1", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (token.StartsWith("include:", StringComparison.OrdinalIgnoreCase)) yield return token[8..].TrimEnd('.');
                else if (token.StartsWith("redirect=", StringComparison.OrdinalIgnoreCase)) yield return token[9..].TrimEnd('.');
        }
    }

    private static string NormalizeName(string value) => value.Trim().TrimEnd('.');
}
