using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;

namespace NSDeck.Providers.Cloud;

public sealed class AzureDnsProvider : JsonDnsProviderBase, IDnsProvider
{
    private const string ApiVersion = "2018-05-01";
    private readonly AzureDnsOptions _options;
    private readonly TokenCredential _credential;
    private readonly Dictionary<string, string> _zoneIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recordSetEtags = new(StringComparer.OrdinalIgnoreCase);

    public AzureDnsProvider(AzureDnsOptions options, HttpClient? httpClient = null, TokenCredential? credential = null) : base(httpClient)
    {
        if (string.IsNullOrWhiteSpace(options.SubscriptionId)) throw new ArgumentException("An Azure subscription ID is required.", nameof(options));
        _options = options;
        var hasServicePrincipal = !string.IsNullOrWhiteSpace(options.TenantId) || !string.IsNullOrWhiteSpace(options.ClientId) || !string.IsNullOrWhiteSpace(options.ClientSecret);
        if (hasServicePrincipal && (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret)))
            throw new ArgumentException("Azure tenant ID, client ID, and client secret must all be supplied, or all left blank to use your existing Azure sign-in.", nameof(options));
        _credential = credential ?? (hasServicePrincipal
            ? new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret)
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeInteractiveBrowserCredential = false }));
    }

    public string ProviderName => "Azure DNS";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        _zoneIds.Clear();
        var domains = new List<DomainSummary>();
        string? url = $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(_options.SubscriptionId)}/providers/Microsoft.Network/dnszones?api-version={ApiVersion}";
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var document = await SendAzureAsync(HttpMethod.Get, url, null, cancellationToken);
            if (document.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var zone in values.EnumerateArray())
                {
                    var name = zone.GetProperty("name").GetString()!;
                    var id = zone.GetProperty("id").GetString()!;
                    _zoneIds[name] = id;
                    domains.Add(new DomainSummary(name, ProviderName));
                }
            }
            url = document.RootElement.TryGetProperty("nextLink", out var next) ? next.GetString() : null;
        }
        return domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        var zoneId = await GetZoneIdAsync(domain, cancellationToken);
        return new DnsZone(domain, ProviderName, await ReadRecordsAsync(domain, zoneId, cancellationToken), DateTimeOffset.Now);
    }

    public async Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default)
    {
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        var zoneId = await GetZoneIdAsync(domain, cancellationToken);
        var current = await ReadRecordsAsync(domain, zoneId, cancellationToken);
        var currentGroups = current.GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());
        var desiredGroups = records.GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var oldGroup in currentGroups.Where(pair => !desiredGroups.ContainsKey(pair.Key)))
        {
            var record = oldGroup.Value[0];
            var url = RecordSetUrl(zoneId, record.Name, record.Type);
            using var _ = await SendAzureAsync(HttpMethod.Delete, url, null, cancellationToken, MatchHeader(oldGroup.Key));
        }

        foreach (var desiredGroup in desiredGroups)
        {
            if (currentGroups.TryGetValue(desiredGroup.Key, out var old) && DnsProviderHelpers.GroupsEqual(old, desiredGroup.Value)) continue;
            var record = desiredGroup.Value[0];
            var body = BuildRecordSetBody(desiredGroup.Value);
            var concurrencyHeader = currentGroups.ContainsKey(desiredGroup.Key)
                ? MatchHeader(desiredGroup.Key)
                : new Dictionary<string, string> { ["If-None-Match"] = "*" };
            using var _ = await SendAzureAsync(HttpMethod.Put, RecordSetUrl(zoneId, record.Name, record.Type), body, cancellationToken, concurrencyHeader);
        }
    }

    private async Task<IReadOnlyList<DnsRecord>> ReadRecordsAsync(string domain, string zoneId, CancellationToken cancellationToken)
    {
        var records = new List<DnsRecord>();
        _recordSetEtags.Clear();
        string? url = $"https://management.azure.com{zoneId}/recordsets?api-version={ApiVersion}";
        while (!string.IsNullOrWhiteSpace(url))
        {
            using var document = await SendAzureAsync(HttpMethod.Get, url, null, cancellationToken);
            if (document.RootElement.TryGetProperty("value", out var values))
                foreach (var set in values.EnumerateArray()) ParseRecordSet(set, domain, records);
            url = document.RootElement.TryGetProperty("nextLink", out var next) ? next.GetString() : null;
        }
        return records;
    }

    private void ParseRecordSet(JsonElement set, string domain, List<DnsRecord> output)
    {
        var fullType = set.GetProperty("type").GetString() ?? string.Empty;
        var type = fullType[(fullType.LastIndexOf('/') + 1)..].ToUpperInvariant();
        var name = set.GetProperty("name").GetString() ?? "@";
        if (type == "SOA" || (type == "NS" && name == "@")) return;
        if (set.TryGetProperty("etag", out var etag) && !string.IsNullOrWhiteSpace(etag.GetString()))
            _recordSetEtags[DnsProviderHelpers.GroupKey(name, type)] = etag.GetString()!;
        var properties = set.GetProperty("properties");
        var ttl = properties.TryGetProperty("TTL", out var ttlValue) ? ttlValue.GetInt32() : 3600;
        var id = set.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;

        void Add(string value, int? priority = null) => output.Add(new DnsRecord
        {
            ProviderRecordId = id, Name = name, Type = type, Value = value, TtlSeconds = ttl, Priority = priority
        });

        switch (type)
        {
            case "A": AddArray("ARecords", item => item.GetProperty("ipv4Address").GetString()!); break;
            case "AAAA": AddArray("AAAARecords", item => item.GetProperty("ipv6Address").GetString()!); break;
            case "CNAME": if (properties.TryGetProperty("CNAMERecord", out var cname)) Add(cname.GetProperty("cname").GetString()!); break;
            case "MX": AddArray("MXRecords", item => item.GetProperty("exchange").GetString()!, item => item.GetProperty("preference").GetInt32()); break;
            case "NS": AddArray("NSRecords", item => item.GetProperty("nsdname").GetString()!); break;
            case "PTR": AddArray("PTRRecords", item => item.GetProperty("ptrdname").GetString()!); break;
            case "TXT": AddArray("TXTRecords", item => string.Concat(item.GetProperty("value").EnumerateArray().Select(part => part.GetString()))); break;
            case "SRV": AddArray("SRVRecords", item => $"{item.GetProperty("priority").GetInt32()} {item.GetProperty("weight").GetInt32()} {item.GetProperty("port").GetInt32()} {item.GetProperty("target").GetString()}"); break;
            case "CAA": AddArray("CAARecords", item => $"{item.GetProperty("flags").GetInt32()} {item.GetProperty("tag").GetString()} {item.GetProperty("value").GetString()}"); break;
        }

        void AddArray(string property, Func<JsonElement, string> value, Func<JsonElement, int?>? priority = null)
        {
            if (!properties.TryGetProperty(property, out var array)) return;
            foreach (var item in array.EnumerateArray()) Add(value(item), priority?.Invoke(item));
        }
    }

    private static object BuildRecordSetBody(IReadOnlyList<DnsRecord> records)
    {
        if (records.Select(record => record.TtlSeconds).Distinct().Count() != 1)
            throw new InvalidOperationException($"Azure DNS requires every value in {records[0].Name} {records[0].Type} to use the same TTL.");
        var ttl = records[0].TtlSeconds;
        object values = records[0].Type.ToUpperInvariant() switch
        {
            "A" => new { TTL = ttl, ARecords = records.Select(record => new { ipv4Address = record.Value }).ToArray() },
            "AAAA" => new { TTL = ttl, AAAARecords = records.Select(record => new { ipv6Address = record.Value }).ToArray() },
            "CNAME" when records.Count == 1 => new { TTL = ttl, CNAMERecord = new { cname = records[0].Value } },
            "MX" => new { TTL = ttl, MXRecords = records.Select(record => new { preference = record.Priority ?? 0, exchange = record.Value }).ToArray() },
            "NS" => new { TTL = ttl, NSRecords = records.Select(record => new { nsdname = record.Value }).ToArray() },
            "PTR" => new { TTL = ttl, PTRRecords = records.Select(record => new { ptrdname = record.Value }).ToArray() },
            "TXT" => new { TTL = ttl, TXTRecords = records.Select(record => new { value = new[] { record.Value } }).ToArray() },
            "SRV" => BuildSrv(ttl, records),
            "CAA" => BuildCaa(ttl, records),
            _ => throw new InvalidOperationException($"Azure DNS record type {records[0].Type} is not supported by this editor.")
        };
        return new { properties = values };
    }

    private static object BuildSrv(int ttl, IReadOnlyList<DnsRecord> records) => new { TTL = ttl, SRVRecords = records.Select(record =>
    {
        var parts = record.Value.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !int.TryParse(parts[0], out var priority) || !int.TryParse(parts[1], out var weight) || !int.TryParse(parts[2], out var port))
            throw new InvalidOperationException($"{record.Name} SRV must use 'priority weight port target' format.");
        return new { priority, weight, port, target = parts[3] };
    }).ToArray() };

    private static object BuildCaa(int ttl, IReadOnlyList<DnsRecord> records) => new { TTL = ttl, CAARecords = records.Select(record =>
    {
        var parts = record.Value.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var flags)) throw new InvalidOperationException($"{record.Name} CAA must use 'flags tag value' format.");
        return new { flags, tag = parts[1], value = parts[2].Trim('"') };
    }).ToArray() };

    private string RecordSetUrl(string zoneId, string name, string type) =>
        $"https://management.azure.com{zoneId}/{Uri.EscapeDataString(type.ToUpperInvariant())}/{Uri.EscapeDataString(name)}?api-version={ApiVersion}";

    private IReadOnlyDictionary<string, string>? MatchHeader(string groupKey) =>
        _recordSetEtags.TryGetValue(groupKey, out var etag)
            ? new Dictionary<string, string> { ["If-Match"] = etag }
            : null;

    private async Task<string> GetZoneIdAsync(string domain, CancellationToken cancellationToken)
    {
        if (_zoneIds.TryGetValue(domain, out var id)) return id;
        await GetDomainsAsync(cancellationToken);
        return _zoneIds.TryGetValue(domain, out id) ? id : throw new InvalidOperationException($"Azure DNS zone {domain} was not found.");
    }

    private async Task<JsonDocument> SendAzureAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), cancellationToken);
        return await SendJsonAsync(method, url, body, token.Token, cancellationToken, headers);
    }
}
