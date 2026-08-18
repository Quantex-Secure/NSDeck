namespace NSDeck.Core.Models;

public sealed record DnsZone(
    string Domain,
    string Provider,
    IReadOnlyList<DnsRecord> Records,
    DateTimeOffset RetrievedAt,
    bool IsUsingProviderDns = true);
