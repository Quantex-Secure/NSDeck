using System.Globalization;
using System.Xml.Linq;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;

namespace NSDeck.Providers.Namecheap;

public sealed class NamecheapDnsProvider : IDnsProvider, IDisposable
{
    private static readonly int[] EditorTtlValues = [60, 300, 1200, 1800, 3600];

    private readonly NamecheapOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public NamecheapDnsProvider(NamecheapOptions options, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsComplete)
        {
            throw new ArgumentException("Namecheap API user, username, API key, and whitelisted client IPv4 address are required.", nameof(options));
        }

        _options = options;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public string ProviderName => _options.UseSandbox ? "Namecheap Sandbox" : "Namecheap";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        const int pageSize = 100;
        var page = 1;
        var processedItems = 0;
        var domains = new List<DomainSummary>();

        while (true)
        {
            var parameters = CreateParameters("namecheap.domains.getList");
            parameters["Page"] = page.ToString(CultureInfo.InvariantCulture);
            parameters["PageSize"] = pageSize.ToString(CultureInfo.InvariantCulture);
            parameters["SortBy"] = "NAME";

            var document = await SendAsync(parameters, cancellationToken);
            var domainElements = Descendants(document, "Domain");

            foreach (var element in domainElements)
            {
                var name = Attribute(element, "Name");
                if (string.IsNullOrWhiteSpace(name) || !ParseBoolean(Attribute(element, "IsOurDNS")))
                {
                    continue;
                }

                domains.Add(new DomainSummary(
                    name,
                    ProviderName,
                    ParseBoolean(Attribute(element, "IsExpired")),
                    ParseBoolean(Attribute(element, "IsLocked")),
                    IsUsingProviderDns: true));
            }

            processedItems += domainElements.Count;
            var totalItems = ParseInt(Descendants(document, "TotalItems").FirstOrDefault()?.Value);
            if (processedItems >= totalItems || domainElements.Count < pageSize || totalItems == 0)
            {
                break;
            }

            page++;
        }

        return domains
            .OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        var (sld, tld) = SplitDomain(domain);
        var parameters = CreateParameters("namecheap.domains.dns.getHosts");
        parameters["SLD"] = sld;
        parameters["TLD"] = tld;

        var document = await SendAsync(parameters, cancellationToken);
        var result = Descendants(document, "DomainDNSGetHostsResult").FirstOrDefault()
            ?? throw new NamecheapApiException($"Namecheap did not return a DNS zone for {domain}.");

        var isUsingOurDns = ParseBoolean(Attribute(result, "IsUsingOurDNS"));
        var records = Descendants(result, "Host")
            .Select(ParseRecord)
            .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new DnsZone(domain, ProviderName, records, DateTimeOffset.Now, isUsingOurDns);
    }

    public async Task ReplaceZoneAsync(
        string domain,
        IReadOnlyList<DnsRecord> records,
        CancellationToken cancellationToken = default)
    {
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorSummary);
        }
        if (records.Count > 800)
        {
            throw new InvalidOperationException("Namecheap BasicDNS supports at most 800 host records.");
        }

        var (sld, tld) = SplitDomain(domain);
        var parameters = CreateParameters("namecheap.domains.dns.setHosts");
        parameters["SLD"] = sld;
        parameters["TLD"] = tld;

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var apiIndex = index + 1;
            parameters[$"HostName{apiIndex}"] = record.Name;
            parameters[$"RecordType{apiIndex}"] = record.Type.ToUpperInvariant();
            parameters[$"Address{apiIndex}"] = record.Value;
            parameters[$"TTL{apiIndex}"] = record.TtlSeconds.ToString(CultureInfo.InvariantCulture);
            if (record.Priority is not null)
            {
                parameters[$"MXPref{apiIndex}"] = record.Priority.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        var document = await SendAsync(parameters, cancellationToken);
        var result = Descendants(document, "DomainDNSSetHostsResult").FirstOrDefault();
        if (result is null || !ParseBoolean(Attribute(result, "IsSuccess")))
        {
            throw new NamecheapApiException($"Namecheap did not confirm the DNS update for {domain}.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<XDocument> SendAsync(
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _httpClient.PostAsync(_options.Endpoint, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new NamecheapApiException($"Namecheap returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(payload);
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            throw new NamecheapApiException("Namecheap returned an unreadable response.", exception.Message);
        }

        var rootStatus = Attribute(document.Root, "Status");
        if (string.Equals(rootStatus, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            var error = Descendants(document, "Error").FirstOrDefault();
            throw new NamecheapApiException(
                error?.Value.Trim() ?? "Namecheap reported an unknown API error.",
                Attribute(error, "Number"));
        }

        return document;
    }

    private Dictionary<string, string> CreateParameters(string command) => new(StringComparer.Ordinal)
    {
        ["ApiUser"] = _options.ApiUser,
        ["ApiKey"] = _options.ApiKey,
        ["UserName"] = _options.UserName,
        ["ClientIp"] = _options.ClientIp,
        ["Command"] = command
    };

    private static DnsRecord ParseRecord(XElement element)
    {
        var ttl = ParseInt(Attribute(element, "TTL"));
        var type = Attribute(element, "Type")?.ToUpperInvariant() ?? "A";
        var priorityText = Attribute(element, "MXPref");
        int? priority = type == "MX" && int.TryParse(priorityText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority)
            ? parsedPriority
            : null;

        return new DnsRecord
        {
            ProviderRecordId = Attribute(element, "HostId"),
            Name = Attribute(element, "Name") ?? "@",
            Type = type,
            Value = Attribute(element, "Address") ?? string.Empty,
            TtlSeconds = NormalizeTtl(ttl),
            Priority = priority
        };
    }

    private static int NormalizeTtl(int ttl)
    {
        if (ttl <= 0)
        {
            return 1800;
        }

        foreach (var editorTtl in EditorTtlValues)
        {
            if (Math.Abs(editorTtl - ttl) <= 1)
            {
                return editorTtl;
            }
        }

        return ttl;
    }

    private static (string Sld, string Tld) SplitDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var normalized = domain.Trim().TrimEnd('.');
        var separator = normalized.IndexOf('.');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new ArgumentException($"{domain} is not a registrable domain name.", nameof(domain));
        }

        return (normalized[..separator], normalized[(separator + 1)..]);
    }

    private static List<XElement> Descendants(XContainer? container, string localName) =>
        container?.Descendants().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool ParseBoolean(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static int ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
