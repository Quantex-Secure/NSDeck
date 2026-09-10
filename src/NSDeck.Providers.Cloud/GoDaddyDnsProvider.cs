using System.Text.Json;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;

namespace NSDeck.Providers.Cloud;

public sealed class GoDaddyDnsProvider : JsonDnsProviderBase, IDnsProvider
{
    private const string ApiBase = "https://api.godaddy.com";
    private readonly GoDaddyDnsOptions _options;

    public GoDaddyDnsProvider(GoDaddyDnsOptions options, HttpClient? httpClient = null) : base(httpClient)
    {
        if (string.IsNullOrWhiteSpace(options.AccessToken)) throw new ArgumentException("A GoDaddy Personal Access Token is required.", nameof(options));
        _options = options;
    }

    public ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records)
    {
        var basic = ZoneValidator.Validate(records);
        if (!basic.IsValid) return basic;
        try { if (records.Any(r => !r.IsReadOnly && r.TtlSeconds < 600)) throw new InvalidOperationException("GoDaddy requires TTLs of at least 600 seconds."); foreach (var r in records.Where(r => !r.IsReadOnly)) ToApiRecord(r); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        { return new ZoneValidationResult([new ValidationIssue(ex.Message)]); }
        return basic;
    }

    public string ProviderName => "GoDaddy";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        var domains = new List<DomainSummary>();
        string? marker = null;
        do
        {
            var url = $"{ApiBase}/v1/domains?limit=1000" + (marker is null ? string.Empty : $"&marker={Uri.EscapeDataString(marker)}");
            using var document = await SendJsonAsync(HttpMethod.Get, url, null, _options.AccessToken, cancellationToken);
            var page = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement : default;
            if (page.ValueKind != JsonValueKind.Array) break;
            foreach (var item in page.EnumerateArray())
            {
                var name = item.TryGetProperty("domain", out var domain) ? domain.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var status = item.TryGetProperty("status", out var state) ? state.GetString() : null;
                var locked = item.TryGetProperty("locked", out var lockValue) && lockValue.ValueKind == JsonValueKind.True;
                domains.Add(new DomainSummary(name, ProviderName, string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase), locked));
            }
            marker = page.GetArrayLength() == 1000 ? domains[^1].Name : null;
        } while (marker is not null);

        return domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonAsync(HttpMethod.Get,
            $"{ApiBase}/v1/domains/{Uri.EscapeDataString(domain)}/records", null, _options.AccessToken, cancellationToken);
        var records = new List<DnsRecord>();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString()?.ToUpperInvariant() ?? "A";
                var name = item.GetProperty("name").GetString() ?? "@";
                var readOnly = type == "SOA" || (type == "NS" && name == "@") || !DnsRecordTypes.All.Contains(type);
                var data = item.TryGetProperty("data", out var dataValue) ? dataValue.GetString() ?? string.Empty : string.Empty;
                var priority = item.TryGetProperty("priority", out var priorityValue) && priorityValue.TryGetInt32(out var parsedPriority)
                    ? parsedPriority : (int?)null;
                if (type == "SRV")
                {
                    var weight = ReadInt(item, "weight");
                    var port = ReadInt(item, "port");
                    data = $"{priority ?? 0} {weight} {port} {data}";
                    priority = null;
                }
                records.Add(new DnsRecord
                {
                    Name = name,
                    IsReadOnly = readOnly, ReadOnlyReason = readOnly ? "Manage this record in GoDaddy." : null,
                    ProviderMetadata = readOnly ? item.GetRawText() : null,
                    Type = type,
                    Value = data,
                    TtlSeconds = ReadInt(item, "ttl", 600),
                    Priority = type == "MX" ? priority : null
                });
            }
        }
        return new DnsZone(domain, ProviderName, records, DateTimeOffset.Now);
    }

    public async Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default)
    {
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        if (records.Any(record => !record.IsReadOnly && record.TtlSeconds < 600))
            throw new InvalidOperationException("GoDaddy requires DNS TTL values of at least 600 seconds.");

        using var _ = await SendJsonAsync(HttpMethod.Put,
            $"{ApiBase}/v1/domains/{Uri.EscapeDataString(domain)}/records", records.Where(r => r.Type != "SOA" && !(r.Type == "NS" && r.Name == "@")).Select(ToApiRecord).ToArray(), _options.AccessToken, cancellationToken);
    }

    private static object ToApiRecord(DnsRecord record)
    {
        if (record.IsReadOnly && record.ProviderMetadata is not null)
        { using var document = JsonDocument.Parse(record.ProviderMetadata); return document.RootElement.Clone(); }
        if (record.Type.Equals("SRV", StringComparison.OrdinalIgnoreCase))
        {
            var parts = record.Value.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !int.TryParse(parts[0], out var priority) || !int.TryParse(parts[1], out var weight) || !int.TryParse(parts[2], out var port))
                throw new InvalidOperationException($"{record.Name} SRV must use 'priority weight port target' format.");
            return new { type = "SRV", name = record.Name, data = parts[3], ttl = record.TtlSeconds, priority, weight, port };
        }
        return new
        {
            type = record.Type.ToUpperInvariant(), name = record.Name, data = record.Value, ttl = record.TtlSeconds,
            priority = record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) ? record.Priority ?? 0 : 0
        };
    }

    private static int ReadInt(JsonElement element, string property, int fallback = 0) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;
}
