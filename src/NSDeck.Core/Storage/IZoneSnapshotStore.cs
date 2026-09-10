using NSDeck.Core.Models;

namespace NSDeck.Core.Storage;

public interface IZoneSnapshotStore
{
    Task SaveAsync(ZoneSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ZoneSnapshot>> GetRecentAsync(string domain, int count = 20, CancellationToken cancellationToken = default);
    async Task<IReadOnlyList<ZoneSnapshot>> GetRecentForTargetAsync(string domain, string accountId, string? zoneId, int count = 20, CancellationToken cancellationToken = default) =>
        (await GetRecentAsync(domain, int.MaxValue, cancellationToken)).Where(s => s.AccountId == accountId && s.ZoneId == zoneId).Take(count).ToArray();
}
