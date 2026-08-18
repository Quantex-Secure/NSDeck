using NSDeck.Core.Providers;

namespace NSDeck.Core.Models;

public sealed record DnsProviderScope(IDnsProvider Provider, IReadOnlyList<DomainSummary> Domains);

public sealed record DnsInventoryZone(
    IDnsProvider Provider,
    string ProviderName,
    string Domain,
    IReadOnlyList<DnsRecord> Records,
    DateTimeOffset RetrievedAt);

public sealed record DnsInventoryRecord(string ProviderName, string Domain, DnsRecord Record)
{
    public string Fqdn => Record.Name == "@"
        ? Domain
        : Record.Name.EndsWith($".{Domain}", StringComparison.OrdinalIgnoreCase)
            ? Record.Name.TrimEnd('.')
            : $"{Record.Name.TrimEnd('.')}.{Domain}";
}

public sealed record DnsInventoryLoadResult(
    IReadOnlyList<DnsInventoryZone> Zones,
    IReadOnlyList<string> Errors)
{
    public IReadOnlyList<DnsInventoryRecord> Records => Zones
        .SelectMany(zone => zone.Records.Select(record => new DnsInventoryRecord(zone.ProviderName, zone.Domain, record)))
        .ToArray();
}

public sealed record DnsPlannedChange(
    DnsInventoryZone Zone,
    DnsRecord Original,
    DnsRecord Updated);

public sealed record DnsZoneOperationResult(
    string Provider,
    string Domain,
    string Status,
    string? Detail = null);

public sealed record DnsChangeLabResult(
    bool Succeeded,
    bool RollbackAttempted,
    bool RollbackSucceeded,
    IReadOnlyList<DnsZoneOperationResult> Operations,
    IReadOnlyList<DnsPlannedChange> Changes);

public sealed record DnsAuditEntry(
    DateTimeOffset Timestamp,
    string Operation,
    string Provider,
    string Domain,
    string Result,
    int ChangeCount = 0,
    string? Fingerprint = null,
    string? Detail = null);
