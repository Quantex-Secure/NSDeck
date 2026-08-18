using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public enum DnsRiskLevel
{
    Information,
    Warning,
    Critical
}

public sealed record DnsRisk(DnsRiskLevel Level, string Message);

public sealed record DnsRiskReport(IReadOnlyList<DnsRisk> Risks)
{
    public bool HasWarnings => Risks.Any(risk => risk.Level >= DnsRiskLevel.Warning);
    public bool HasCriticalRisks => Risks.Any(risk => risk.Level == DnsRiskLevel.Critical);

    public string Summary => string.Join(Environment.NewLine, Risks
        .OrderByDescending(risk => risk.Level)
        .Select(risk => $"• {risk.Message}"));
}

public static class ZoneRiskAnalyzer
{
    public static DnsRiskReport Analyze(
        IReadOnlyCollection<DnsRecord> original,
        IReadOnlyCollection<DnsRecord> desired)
    {
        var changes = ZoneComparer.Diff(original, desired);
        var affectedOriginals = changes
            .Where(change => change.Kind is ZoneChangeKind.Delete or ZoneChangeKind.Update)
            .Select(change => change.Original ?? change.Record)
            .ToArray();
        var risks = new List<DnsRisk>();

        if (original.Any(IsMx) && !desired.Any(IsMx))
            Add(DnsRiskLevel.Critical, "This change removes every MX record and can stop inbound email.");

        var originalApexEndpoints = original.Where(IsApexEndpoint).ToArray();
        if (originalApexEndpoints.Length > 0 && !desired.Any(IsApexEndpoint))
            Add(DnsRiskLevel.Critical, "This change removes every apex A, AAAA, or CNAME endpoint.");
        else if (affectedOriginals.Any(IsApexEndpoint))
            Add(DnsRiskLevel.Warning, "An apex endpoint is being changed.");

        if (affectedOriginals.Any(IsMx) && desired.Any(IsMx))
            Add(DnsRiskLevel.Warning, "Mail routing is being changed.");

        if (affectedOriginals.Any(record => record.Name.Equals("_dmarc", StringComparison.OrdinalIgnoreCase)))
            Add(DnsRiskLevel.Warning, "A DMARC record is being changed or removed.");

        if (affectedOriginals.Any(record => record.Name.Contains("_domainkey", StringComparison.OrdinalIgnoreCase)))
            Add(DnsRiskLevel.Warning, "A DKIM record is being changed or removed.");

        if (affectedOriginals.Any(record => record.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) &&
                                            record.Value.TrimStart().StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)))
            Add(DnsRiskLevel.Warning, "An SPF record is being changed or removed.");

        if (affectedOriginals.Any(record => record.Type.Equals("CAA", StringComparison.OrdinalIgnoreCase)))
            Add(DnsRiskLevel.Warning, "A CAA record is being changed or removed; certificate issuance policy may change.");

        if (affectedOriginals.Any(record => record.Type.Equals("NS", StringComparison.OrdinalIgnoreCase)))
            Add(DnsRiskLevel.Warning, "A delegated NS record is being changed or removed.");

        var deletedCount = changes.Count(change => change.Kind == ZoneChangeKind.Delete);
        if (deletedCount >= 5 || (original.Count >= 4 && deletedCount >= Math.Ceiling(original.Count * 0.25)))
            Add(DnsRiskLevel.Warning, $"This plan deletes {deletedCount} of {original.Count} records.");

        if (changes.Count >= 10)
            Add(DnsRiskLevel.Warning, $"This is a large zone update affecting {changes.Count} records.");

        return new DnsRiskReport(risks);

        void Add(DnsRiskLevel level, string message)
        {
            if (risks.All(risk => !risk.Message.Equals(message, StringComparison.Ordinal)))
                risks.Add(new DnsRisk(level, message));
        }
    }

    private static bool IsMx(DnsRecord record) => record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase);

    private static bool IsApexEndpoint(DnsRecord record) =>
        record.Name == "@" && record.Type.ToUpperInvariant() is "A" or "AAAA" or "CNAME";
}
