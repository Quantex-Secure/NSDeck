# Best-practice scanner and guided setup

Open **Action → Best Practices & Guided Setup**, or use **Best Practices** on the toolbar. The scanner reads the selected zone and its pending edits locally; it sends no public DNS queries. Read-only accounts can scan and preview templates.

Findings explain the problem, severity, next step, and an authoritative source. Select a finding and choose **Set up selected finding** to open the relevant template. Enter the values supplied by your hosting, email, or certificate service, select **Preview records**, then **Stage reviewed records**. Nothing is published until you review and apply the main window's pending changes.

## What the scanner checks

- Invalid records and CNAME conflicts.
- Missing local SPF/DMARC policies, duplicate policies, and SPF allow-all mechanisms.
- SPF policies containing more than ten local lookup-causing terms. Nested includes and redirects require separate evaluation.
- Invalid DMARC policy values and opportunities to configure aggregate reporting.
- Whether local DKIM selector records are present; this does not establish that messages are signed or aligned.
- Conflicting null MX records and mail exchangers pointing at a local CNAME.
- Mixed TTLs in a record set and whether local CAA records are present.
- Protected records that need their provider's console.

This is configuration guidance, not a full DNS, deliverability, or security audit. The scan does not evaluate inherited policies, authoritative delegation, DNSSEC, recursive SPF evaluation, service availability, or mail delivery. Missing local records can be intentional. There is no universal “best TTL”: consider caching, provider limits, and planned changes.

## Templates

| Template | Required information | Proposed records |
| --- | --- | --- |
| Website address | Host and hosting-service IPv4/IPv6 address | A or AAAA |
| Website alias | Non-apex host and canonical target | CNAME |
| Mail exchanger | Provider MX host and preference | MX |
| Authorized email senders | Complete policy covering all mail senders | SPF TXT |
| Email monitoring | Working aggregate-report mailbox | DMARC TXT with `p=none` |
| Email signing | Provider-issued selector and public record | DKIM TXT or CNAME |
| Approved certificate authority | CA domain approved for all relevant issuance/renewals | CAA `issue` |
| Domain with no email | Explicit confirmation that the domain neither sends nor receives mail | Null MX, SPF `-all`, DMARC `p=reject` |

Existing SPF and DMARC policies are never silently replaced or duplicated. The no-mail template refuses conflicting MX records. Review and edit existing policies deliberately. DKIM private keys are never generated or requested. External DMARC report destinations need authorization from their operator. CAA restrictions can prevent certificate issuance if an active CA is omitted.

## Sources

- [SPF — RFC 7208](https://www.rfc-editor.org/rfc/rfc7208.html)
- [DMARC — RFC 9989, published May 2026](https://www.rfc-editor.org/rfc/rfc9989.html)
- [DKIM — RFC 6376](https://www.rfc-editor.org/rfc/rfc6376.html)
- [Null MX — RFC 7505](https://www.rfc-editor.org/rfc/rfc7505.html)
- [CAA — RFC 8659](https://www.rfc-editor.org/rfc/rfc8659.html)
- [DNS clarifications — RFC 2181](https://www.rfc-editor.org/rfc/rfc2181.html)
