using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public static class ZoneRecovery
{
    // Restore only touched record sets. Independent administrator edits survive recovery.
    // Ambiguous partial changes inside a set are left for an administrator to inspect.
    public static IReadOnlyList<DnsRecord> Build(IReadOnlyList<DnsRecord> original, IReadOnlyList<DnsRecord> desired, IReadOnlyList<DnsRecord> current)
    {
        static string Key(DnsRecord r) => r.Name.Trim().TrimEnd('.').ToLowerInvariant() + "|" + r.Type.ToUpperInvariant();
        var restored = current.Select(r => r.Clone()).ToList();
        foreach (var key in original.Concat(desired).Select(Key).Distinct())
        {
            var before = original.Where(r => Key(r) == key).ToArray();
            var after = desired.Where(r => Key(r) == key).ToArray();
            if (ZoneComparer.Fingerprint(before) == ZoneComparer.Fingerprint(after)) continue;
            var live = current.Where(r => Key(r) == key).ToArray();
            if (ZoneComparer.Fingerprint(live) == ZoneComparer.Fingerprint(before)) continue;
            if (ZoneComparer.Fingerprint(live) != ZoneComparer.Fingerprint(after))
                throw new InvalidOperationException($"Recovery requires review: {key} contains an intervening or partial change. No recovery write was sent for this zone; use its saved snapshot.");
            restored.RemoveAll(r => Key(r) == key);
            restored.AddRange(before.Select(r => r.Clone()));
        }
        return restored;
    }
}
