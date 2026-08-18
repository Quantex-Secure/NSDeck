using NSDeck.Core.Models;

namespace NSDeck.Core.Storage;

public interface IZoneSnapshotStore
{
    Task SaveAsync(ZoneSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ZoneSnapshot>> GetRecentAsync(string domain, int count = 20, CancellationToken cancellationToken = default);
}
