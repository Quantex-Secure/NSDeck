namespace NSDeck.Core.Models;

public sealed class DnsRecord
{
    public Guid LocalId { get; init; } = Guid.NewGuid();
    public string? ProviderRecordId { get; init; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public required string Value { get; set; }
    public int TtlSeconds { get; set; } = 1800;
    public int? Priority { get; set; }

    public DnsRecord Clone() => new()
    {
        LocalId = LocalId,
        ProviderRecordId = ProviderRecordId,
        Name = Name,
        Type = Type,
        Value = Value,
        TtlSeconds = TtlSeconds,
        Priority = Priority
    };

    public static bool ContentEquals(DnsRecord left, DnsRecord right) =>
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Value, right.Value, StringComparison.Ordinal) &&
        left.TtlSeconds == right.TtlSeconds &&
        left.Priority == right.Priority;
}
