using System.Text.Json;
using NSDeck.Core.Models;

namespace NSDeck.Core.Storage;

public sealed class JsonZoneSnapshotStore(string rootPath) : IZoneSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task SaveAsync(ZoneSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var domainPath = GetDomainPath(snapshot.Domain);
        Directory.CreateDirectory(domainPath);
        var stamp = snapshot.CreatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(domainPath, $"{stamp}.json");
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<IReadOnlyList<ZoneSnapshot>> GetRecentAsync(
        string domain,
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        var domainPath = GetDomainPath(domain);
        if (!Directory.Exists(domainPath))
        {
            return [];
        }

        var snapshots = new List<ZoneSnapshot>();
        foreach (var path in Directory.EnumerateFiles(domainPath, "*.json")
                     .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                     .Take(count))
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<ZoneSnapshot>(stream, JsonOptions, cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private string GetDomainPath(string domain)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(domain.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character));
        return Path.Combine(rootPath, safeName);
    }
}
