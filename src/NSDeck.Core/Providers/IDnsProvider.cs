using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Core.Providers;

public interface IDnsProvider
{
    string ProviderName { get; }
    bool SupportsPublicDnsPropagation => true;
    bool IsReadOnly => false;
    string AccountId => ProviderName;
    string? ZoneId => null;

    IDnsProvider ForZone(DomainSummary zone) => new ZoneTargetProvider(this, zone);
    Task<DnsZone> GetZoneAsync(DomainSummary zone, CancellationToken cancellationToken = default) => GetZoneAsync(zone.Name, cancellationToken);
    Task ReplaceZoneAsync(DomainSummary zone, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default) => ReplaceZoneAsync(zone.Name, records, cancellationToken);
    ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records) => ZoneValidator.Validate(records);

    async Task ReplaceZoneGuardedAsync(DomainSummary zone, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> desired, CancellationToken cancellationToken = default)
    {
        if (IsReadOnly) throw new InvalidOperationException("This account is read-only.");
        var current = await GetZoneAsync(zone, cancellationToken);
        ZoneWriteGuard.Check(current.Records, expected, desired);
        await ReplaceZoneAsync(zone, desired, cancellationToken);
    }

    Task ReplaceZoneGuardedAsync(string domain, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> desired, CancellationToken cancellationToken = default) =>
        ReplaceZoneGuardedAsync(new DomainSummary(domain, ProviderName), expected, desired, cancellationToken);

    Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default);

    Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default);

    Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default);
}
