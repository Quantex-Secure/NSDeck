namespace NSDeck.Core.Models;

public sealed record DomainSummary(
    string Name,
    string Provider,
    bool IsExpired = false,
    bool IsLocked = false,
    bool? IsUsingProviderDns = null)
{
    public string DisplayName => Name;
}
