namespace NSDeck.Core.Services;

public static class DnsRecordTypes
{
    public static IReadOnlyList<string> All { get; } =
    [
        "A", "AAAA", "ALIAS", "CAA", "CNAME", "DS", "HTTPS", "MX", "MXE", "NAPTR", "NS", "PTR",
        "SPF", "SRV", "SSHFP", "SVCB", "TLSA", "TXT", "URL", "URL301", "FRAME"
    ];
}
