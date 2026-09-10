namespace NSDeck.Core.Models;

public sealed class DnsRecord
{
    public Guid LocalId { get; init; } = Guid.NewGuid();
    public string? ProviderRecordId { get; init; }
    public string? ProviderMetadata { get; init; }
    public bool IsReadOnly { get; init; }
    public string? ReadOnlyReason { get; init; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public required string Value { get; set; }
    public int TtlSeconds { get; set; } = 1800;
    public int? Priority { get; set; }

    public DnsRecord Clone() => new()
    {
        LocalId = LocalId,
        ProviderRecordId = ProviderRecordId,
        ProviderMetadata = ProviderMetadata,
        IsReadOnly = IsReadOnly,
        ReadOnlyReason = ReadOnlyReason,
        Name = Name,
        Type = Type,
        Value = Value,
        TtlSeconds = TtlSeconds,
        Priority = Priority
    };

    public static bool ContentEquals(DnsRecord left, DnsRecord right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Services.DnsRecordSemantics.CanonicalValue(left), Services.DnsRecordSemantics.CanonicalValue(right), StringComparison.Ordinal) &&
        left.TtlSeconds == right.TtlSeconds &&
        left.Priority == right.Priority &&
        Services.DnsRecordSemantics.CanonicalMetadata(left.ProviderMetadata) == Services.DnsRecordSemantics.CanonicalMetadata(right.ProviderMetadata) && left.IsReadOnly == right.IsReadOnly;
}
