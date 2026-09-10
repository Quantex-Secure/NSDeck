using System.Net;
using NSDeck.Core.Models;

namespace NSDeck.Core.Services;

public enum DnsFindingSeverity { Information, Warning, Critical }
public sealed record DnsPracticeFinding(string Rule, DnsFindingSeverity Severity, string Title, string Explanation, string SourceUrl);
public enum DnsSetupTemplate { WebsiteAddress, WebsiteAlias, MailExchanger, Spf, DmarcMonitoring, Dkim, Caa, NoMail }

public static class DnsBestPracticeScanner
{
    public const string SpfSource = "https://www.rfc-editor.org/rfc/rfc7208.html";
    public const string DmarcSource = "https://www.rfc-editor.org/rfc/rfc9989.html";
    public const string CaaSource = "https://www.rfc-editor.org/rfc/rfc8659.html";
    public const string DkimSource = "https://www.rfc-editor.org/rfc/rfc6376.html";
    public const string DnsSource = "https://www.rfc-editor.org/rfc/rfc1034.html";
    public const string NullMxSource = "https://www.rfc-editor.org/rfc/rfc7505.html";

    public static IReadOnlyList<DnsPracticeFinding> Scan(string domain, IReadOnlyList<DnsRecord> records)
    {
        var findings = new List<DnsPracticeFinding>();
        void Add(string rule, DnsFindingSeverity severity, string title, string explanation, string source) => findings.Add(new(rule, severity, title, explanation, source));
        string Name(DnsRecord r) => Relative(r.Name, domain);
        var editable = records.Where(r => !r.IsReadOnly).ToArray();
        foreach (var issue in ZoneValidator.Validate(records).Issues)
            Add("DNS-VALIDATION", DnsFindingSeverity.Critical, "Record validation", issue.Message, DnsSource);
        var spf = editable.Where(r => r.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) && Txt(r.Value).StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var group in spf.GroupBy(Name))
        {
            if (group.Count() > 1) Add("SPF-DUPLICATE", DnsFindingSeverity.Critical, $"Multiple SPF records at {group.Key}", "Publish one SPF policy at each name. Combine authorized senders after reviewing all mail services; do not add another policy.", SpfSource);
            foreach (var record in group)
            {
                var tokens = Txt(record.Value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Any(t => t.Equals("+all", StringComparison.OrdinalIgnoreCase) || t.Equals("all", StringComparison.OrdinalIgnoreCase)))
                    Add("SPF-ALLOW-ALL", DnsFindingSeverity.Critical, $"SPF permits every sender at {group.Key}", "Inventory legitimate senders and replace the allow-all mechanism with an appropriate policy. Changing this without a sender inventory can disrupt mail.", SpfSource);
                var lookups = tokens.Count(t => new[] { "include", "a", "mx", "ptr", "exists", "redirect" }.Contains(t.TrimStart('+', '-', '~', '?').Split(':', '=')[0], StringComparer.OrdinalIgnoreCase));
                if (lookups > 10) Add("SPF-LOOKUPS", DnsFindingSeverity.Critical, "SPF exceeds the lookup-term limit", "This policy alone contains more than ten lookup-causing terms. Nested includes and redirects can add more; this local scan does not resolve them.", SpfSource);
                else if (lookups > 0) Add("SPF-LOOKUPS-UNVERIFIED", DnsFindingSeverity.Information, $"Verify nested SPF lookups at {group.Key}", $"Found {lookups} local lookup-causing terms. Check the full evaluated include/redirect chain before deployment; this is not a complete SPF evaluation.", SpfSource);
            }
        }
        if (!spf.Any(r => Name(r) == "@")) Add("SPF-MISSING", DnsFindingSeverity.Warning, "No apex SPF policy found", "If this domain sends mail, use the exact authorized sender information from every mail service. If it sends no mail, the explicit no-mail template can publish a deny-all policy.", SpfSource);
        var dmarc = editable.Where(r => r.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) && Name(r) == "_dmarc" && Txt(r.Value).StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (dmarc.Length == 0) Add("DMARC-MISSING", DnsFindingSeverity.Warning, "No local DMARC policy found", "Check inherited organizational policy first. For a mail-sending domain, begin with monitoring and a working report mailbox, verify SPF/DKIM alignment, then consider enforcement.", DmarcSource);
        if (dmarc.Length > 1) Add("DMARC-DUPLICATE", DnsFindingSeverity.Critical, "Multiple DMARC policies", "Publish a single valid policy at _dmarc. Conflicting policies prevent reliable policy discovery.", DmarcSource);
        foreach (var record in dmarc)
        {
            var tags = Txt(record.Value).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(t => t.Split('=', 2)).Where(p => p.Length == 2).ToArray();
            var policies = tags.Where(t => t[0].Trim().Equals("p", StringComparison.OrdinalIgnoreCase)).Select(t => t[1].Trim()).ToArray();
            if (policies.Length != 1 || !new[] { "none", "quarantine", "reject" }.Contains(policies[0], StringComparer.OrdinalIgnoreCase))
                Add("DMARC-INVALID", DnsFindingSeverity.Critical, "Invalid DMARC policy", "Specify one p=none, p=quarantine, or p=reject policy after reviewing mail behavior.", DmarcSource);
            else if (policies[0].Equals("none", StringComparison.OrdinalIgnoreCase))
                Add("DMARC-MONITORING", DnsFindingSeverity.Information, "DMARC is in monitoring mode", "Monitoring is a useful rollout stage. Review reports and authenticate legitimate mail before changing enforcement.", DmarcSource);
            if (!tags.Any(t => t[0].Trim().Equals("rua", StringComparison.OrdinalIgnoreCase)))
                Add("DMARC-REPORTS", DnsFindingSeverity.Information, "No aggregate reporting address", "Consider a mailbox or reporting service you control. An external reporting destination requires authorization by its operator.", DmarcSource);
        }
        if (!editable.Any(r => Name(r).Contains("._domainkey", StringComparison.OrdinalIgnoreCase) && r.Type.ToUpperInvariant() is "TXT" or "CNAME"))
            Add("DKIM-UNVERIFIED", DnsFindingSeverity.Information, "DKIM setup needs confirmation", "No local selector records were found. Obtain the exact selector and public record from your mail provider; a zone scan cannot verify signing or alignment and cannot discover every selector.", DkimSource);
        var mx = editable.Where(r => r.Type.Equals("MX", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var group in mx.GroupBy(Name))
        {
            if (group.Any(r => r.Value.Trim() == ".") && (group.Count() != 1 || group.First().Priority != 0))
                Add("MX-NULL-CONFLICT", DnsFindingSeverity.Critical, "Null MX conflicts with mail routing", "A no-service MX must be the only MX at that name and use preference 0 with target '.'.", NullMxSource);
            foreach (var record in group.Where(r => r.Value.Trim() != "."))
                if (editable.Any(r => r.Type.Equals("CNAME", StringComparison.OrdinalIgnoreCase) && Name(r) == Relative(record.Value, domain)))
                    Add("MX-ALIAS", DnsFindingSeverity.Warning, "Mail exchanger points to an alias", "Use the mail provider's canonical mail exchanger host, rather than a CNAME alias.", "https://www.rfc-editor.org/rfc/rfc2181.html");
        }
        if (!editable.Any(r => r.Type.Equals("CAA", StringComparison.OrdinalIgnoreCase)))
            Add("CAA-REVIEW", DnsFindingSeverity.Information, "Review certificate authority authorization", "CAA is optional and can be inherited. Before adding restrictions, inventory the certificate authorities used by your website, CDN, and renewal services.", CaaSource);
        foreach (var group in editable.GroupBy(r => Name(r) + "|" + r.Type.ToUpperInvariant()).Where(g => g.Select(r => r.TtlSeconds).Distinct().Count() > 1))
            Add("TTL-MIXED", DnsFindingSeverity.Warning, $"Mixed TTLs in {group.Key}", "Values in the same record set should use one TTL. Choose a provider-supported value; changing TTL does not expire already-cached answers.", "https://www.rfc-editor.org/rfc/rfc2181.html");
        if (records.Any(r => r.IsReadOnly)) Add("SCOPE-PROTECTED", DnsFindingSeverity.Information, "Some records require the provider console", "Provider-managed and unsupported records are visible but cannot be corrected by this scanner. DNSSEC, registrar delegation, inheritance, and live mail delivery are not verified by this local scan.", DnsSource);
        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Rule, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<DnsRecord> Create(DnsSetupTemplate template, string domain, string name, string value, int ttl, int priority = 10, bool noMailConfirmed = false)
    {
        name = string.IsNullOrWhiteSpace(name) ? "@" : Relative(name.Trim(), domain);
        value = value.Trim();
        DnsRecord Record(string host, string type, string data, int? pref = null) => new() { Name = host, Type = type, Value = data, TtlSeconds = ttl, Priority = pref };
        if (ttl <= 0) throw new InvalidOperationException("Enter a positive TTL supported by this provider.");
        IReadOnlyList<DnsRecord> result = template switch
        {
            DnsSetupTemplate.WebsiteAddress when IPAddress.TryParse(value, out var ip) => [Record(name, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "A" : "AAAA", ip.ToString())],
            DnsSetupTemplate.WebsiteAlias when name != "@" && Host(value) => [Record(name, "CNAME", value.TrimEnd('.') + ".")],
            DnsSetupTemplate.MailExchanger when Host(value) && priority is >= 0 and <= 65535 => [Record(name, "MX", value.TrimEnd('.') + ".", priority)],
            DnsSetupTemplate.Spf when value.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase) && !value.Contains('\n') => [Record(name, "TXT", value)],
            DnsSetupTemplate.DmarcMonitoring when ValidMailbox(value) => [Record("_dmarc", "TXT", $"v=DMARC1; p=none; rua=mailto:{value}")],
            DnsSetupTemplate.Dkim when name.EndsWith("._domainkey", StringComparison.OrdinalIgnoreCase) && value.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase) => [Record(name, "TXT", value)],
            DnsSetupTemplate.Dkim when name.EndsWith("._domainkey", StringComparison.OrdinalIgnoreCase) && Host(value) => [Record(name, "CNAME", value.TrimEnd('.') + ".")],
            DnsSetupTemplate.Caa when Host(value) => [Record(name, "CAA", $"0 issue \"{value.TrimEnd('.')}\"")],
            DnsSetupTemplate.NoMail when noMailConfirmed => [Record("@", "MX", ".", 0), Record("@", "TXT", "v=spf1 -all"), Record("_dmarc", "TXT", "v=DMARC1; p=reject")],
            _ => throw new InvalidOperationException("Complete the template with valid values from your service provider. The no-mail template requires explicit confirmation.")
        };
        var validation = ZoneValidator.Validate(result);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        return result;
    }

    public static IReadOnlyList<DnsRecord> Stage(IReadOnlyList<DnsRecord> current, IReadOnlyList<DnsRecord> additions)
    {
        var result = current.Select(r => r.Clone()).ToList();
        foreach (var record in additions)
        {
            if (result.Any(r => DnsRecord.ContentEquals(r, record))) continue;
            bool sameName(DnsRecord r) => r.Name.TrimEnd('.').Equals(record.Name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
            if (result.Any(r => sameName(r) && r.IsReadOnly && r.Type.Equals(record.Type, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This name and type are managed by the provider.");
            if (result.Any(r => sameName(r) && r.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase) && record.Type == "TXT" &&
                ((Txt(record.Value).StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) && Txt(r.Value).StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)) ||
                 (Txt(record.Value).StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) && Txt(r.Value).StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase)))))
                throw new InvalidOperationException("A policy already exists at this name. Edit that policy instead of adding a duplicate.");
            if (record.Type == "MX" && result.Any(r => sameName(r) && r.Type == "MX" && (r.Value == "." || record.Value == ".")))
                throw new InvalidOperationException("The proposed MX conflicts with existing mail routing. Review and remove conflicting records explicitly.");
            result.Add(record.Clone());
        }
        var validation = ZoneValidator.Validate(result);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        return result;
    }

    private static string Txt(string value) => DnsRecordSemantics.DecodeText(value).Trim();
    private static string Relative(string name, string domain)
    {
        name = name.TrimEnd('.').ToLowerInvariant(); domain = domain.TrimEnd('.').ToLowerInvariant();
        if (name == domain || name == "@") return "@";
        return name.EndsWith("." + domain, StringComparison.Ordinal) ? name[..^(domain.Length + 1)] : name;
    }
    private static bool Host(string value) => value.TrimEnd('.').Length is > 0 and <= 253 && !IPAddress.TryParse(value, out _) &&
        value.TrimEnd('.').Split('.').All(label => label.Length is > 0 and <= 63 && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && label[0] != '-' && label[^1] != '-');
    private static bool ValidMailbox(string value) => value.Count(c => c == '@') == 1 && value.Split('@')[0].Length > 0 &&
        !value.Any(c => char.IsWhiteSpace(c) || c is ';' or ',' or '"' or '\r' or '\n') && Host(value.Split('@')[1]);
}
