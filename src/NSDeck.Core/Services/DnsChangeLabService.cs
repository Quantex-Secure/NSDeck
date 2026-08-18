using NSDeck.Core.Models;
using NSDeck.Core.Storage;

namespace NSDeck.Core.Services;

public sealed class DnsChangeLabService
{
    private static readonly TimeSpan[] DefaultVerificationDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15)
    ];

    private readonly IZoneSnapshotStore _snapshotStore;
    private readonly Func<DnsAuditEntry, CancellationToken, Task>? _audit;
    private readonly IReadOnlyList<TimeSpan> _verificationDelays;

    public DnsChangeLabService(
        IZoneSnapshotStore snapshotStore,
        Func<DnsAuditEntry, CancellationToken, Task>? audit = null,
        IReadOnlyList<TimeSpan>? verificationDelays = null)
    {
        _snapshotStore = snapshotStore;
        _audit = audit;
        _verificationDelays = verificationDelays ?? DefaultVerificationDelays;
    }

    public async Task<DnsInventoryLoadResult> LoadInventoryAsync(
        IReadOnlyCollection<DnsProviderScope> scopes,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var zones = new List<DnsInventoryZone>();
        var errors = new List<string>();
        var total = scopes.Sum(scope => scope.Domains.Count);
        var current = 0;

        foreach (var scope in scopes)
        {
            foreach (var domain in scope.Domains)
            {
                current++;
                progress?.Report($"Reading {domain.Name} from {scope.Provider.ProviderName} ({current} of {total})…");
                try
                {
                    var zone = await scope.Provider.GetZoneAsync(domain.Name, cancellationToken);
                    if (!zone.IsUsingProviderDns)
                    {
                        errors.Add($"{scope.Provider.ProviderName} / {domain.Name}: the zone is not active on this provider.");
                        continue;
                    }
                    zones.Add(new DnsInventoryZone(scope.Provider, scope.Provider.ProviderName, domain.Name,
                        zone.Records.Select(record => record.Clone()).ToArray(), zone.RetrievedAt));
                }
                catch (Exception exception)
                {
                    errors.Add($"{scope.Provider.ProviderName} / {domain.Name}: {exception.Message}");
                }
            }
        }

        return new DnsInventoryLoadResult(zones, errors);
    }

    public async Task<DnsChangeLabResult> ApplyAsync(
        IReadOnlyCollection<DnsPlannedChange> changes,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count == 0)
            return new DnsChangeLabResult(false, false, true, [new DnsZoneOperationResult("", "", "No changes")], []);

        var operations = new List<DnsZoneOperationResult>();
        var prepared = new List<PreparedZone>();

        foreach (var group in changes.GroupBy(change => change.Zone))
        {
            var zone = group.Key;
            progress?.Report($"Preflighting {zone.Domain} on {zone.ProviderName}…");
            var fresh = await zone.Provider.GetZoneAsync(zone.Domain, cancellationToken);
            if (ZoneComparer.Fingerprint(fresh.Records) != ZoneComparer.Fingerprint(zone.Records))
            {
                operations.Add(new DnsZoneOperationResult(zone.ProviderName, zone.Domain, "Stopped", "The zone changed after the Change Lab inventory was loaded."));
                return new DnsChangeLabResult(false, false, true, operations, changes.ToArray());
            }

            var desired = BuildDesiredRecords(fresh.Records, group.ToArray());
            var validation = ZoneValidator.Validate(desired);
            if (!validation.IsValid)
            {
                operations.Add(new DnsZoneOperationResult(zone.ProviderName, zone.Domain, "Stopped", validation.ErrorSummary));
                return new DnsChangeLabResult(false, false, true, operations, changes.ToArray());
            }

            var snapshot = new ZoneSnapshot(zone.Domain, zone.ProviderName, DateTimeOffset.Now,
                ZoneComparer.Fingerprint(fresh.Records), fresh.Records.Select(record => record.Clone()).ToArray());
            await _snapshotStore.SaveAsync(snapshot, cancellationToken);
            prepared.Add(new PreparedZone(zone, fresh.Records.Select(record => record.Clone()).ToArray(), desired, group.ToArray()));
        }

        var written = new List<PreparedZone>();
        try
        {
            foreach (var item in prepared)
            {
                progress?.Report($"Applying {item.Changes.Count} change{(item.Changes.Count == 1 ? string.Empty : "s")} to {item.Zone.Domain}…");
                await AuditAsync(new DnsAuditEntry(DateTimeOffset.Now, "change-lab-apply", item.Zone.ProviderName,
                    item.Zone.Domain, "started", item.Changes.Count, ZoneComparer.Fingerprint(item.Desired)), cancellationToken);
                await item.Zone.Provider.ReplaceZoneAsync(item.Zone.Domain, item.Desired, cancellationToken);
                written.Add(item);
                var verified = await WaitForVerificationAsync(item.Zone, item.Desired, progress, cancellationToken);
                if (!verified)
                    throw new InvalidOperationException($"{item.Zone.ProviderName} accepted the update for {item.Zone.Domain}, but it did not verify within 30 seconds.");
                operations.Add(new DnsZoneOperationResult(item.Zone.ProviderName, item.Zone.Domain, "Applied and verified"));
                await AuditAsync(new DnsAuditEntry(DateTimeOffset.Now, "change-lab-apply", item.Zone.ProviderName,
                    item.Zone.Domain, "verified", item.Changes.Count, ZoneComparer.Fingerprint(item.Desired)), cancellationToken);
            }

            return new DnsChangeLabResult(true, false, true, operations, changes.ToArray());
        }
        catch (Exception exception)
        {
            operations.Add(new DnsZoneOperationResult("Change Lab", "Transaction", "Failed", exception.Message));
            var rollbackSucceeded = true;
            foreach (var item in written.AsEnumerable().Reverse())
            {
                try
                {
                    progress?.Report($"Rolling back {item.Zone.Domain} on {item.Zone.ProviderName}…");
                    await item.Zone.Provider.ReplaceZoneAsync(item.Zone.Domain, item.Original, CancellationToken.None);
                    var verified = await WaitForVerificationAsync(item.Zone, item.Original, progress, CancellationToken.None);
                    rollbackSucceeded &= verified;
                    operations.Add(new DnsZoneOperationResult(item.Zone.ProviderName, item.Zone.Domain,
                        verified ? "Rolled back and verified" : "Rollback verification timed out"));
                    await AuditAsync(new DnsAuditEntry(DateTimeOffset.Now, "change-lab-rollback", item.Zone.ProviderName,
                        item.Zone.Domain, verified ? "verified" : "verification-timeout", item.Changes.Count,
                        ZoneComparer.Fingerprint(item.Original)), CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    rollbackSucceeded = false;
                    operations.Add(new DnsZoneOperationResult(item.Zone.ProviderName, item.Zone.Domain, "Rollback failed", rollbackException.Message));
                }
            }

            return new DnsChangeLabResult(false, written.Count > 0, rollbackSucceeded, operations, changes.ToArray());
        }
    }

    private async Task<bool> WaitForVerificationAsync(
        DnsInventoryZone zone,
        IReadOnlyList<DnsRecord> desired,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var fingerprint = ZoneComparer.Fingerprint(desired);
        for (var attempt = 0; attempt < _verificationDelays.Count; attempt++)
        {
            var delay = _verificationDelays[attempt];
            if (delay > TimeSpan.Zero)
            {
                progress?.Report($"Waiting for {zone.ProviderName} to publish {zone.Domain}… check {attempt + 1} of {_verificationDelays.Count}");
                await Task.Delay(delay, cancellationToken);
            }
            var check = await zone.Provider.GetZoneAsync(zone.Domain, cancellationToken);
            if (ZoneComparer.Fingerprint(check.Records) == fingerprint) return true;
        }
        return false;
    }

    private static IReadOnlyList<DnsRecord> BuildDesiredRecords(
        IReadOnlyList<DnsRecord> current,
        IReadOnlyList<DnsPlannedChange> changes)
    {
        var desired = current.Select(record => record.Clone()).ToList();
        foreach (var change in changes)
        {
            var index = desired.FindIndex(record => DnsRecord.ContentEquals(record, change.Original));
            if (index < 0)
                throw new InvalidOperationException($"The original {change.Original.Name} {change.Original.Type} record could not be matched during preflight.");
            var existing = desired[index];
            desired[index] = new DnsRecord
            {
                LocalId = existing.LocalId,
                ProviderRecordId = existing.ProviderRecordId,
                Name = change.Updated.Name,
                Type = change.Updated.Type,
                Value = change.Updated.Value,
                TtlSeconds = change.Updated.TtlSeconds,
                Priority = change.Updated.Priority
            };
        }
        return desired;
    }

    private Task AuditAsync(DnsAuditEntry entry, CancellationToken cancellationToken) =>
        _audit?.Invoke(entry, cancellationToken) ?? Task.CompletedTask;

    private sealed record PreparedZone(
        DnsInventoryZone Zone,
        IReadOnlyList<DnsRecord> Original,
        IReadOnlyList<DnsRecord> Desired,
        IReadOnlyList<DnsPlannedChange> Changes);
}
