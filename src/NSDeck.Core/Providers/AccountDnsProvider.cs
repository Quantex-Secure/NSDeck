using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Core.Providers;

public sealed class AccountDnsProvider(IDnsProvider source, string profileId, string name, bool readOnly) : IDnsProvider, IDisposable
{
    public string ProviderName => $"{name} / {source.ProviderName}";
    public string AccountId => profileId + "/" + source.AccountId;
    public bool IsReadOnly => readOnly || source.IsReadOnly;
    public bool SupportsPublicDnsPropagation => source.SupportsPublicDnsPropagation;
    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken ct = default) =>
        (await source.GetDomainsAsync(ct)).Select(z => z with { Provider = ProviderName }).ToArray();
    public Task<DnsZone> GetZoneAsync(string domain, CancellationToken ct = default) => source.GetZoneAsync(domain, ct);
    public Task<DnsZone> GetZoneAsync(DomainSummary zone, CancellationToken ct = default) => source.GetZoneAsync(zone, ct);
    public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
    { Check(); return source.ReplaceZoneAsync(domain, records, ct); }
    public Task ReplaceZoneAsync(DomainSummary zone, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
    { Check(); return source.ReplaceZoneAsync(zone, records, ct); }
    public Task ReplaceZoneGuardedAsync(DomainSummary zone, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
    { Check(); return source.ReplaceZoneGuardedAsync(zone, expected, records, ct); }
    public ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records) => source.ValidateRecords(records);
    private void Check() { if (IsReadOnly) throw new InvalidOperationException("This account is read-only. Enable editing in account settings first."); }
    public void Dispose() { (source as IDisposable)?.Dispose(); }
}
