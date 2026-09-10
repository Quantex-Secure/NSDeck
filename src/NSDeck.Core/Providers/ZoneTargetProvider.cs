using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Core.Providers;

// A selected target retains its provider ID even when another same-name zone exists.
public sealed class ZoneTargetProvider(IDnsProvider source, DomainSummary zone) : IDnsProvider
{
    public string ProviderName => source.ProviderName;
    public string AccountId => source.AccountId;
    public string? ZoneId => zone.ZoneId;
    public bool IsReadOnly => source.IsReadOnly;
    public bool SupportsPublicDnsPropagation => zone.IsPublic && source.SupportsPublicDnsPropagation;
    public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DomainSummary>>([zone]);
    public Task<DnsZone> GetZoneAsync(string domain, CancellationToken ct = default) { Check(domain); return source.GetZoneAsync(zone, ct); }
    public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
    { Check(domain); if (IsReadOnly) throw new InvalidOperationException("This account is read-only."); return source.ReplaceZoneAsync(zone, records, ct); }
    public Task ReplaceZoneGuardedAsync(string domain, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> desired, CancellationToken ct = default)
    { Check(domain); return source.ReplaceZoneGuardedAsync(zone, expected, desired, ct); }
    public ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records) => source.ValidateRecords(records);
    private void Check(string domain) { if (!domain.Equals(zone.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected zone does not match the write target."); }
}
