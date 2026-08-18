using NSDeck.Core.Models;

namespace NSDeck.Core.Providers;

public interface IDnsProvider
{
    string ProviderName { get; }
    bool SupportsPublicDnsPropagation => true;

    Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default);

    Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default);

    Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default);
}
