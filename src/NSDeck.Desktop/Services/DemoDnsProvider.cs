using NSDeck.Core.Models;
using NSDeck.Core.Providers;

namespace NSDeck.Desktop.Services;

public sealed class DemoDnsProvider : IDnsProvider
{
    private readonly Dictionary<string, List<DnsRecord>> _zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["example.com"] =
        [
            Record("@", "A", "203.0.113.10", 300),
            Record("www", "CNAME", "example.com.", 300),
            Record("mail", "MX", "mail.example.com.", 1800, 10),
            Record("@", "TXT", "v=spf1 include:_spf.example.com ~all", 1800),
            Record("_dmarc", "TXT", "v=DMARC1; p=quarantine", 1800),
            Record("ftp", "A", "203.0.113.20", 300),
            Record("api", "CNAME", "www.example.com.", 300),
            Record("_acme-challenge", "TXT", "a1b2c3d4e5f6g7h8i9j0", 300),
            Record("blog", "CNAME", "www.example.com.", 300),
            Record("dev", "A", "203.0.113.30", 300),
            Record("selector1._domainkey", "TXT", "v=DKIM1; k=rsa; p=MIIBIjANBgkqhki...", 1800)
        ],
        ["example.net"] =
        [
            Record("@", "A", "198.51.100.42", 1800),
            Record("www", "CNAME", "example.net.", 1800)
        ],
        ["example.org"] =
        [
            Record("@", "A", "192.0.2.18", 1800),
            Record("www", "CNAME", "example.org.", 1800)
        ]
    };

    public string ProviderName => "Namecheap Demo";
    public bool SupportsPublicDnsPropagation => false;

    public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DomainSummary>>(_zones.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new DomainSummary(name, ProviderName, IsUsingProviderDns: true))
            .ToArray());

    public Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        var records = _zones.TryGetValue(domain, out var zone)
            ? zone.Select(record => record.Clone()).ToArray()
            : [];
        return Task.FromResult(new DnsZone(domain, ProviderName, records, DateTimeOffset.Now));
    }

    public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default)
    {
        _zones[domain] = records.Select(record => record.Clone()).ToList();
        return Task.CompletedTask;
    }

    private static DnsRecord Record(string name, string type, string value, int ttl, int? priority = null) => new()
    {
        Name = name,
        Type = type,
        Value = value,
        TtlSeconds = ttl,
        Priority = priority
    };
}
