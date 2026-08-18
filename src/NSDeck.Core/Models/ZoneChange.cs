namespace NSDeck.Core.Models;

public enum ZoneChangeKind
{
    Add,
    Update,
    Delete
}

public sealed record ZoneChange(
    ZoneChangeKind Kind,
    DnsRecord Record,
    DnsRecord? Original = null)
{
    public string Action => Kind.ToString();
}
