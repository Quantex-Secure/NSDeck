using System.Security.Cryptography;
using System.Text;
using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public static class ZoneComparer
{
    public static IReadOnlyList<ZoneChange> Diff(
        IReadOnlyCollection<DnsRecord> original,
        IReadOnlyCollection<DnsRecord> current)
    {
        var changes = new List<ZoneChange>();
        var originalById = original.ToDictionary(record => record.LocalId);
        var currentById = current.ToDictionary(record => record.LocalId);

        foreach (var record in current)
        {
            if (!originalById.TryGetValue(record.LocalId, out var oldRecord))
            {
                changes.Add(new ZoneChange(ZoneChangeKind.Add, record.Clone()));
            }
            else if (!DnsRecord.ContentEquals(oldRecord, record))
            {
                changes.Add(new ZoneChange(ZoneChangeKind.Update, record.Clone(), oldRecord.Clone()));
            }
        }

        foreach (var record in original)
        {
            if (!currentById.ContainsKey(record.LocalId))
            {
                changes.Add(new ZoneChange(ZoneChangeKind.Delete, record.Clone(), record.Clone()));
            }
        }

        return changes
            .OrderBy(change => change.Record.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Record.Type, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Fingerprint(IEnumerable<DnsRecord> records)
    {
        var canonical = records
            .Select(record => string.Join('\u001f',
                record.Name.Trim().ToLowerInvariant(),
                record.Type.Trim().ToUpperInvariant(),
                record.Value.Trim(),
                record.TtlSeconds,
                record.Priority?.ToString() ?? string.Empty))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonical)));
        return Convert.ToHexString(bytes);
    }
}
