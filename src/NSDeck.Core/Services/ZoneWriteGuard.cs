using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public static class ZoneWriteGuard
{
    public static void Check(IReadOnlyList<DnsRecord> current, IReadOnlyList<DnsRecord>? expected, IReadOnlyList<DnsRecord> desired)
    {
        if (expected is not null && ZoneComparer.Fingerprint(current) != ZoneComparer.Fingerprint(expected))
            throw new InvalidOperationException("The provider changed since preflight. Refresh and review the zone before writing.");
        var validation = ZoneValidator.Validate(desired);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        if (ZoneComparer.Fingerprint(current.Where(r => r.IsReadOnly), true) != ZoneComparer.Fingerprint(desired.Where(r => r.IsReadOnly), true))
            throw new InvalidOperationException("Provider-managed and unsupported records must remain unchanged.");
        if (desired.Any(r => !r.IsReadOnly && current.Any(p => p.IsReadOnly && p.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && p.Type.Equals(r.Type, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("A proposed record overlaps a provider-managed record set. Use the provider console.");
    }
}
