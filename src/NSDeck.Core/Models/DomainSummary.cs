namespace NSDeck.Core.Models;

public sealed record DomainSummary(
    string Name,
    string Provider,
    bool IsExpired = false,
    bool IsLocked = false,
    bool? IsUsingProviderDns = null,
    string? ZoneId = null,
    bool IsPublic = true)
{
    public string DisplayName => ZoneId is null ? Name : $"{Name} ({(IsPublic ? "public" : "private")}, {ZoneId})";
}
