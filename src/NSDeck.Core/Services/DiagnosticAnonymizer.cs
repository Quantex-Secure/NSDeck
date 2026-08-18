using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public sealed class DiagnosticAnonymizer
{
    private const string ProviderSeparator = " — ";
    private readonly Dictionary<string, string> _domainAliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serverAliases = new(StringComparer.OrdinalIgnoreCase);

    public DnsAuditEntry Anonymize(DnsAuditEntry entry) => entry with
    {
        Provider = AnonymizeProvider(entry.Provider),
        Domain = GetDomainAlias(entry.Domain),
        Fingerprint = null,
        Detail = string.IsNullOrWhiteSpace(entry.Detail)
            ? null
            : "Provider detail was omitted from this shareable report."
    };

    public string AnonymizeProvider(string provider)
    {
        var separator = provider.IndexOf(ProviderSeparator, StringComparison.Ordinal);
        if (separator < 0) return provider;

        var providerType = provider[..separator];
        var server = provider[(separator + ProviderSeparator.Length)..];
        if (!_serverAliases.TryGetValue(server, out var alias))
        {
            alias = $"server-{_serverAliases.Count + 1:000}.example.invalid";
            _serverAliases[server] = alias;
        }
        return providerType + ProviderSeparator + alias;
    }

    private string GetDomainAlias(string domain)
    {
        if (!_domainAliases.TryGetValue(domain, out var alias))
        {
            alias = $"zone-{_domainAliases.Count + 1:000}.example.invalid";
            _domainAliases[domain] = alias;
        }
        return alias;
    }
}
