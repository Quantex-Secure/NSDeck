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
                _zoneIds[name] = _zoneIds.ContainsKey(name) ? string.Empty : id;
                domains.Add(new DomainSummary(name, ProviderName, IsUsingProviderDns: string.Equals(status, "active", StringComparison.OrdinalIgnoreCase), ZoneId: id));
            }
            if (document.RootElement.TryGetProperty("result_info", out var info) && info.TryGetProperty("total_pages", out var count)) totalPages = count.GetInt32();
            page++;
        } while (page <= totalPages);
        return domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default) => GetZoneAsync(new DomainSummary(domain, ProviderName), cancellationToken);

    public async Task<DnsZone> GetZoneAsync(DomainSummary target, CancellationToken cancellationToken = default)
    {
        var domain = target.Name;
        var zoneId = target.ZoneId ?? await GetZoneIdAsync(domain, cancellationToken);
        return new DnsZone(domain, ProviderName, await ReadRecordsAsync(domain, zoneId, cancellationToken), DateTimeOffset.Now);
    }

    public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct = default) => ReplaceCoreAsync(new DomainSummary(domain, ProviderName), records, null, ct);
    public Task ReplaceZoneAsync(DomainSummary zone, IReadOnlyList<DnsRecord> records, CancellationToken ct = default) => ReplaceCoreAsync(zone, records, null, ct);
    public Task ReplaceZoneGuardedAsync(DomainSummary zone, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> records, CancellationToken ct = default) => ReplaceCoreAsync(zone, records, expected, ct);
    public Task ReplaceZoneGuardedAsync(string domain, IReadOnlyList<DnsRecord> expected, IReadOnlyList<DnsRecord> records, CancellationToken ct = default) => ReplaceCoreAsync(new DomainSummary(domain, ProviderName), records, expected, ct);

    private async Task ReplaceCoreAsync(DomainSummary target, IReadOnlyList<DnsRecord> records, IReadOnlyList<DnsRecord>? expected, CancellationToken cancellationToken)
    {
        var domain = target.Name;
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        var zoneId = target.ZoneId ?? await GetZoneIdAsync(domain, cancellationToken);
        var allCurrent = await ReadRecordsAsync(domain, zoneId, cancellationToken);
        ZoneWriteGuard.Check(allCurrent, expected, records);
        var current = allCurrent.Where(r => !r.IsReadOnly).ToArray();
        records = records.Where(r => !r.IsReadOnly).ToArray();
        // Snapshot IDs may no longer exist. Match against this zone, then create missing records.
        var matched = new HashSet<string>(StringComparer.Ordinal);
        records = records.Select(record =>
        {
            var old = current.FirstOrDefault(r => r.ProviderRecordId == record.ProviderRecordId && !matched.Contains(r.ProviderRecordId!))
                ?? current.FirstOrDefault(r => !matched.Contains(r.ProviderRecordId!) && DnsRecord.ContentEquals(r, record))
                ?? current.FirstOrDefault(r => !matched.Contains(r.ProviderRecordId!) && r.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase) && r.Type.Equals(record.Type, StringComparison.OrdinalIgnoreCase));
            if (old?.ProviderRecordId is not null) matched.Add(old.ProviderRecordId);
            return new DnsRecord { LocalId = record.LocalId, ProviderRecordId = old?.ProviderRecordId,
                Name = record.Name, Type = record.Type, Value = record.Value, TtlSeconds = record.TtlSeconds,
                Priority = record.Priority, ProviderMetadata = record.ProviderMetadata ?? old?.ProviderMetadata };
        }).ToArray();
        var bodies = records.Select(record => ToApiRecord(record, domain)).ToArray();
        var desiredIds = records.Where(record => !string.IsNullOrWhiteSpace(record.ProviderRecordId)).Select(record => record.ProviderRecordId!).ToHashSet(StringComparer.Ordinal);

        foreach (var oldRecord in current.Where(record => record.ProviderRecordId is not null && !desiredIds.Contains(record.ProviderRecordId)))
            using (await SendCloudflareAsync(HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{oldRecord.ProviderRecordId}", null, cancellationToken)) { }

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var body = bodies[index];
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
                var readOnly = !DnsRecordTypes.All.Contains(type, StringComparer.OrdinalIgnoreCase);
                var content = item.TryGetProperty("content", out var contentValue) ? contentValue.GetString() ?? string.Empty : string.Empty;
                var priority = item.TryGetProperty("priority", out var priorityValue) && priorityValue.TryGetInt32(out var parsed) ? parsed : (int?)null;
                if (type == "SRV")
                    content = item.TryGetProperty("data", out var srv)
                        ? $"{srv.GetProperty("priority").GetInt32()} {srv.GetProperty("weight").GetInt32()} {srv.GetProperty("port").GetInt32()} {srv.GetProperty("target").GetString()}"
                        : $"{priority ?? 0} {content}";
                if (type == "TXT") content = DnsRecordSemantics.DecodeText(content);
                records.Add(new DnsRecord
                {
                    ProviderRecordId = item.GetProperty("id").GetString(),
                    IsReadOnly = readOnly, ReadOnlyReason = readOnly ? "Manage this record type in Cloudflare." : null,
                    ProviderMetadata = ReadMetadata(item),
                    Name = DnsProviderHelpers.ToRelativeName(item.GetProperty("name").GetString()!, domain), Type = type, Value = content,
                    TtlSeconds = item.TryGetProperty("ttl", out var ttl) ? ttl.GetInt32() : 1, Priority = type == "MX" ? priority : null
                });
            }
            if (document.RootElement.TryGetProperty("result_info", out var info) && info.TryGetProperty("total_pages", out var count)) totalPages = count.GetInt32();
            page++;
        } while (page <= totalPages);
        return records;
    }

    private static string? ReadMetadata(JsonElement item)
    {
        var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        if (item.TryGetProperty("proxied", out var proxied)) values["proxied"] = proxied.Clone();
        foreach (var key in new[] { "proxied", "comment", "tags", "settings" })
            if (item.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) &&
                !(value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())) &&
                !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) &&
                !(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(p => p.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null))) values[key] = value.Clone();
        return values.Count == 0 ? null : JsonSerializer.Serialize(values);
    }

    private static object ToApiRecord(DnsRecord record, string domain)
    {
        var body = new Dictionary<string, object?> { ["type"] = record.Type.ToUpperInvariant(),
            ["name"] = DnsProviderHelpers.ToAbsoluteName(record.Name, domain), ["content"] = record.Value, ["ttl"] = record.TtlSeconds };
        if (record.Type.Equals("MX", StringComparison.OrdinalIgnoreCase)) body["priority"] = record.Priority;
        if (record.Type.Equals("SRV", StringComparison.OrdinalIgnoreCase))
        {
            var parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            body.Remove("content");
            body["data"] = new { priority = ushort.Parse(parts[0]), weight = ushort.Parse(parts[1]), port = ushort.Parse(parts[2]), target = parts[3] };
        }
        if (record.ProviderMetadata is not null)
        {
            using var metadata = JsonDocument.Parse(record.ProviderMetadata);
            foreach (var key in new[] { "proxied", "comment", "tags", "settings" })
                if (metadata.RootElement.TryGetProperty(key, out var value)) body[key] = value.Clone();
        }
        return body;
    }

    private async Task<string> GetZoneIdAsync(string domain, CancellationToken cancellationToken)
    {
        if (_zoneIds.TryGetValue(domain, out var id) && !string.IsNullOrEmpty(id)) return id;
        await GetDomainsAsync(cancellationToken);
        return _zoneIds.TryGetValue(domain, out id) && !string.IsNullOrEmpty(id) ? id : throw new InvalidOperationException($"Cloudflare zone {domain} was not found.");
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
