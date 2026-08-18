namespace NSDeck.Core.Models;

public sealed record ZoneSnapshot(
    string Domain,
    string Provider,
    DateTimeOffset CreatedAt,
    string Fingerprint,
    IReadOnlyList<DnsRecord> Records);
