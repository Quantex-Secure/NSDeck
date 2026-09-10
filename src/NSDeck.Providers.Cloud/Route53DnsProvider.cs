using Amazon;
using Amazon.Runtime;
using Amazon.Route53;
using Amazon.Route53.Model;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;
using AwsResourceRecord = Amazon.Route53.Model.ResourceRecord;
using AwsResourceRecordSet = Amazon.Route53.Model.ResourceRecordSet;

namespace NSDeck.Providers.Cloud;

public sealed class Route53DnsProvider : IDnsProvider, IDisposable
{
    private readonly IAmazonRoute53 _client;
    private readonly Dictionary<string, string> _zoneIds = new(StringComparer.OrdinalIgnoreCase);

    public Route53DnsProvider(Route53DnsOptions options, IAmazonRoute53? client = null)
    {
        if (client is not null)
        {
            _client = client;
            return;
        }
        if (string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.SecretAccessKey))
            throw new ArgumentException("An AWS access key ID and secret access key are required.", nameof(options));
        AWSCredentials credentials = string.IsNullOrWhiteSpace(options.SessionToken)
            ? new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey)
            : new SessionAWSCredentials(options.AccessKeyId, options.SecretAccessKey, options.SessionToken);
        _client = new AmazonRoute53Client(credentials, RegionEndpoint.USEast1);
    }

    public ZoneValidationResult ValidateRecords(IReadOnlyList<DnsRecord> records)
    {
        var basic = ZoneValidator.Validate(records);
        if (!basic.IsValid) return basic;
        try { foreach (var group in records.Where(r => !r.IsReadOnly).GroupBy(DnsProviderHelpers.GroupKey)) BuildSet("example.invalid", group.ToArray()); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        { return new ZoneValidationResult([new ValidationIssue(ex.Message)]); }
        return basic;
    }

    public string ProviderName => "AWS Route 53";

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        _zoneIds.Clear();
        var domains = new List<DomainSummary>();
        string? marker = null;
        do
        {
            var response = await _client.ListHostedZonesAsync(new ListHostedZonesRequest { Marker = marker, MaxItems = "100" }, cancellationToken);
            foreach (var zone in response.HostedZones ?? [])
            {
                var name = zone.Name.TrimEnd('.');
                _zoneIds[name] = _zoneIds.ContainsKey(name) ? string.Empty : zone.Id;
                domains.Add(new DomainSummary(name, ProviderName, ZoneId: zone.Id, IsPublic: zone.Config?.PrivateZone != true));
            }
            marker = response.IsTruncated == true ? response.NextMarker : null;
        } while (!string.IsNullOrWhiteSpace(marker));
        return domains.OrderBy(domain => domain.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default) => GetZoneAsync(new DomainSummary(domain, ProviderName), cancellationToken);

    public async Task<DnsZone> GetZoneAsync(DomainSummary target, CancellationToken cancellationToken = default)
    {
        var domain = target.Name;
        var zoneId = target.ZoneId ?? await GetZoneIdAsync(domain, cancellationToken);
        var sets = await ReadRecordSetsAsync(zoneId, cancellationToken);
        return new DnsZone(domain, ProviderName, Flatten(domain, sets), DateTimeOffset.Now);
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
        var currentSets = await ReadRecordSetsAsync(zoneId, cancellationToken);
        ZoneWriteGuard.Check(Flatten(domain, currentSets), expected, records);
        records = records.Where(r => !r.IsReadOnly).ToArray();
        var currentEditable = currentSets.Where(set => IsEditableSet(set, domain)).ToArray();
        var currentGroups = Flatten(domain, currentEditable).GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());
        var currentSetByKey = currentEditable.ToDictionary(set => DnsProviderHelpers.GroupKey(DnsProviderHelpers.ToRelativeName(set.Name, domain), set.Type.Value));
        var desiredGroups = records.GroupBy(DnsProviderHelpers.GroupKey).ToDictionary(group => group.Key, group => group.ToArray());
        var changes = new List<Change>();

        foreach (var old in currentGroups.Where(pair => !desiredGroups.ContainsKey(pair.Key)))
            changes.Add(new Change { Action = ChangeAction.DELETE, ResourceRecordSet = currentSetByKey[old.Key] });

        foreach (var desired in desiredGroups)
        {
            if (currentGroups.TryGetValue(desired.Key, out var old) && DnsProviderHelpers.GroupsEqual(old, desired.Value)) continue;
            changes.Add(new Change { Action = ChangeAction.UPSERT, ResourceRecordSet = BuildSet(domain, desired.Value) });
        }

        if (changes.Count == 0) return;
        await _client.ChangeResourceRecordSetsAsync(new ChangeResourceRecordSetsRequest
        {
            HostedZoneId = zoneId,
            ChangeBatch = new ChangeBatch { Comment = "NSDeck guarded zone update", Changes = changes }
        }, cancellationToken);
    }

    private async Task<List<AwsResourceRecordSet>> ReadRecordSetsAsync(string zoneId, CancellationToken cancellationToken)
    {
        var sets = new List<AwsResourceRecordSet>();
        string? name = null;
        RRType? type = null;
        string? identifier = null;
        do
        {
            var response = await _client.ListResourceRecordSetsAsync(new ListResourceRecordSetsRequest
            {
                HostedZoneId = zoneId, StartRecordName = name, StartRecordType = type, StartRecordIdentifier = identifier, MaxItems = "300"
            }, cancellationToken);
            sets.AddRange(response.ResourceRecordSets ?? []);
            if (response.IsTruncated != true) break;
            name = response.NextRecordName;
            type = response.NextRecordType;
            identifier = response.NextRecordIdentifier;
        } while (true);
        return sets;
    }

    private static IReadOnlyList<DnsRecord> Flatten(string domain, IEnumerable<AwsResourceRecordSet> sets)
    {
        var records = new List<DnsRecord>();
        foreach (var set in sets)
        {
            if (!IsEditableSet(set, domain))
            {
                records.Add(new DnsRecord { Name = DnsProviderHelpers.ToRelativeName(set.Name, domain), Type = set.Type.Value,
                    Value = System.Text.Json.JsonSerializer.Serialize(set), IsReadOnly = true,
                    ReadOnlyReason = "Provider-managed or advanced routing record. Use the Route 53 console." });
                continue;
            }
            AddEditable(set);
        }
        return records;

        void AddEditable(AwsResourceRecordSet set)
        {
            var type = set.Type.Value;
            foreach (var value in set.ResourceRecords ?? [])
            {
                var parsed = DnsProviderHelpers.ParsePriorityValue(type, value.Value);
                records.Add(new DnsRecord
                {
                    Name = DnsProviderHelpers.ToRelativeName(set.Name, domain), Type = type, Value = parsed.Value,
                    TtlSeconds = checked((int)(set.TTL ?? 300)), Priority = parsed.Priority
                });
            }
        }
    }

    private static bool IsEditableSet(AwsResourceRecordSet set, string domain)
    {
        var type = set.Type?.Value;
        var apex = DnsProviderHelpers.ToRelativeName(set.Name, domain) == "@";
        return DnsRecordTypes.All.Contains(type ?? "") && type != "SOA" && !(type == "NS" && apex) && set.AliasTarget is null && string.IsNullOrWhiteSpace(set.SetIdentifier);
    }

    private static AwsResourceRecordSet BuildSet(string domain, IReadOnlyList<DnsRecord> records)
    {
        if (records.Select(record => record.TtlSeconds).Distinct().Count() != 1)
            throw new InvalidOperationException($"Route 53 requires every value in {records[0].Name} {records[0].Type} to use the same TTL.");
        return new AwsResourceRecordSet
        {
            Name = DnsProviderHelpers.ToAbsoluteName(records[0].Name, domain) + ".",
            Type = RRType.FindValue(records[0].Type.ToUpperInvariant()), TTL = records[0].TtlSeconds,
            ResourceRecords = records.Select(record => new AwsResourceRecord { Value = DnsProviderHelpers.FormatValue(record) }).ToList()
        };
    }

    private async Task<string> GetZoneIdAsync(string domain, CancellationToken cancellationToken)
    {
        if (_zoneIds.TryGetValue(domain, out var id) && !string.IsNullOrEmpty(id)) return id;
        await GetDomainsAsync(cancellationToken);
        return _zoneIds.TryGetValue(domain, out id) && !string.IsNullOrEmpty(id) ? id : throw new InvalidOperationException($"Route 53 hosted zone {domain} was not found.");
    }

    public void Dispose() => _client.Dispose();
}
