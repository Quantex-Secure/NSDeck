using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Dns.v1;
using Google.Apis.Dns.v1.Data;
using Google.Apis.Services;
using GoogleRecordSet = Google.Apis.Dns.v1.Data.ResourceRecordSet;

namespace NSDeck.Providers.Cloud;

public sealed class GoogleCloudDnsProvider : IDnsProvider, IDisposable
{
    private readonly GoogleCloudDnsOptions _options;
    private readonly DnsService _service;
    private readonly Dictionary<string, string> _zoneNames = new(StringComparer.OrdinalIgnoreCase);

    private GoogleCloudDnsProvider(GoogleCloudDnsOptions options, DnsService service)
    {
        _options = options;
        _service = service;
    }

    public static async Task<GoogleCloudDnsProvider> CreateAsync(GoogleCloudDnsOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectId)) throw new ArgumentException("A Google Cloud project ID is required.", nameof(options));
        GoogleCredential credential;
        if (string.IsNullOrWhiteSpace(options.ServiceAccountJsonPath))
            credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
        else
        {
            var serviceAccount = await CredentialFactory.FromFileAsync<ServiceAccountCredential>(options.ServiceAccountJsonPath, cancellationToken);
            credential = serviceAccount.ToGoogleCredential();
        }
        if (credential.IsCreateScopedRequired) credential = credential.CreateScoped(DnsService.Scope.NdevClouddnsReadwrite);
        var service = new DnsService(new BaseClientService.Initializer { HttpClientInitializer = credential, ApplicationName = "NSDeck" });
        return new GoogleCloudDnsProvider(options, service);
    }

    public string ProviderName => "Google Cloud DNS";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        _zoneNames.Clear();
        var domains = new List<DomainSummary>();
        string? pageToken = null;
        do
        {
            var request = _service.ManagedZones.List(_options.ProjectId);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var zone in response.ManagedZones ?? [])
            {
                if (!string.Equals(zone.Visibility, "public", StringComparison.OrdinalIgnoreCase)) continue;
                var domain = zone.DnsName.TrimEnd('.');
                _zoneNames[domain] = zone.Name;
                domains.Add(new DomainSummary(domain, ProviderName));
            }
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));
        return domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        var zone = await GetZoneNameAsync(domain, cancellationToken);
        return new DnsZone(domain, ProviderName, Flatten(domain, await ReadSetsAsync(zone, cancellationToken)), DateTimeOffset.Now);
    }

    public async Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default)
    {
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        var zone = await GetZoneNameAsync(domain, cancellationToken);
        var currentSets = (await ReadSetsAsync(zone, cancellationToken)).Where(set => IsEditable(set, domain)).ToArray();
        var currentGroups = Flatten(domain, currentSets).GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());
        var currentSetByKey = currentSets.ToDictionary(set => DnsProviderHelpers.GroupKey(DnsProviderHelpers.ToRelativeName(set.Name, domain), set.Type));
        var desiredGroups = records.GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());
        var additions = new List<GoogleRecordSet>();
        var deletions = new List<GoogleRecordSet>();

        foreach (var old in currentGroups.Where(pair => !desiredGroups.ContainsKey(pair.Key))) deletions.Add(currentSetByKey[old.Key]);
        foreach (var desired in desiredGroups)
        {
            if (currentGroups.TryGetValue(desired.Key, out var old) && DnsProviderHelpers.GroupsEqual(old, desired.Value)) continue;
            if (currentSetByKey.TryGetValue(desired.Key, out var oldSet)) deletions.Add(oldSet);
            additions.Add(BuildSet(domain, desired.Value));
        }
        if (additions.Count == 0 && deletions.Count == 0) return;
        await _service.Changes.Create(new Change { Additions = additions, Deletions = deletions }, _options.ProjectId, zone).ExecuteAsync(cancellationToken);
    }

    private async Task<List<GoogleRecordSet>> ReadSetsAsync(string zone, CancellationToken cancellationToken)
    {
        var sets = new List<GoogleRecordSet>();
        string? pageToken = null;
        do
        {
            var request = _service.ResourceRecordSets.List(_options.ProjectId, zone);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            sets.AddRange(response.Rrsets ?? []);
            pageToken = response.NextPageToken;
        } while (!string.IsNullOrWhiteSpace(pageToken));
        return sets;
    }

    private static IReadOnlyList<DnsRecord> Flatten(string domain, IEnumerable<GoogleRecordSet> sets)
    {
        var records = new List<DnsRecord>();
        foreach (var set in sets.Where(set => IsEditable(set, domain)))
        {
            foreach (var value in set.Rrdatas ?? [])
            {
                var parsed = DnsProviderHelpers.ParsePriorityValue(set.Type, value);
                records.Add(new DnsRecord
                {
                    Name = DnsProviderHelpers.ToRelativeName(set.Name, domain), Type = set.Type, Value = parsed.Value,
                    TtlSeconds = checked((int)(set.Ttl ?? 300)), Priority = parsed.Priority
                });
            }
        }
        return records;
    }

    private static bool IsEditable(GoogleRecordSet set, string domain)
    {
        var apex = DnsProviderHelpers.ToRelativeName(set.Name, domain) == "@";
        return set.Type != "SOA" && !(set.Type == "NS" && apex) && set.Rrdatas is { Count: > 0 };
    }

    private static GoogleRecordSet BuildSet(string domain, IReadOnlyList<DnsRecord> records)
    {
        if (records.Select(record => record.TtlSeconds).Distinct().Count() != 1)
            throw new InvalidOperationException($"Google Cloud DNS requires every value in {records[0].Name} {records[0].Type} to use the same TTL.");
        return new GoogleRecordSet
        {
            Name = DnsProviderHelpers.ToAbsoluteName(records[0].Name, domain) + ".", Type = records[0].Type.ToUpperInvariant(),
            Ttl = records[0].TtlSeconds, Rrdatas = records.Select(DnsProviderHelpers.FormatValue).ToList()
        };
    }

    private async Task<string> GetZoneNameAsync(string domain, CancellationToken cancellationToken)
    {
        if (_zoneNames.TryGetValue(domain, out var name)) return name;
        await GetDomainsAsync(cancellationToken);
        return _zoneNames.TryGetValue(domain, out name) ? name : throw new InvalidOperationException($"Google Cloud DNS zone {domain} was not found.");
    }

    public void Dispose() => _service.Dispose();
}
