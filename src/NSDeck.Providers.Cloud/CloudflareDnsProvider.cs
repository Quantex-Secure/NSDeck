using System.Text.Json;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;

namespace NSDeck.Providers.Cloud;

public sealed class CloudflareDnsProvider : JsonDnsProviderBase, IDnsProvider
{
    private const string ApiBase = "https://api.cloudflare.com/client/v4";
    private readonly CloudflareDnsOptions _options;
    private readonly Dictionary<string, string> _zoneIds = new(StringComparer.OrdinalIgnoreCase);

    public CloudflareDnsProvider(CloudflareDnsOptions options, HttpClient? httpClient = null) : base(httpClient)
    {
        if (string.IsNullOrWhiteSpace(options.ApiToken)) throw new ArgumentException("A Cloudflare API token is required.", nameof(options));
        _options = options;
    }

    public string ProviderName => "Cloudflare";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        _zoneIds.Clear();
        var domains = new List<DomainSummary>();
        var page = 1;
        var totalPages = 1;
        do
        {
            using var document = await SendCloudflareAsync(HttpMethod.Get, $"/zones?page={page}&per_page=50", null, cancellationToken);
            foreach (var zone in document.RootElement.GetProperty("result").EnumerateArray())
            {
                var id = zone.GetProperty("id").GetString()!;
                var name = zone.GetProperty("name").GetString()!;
                var status = zone.TryGetProperty("status", out var statusValue) ? statusValue.GetString() : null;
                _zoneIds[name] = id;
                domains.Add(new DomainSummary(name, ProviderName, IsUsingProviderDns: string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)));
            }
            if (document.RootElement.TryGetProperty("result_info", out var info) && info.TryGetProperty("total_pages", out var count)) totalPages = count.GetInt32();
            page++;
        } while (page <= totalPages);
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
        var desiredIds = records.Where(record => !string.IsNullOrWhiteSpace(record.ProviderRecordId)).Select(record => record.ProviderRecordId!).ToHashSet(StringComparer.Ordinal);

        foreach (var oldRecord in current.Where(record => record.ProviderRecordId is not null && !desiredIds.Contains(record.ProviderRecordId)))
            using (await SendCloudflareAsync(HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{oldRecord.ProviderRecordId}", null, cancellationToken)) { }

        foreach (var record in records)
        {
            var body = ToApiRecord(record, domain);
            if (string.IsNullOrWhiteSpace(record.ProviderRecordId))
                using (await SendCloudflareAsync(HttpMethod.Post, $"/zones/{zoneId}/dns_records", body, cancellationToken)) { }
            else
            {
                var old = current.FirstOrDefault(item => item.ProviderRecordId == record.ProviderRecordId);
                if (old is null || !DnsRecord.ContentEquals(old, record))
                    using (await SendCloudflareAsync(HttpMethod.Patch, $"/zones/{zoneId}/dns_records/{record.ProviderRecordId}", body, cancellationToken)) { }
            }
        }
    }

    private async Task<IReadOnlyList<DnsRecord>> ReadRecordsAsync(string domain, string zoneId, CancellationToken cancellationToken)
    {
        var records = new List<DnsRecord>();
        var page = 1;
        var totalPages = 1;
        do
        {
            using var document = await SendCloudflareAsync(HttpMethod.Get, $"/zones/{zoneId}/dns_records?page={page}&per_page=100", null, cancellationToken);
            foreach (var item in document.RootElement.GetProperty("result").EnumerateArray())
            {
                var type = item.GetProperty("type").GetString()?.ToUpperInvariant() ?? "A";
                if (type is "SOA" or "HTTPS" or "SVCB") continue;
                var content = item.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? string.Empty : string.Empty;
                var priority = item.TryGetProperty("priority", out var priorityValue) && priorityValue.TryGetInt32(out var parsed) ? parsed : (int?)null;
                records.Add(new DnsRecord
                {
                    ProviderRecordId = item.GetProperty("id").GetString(),
                    Name = DnsProviderHelpers.ToRelativeName(item.GetProperty("name").GetString()!, domain), Type = type, Value = content,
                    TtlSeconds = item.TryGetProperty("ttl", out var ttl) ? ttl.GetInt32() : 1, Priority = type == "MX" ? priority : null
                });
            }
            if (document.RootElement.TryGetProperty("result_info", out var info) && info.TryGetProperty("total_pages", out var count)) totalPages = count.GetInt32();
            page++;
        } while (page <= totalPages);
        return records;
    }

    private static object ToApiRecord(DnsRecord record, string domain) => new
    {
        type = record.Type.ToUpperInvariant(), name = DnsProviderHelpers.ToAbsoluteName(record.Name, domain), content = record.Value, ttl = record.TtlSeconds,
        priority = record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) ? record.Priority : null
    };

    private async Task<string> GetZoneIdAsync(string domain, CancellationToken cancellationToken)
    {
        if (_zoneIds.TryGetValue(domain, out var id)) return id;
        await GetDomainsAsync(cancellationToken);
        return _zoneIds.TryGetValue(domain, out id) ? id : throw new InvalidOperationException($"Cloudflare zone {domain} was not found.");
    }

    private async Task<JsonDocument> SendCloudflareAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var document = await SendJsonAsync(method, ApiBase + path, body, _options.ApiToken, cancellationToken);
        if (document.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var message = document.RootElement.TryGetProperty("errors", out var errors) ? errors.ToString() : "Cloudflare rejected the request.";
            document.Dispose();
            throw new InvalidOperationException(message);
        }
        return document;
    }
}
