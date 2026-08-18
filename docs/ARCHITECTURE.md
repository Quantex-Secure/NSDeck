# Architecture

NSDeck is developed and distributed as a Quantex Secure product.

## Design direction

The application uses a restrained Windows administration-console design: white surfaces, cool-gray separators, Segoe UI typography, a compact command bar, a persistent provider-and-zone tree, and dense record tables.

## Provider boundary

`IDnsProvider` isolates the WPF interface and guarded apply workflow from provider APIs.

```text
WPF desktop interface
        |
MainViewModel, Change Lab, resolver radar, and guarded apply workflows
        |
IDnsProvider
        +-- NamecheapDnsProvider
        +-- AzureDnsProvider
        +-- GoDaddyDnsProvider
        +-- CloudflareDnsProvider
        +-- Route53DnsProvider
        +-- GoogleCloudDnsProvider
        +-- WindowsDnsProvider
```

The view model owns all configured provider instances, loads their zones independently, and selects the correct provider when a domain is opened. A failure in one account does not prevent successfully loaded providers from appearing.

## Consistency model

The application performs an optimistic-concurrency check using a canonical SHA-256 fingerprint. Record order and provider record IDs are excluded. A pre-change snapshot is saved only after the second read confirms that the provider has not changed since the zone was opened.

Azure record-set updates additionally use native ETags with `If-Match` or `If-None-Match`. This closes the final concurrency window between the preflight read and provider write.

Namecheap and GoDaddy expose complete-record replacement operations. Azure and Google operate on record sets, Route 53 applies a transactional change batch, and Cloudflare uses record-level create, patch, and delete calls. Provider-managed apex SOA/NS and advanced policy records are excluded from the editable projection, so ordinary updates do not overwrite them.

## Authentication and secret storage

- Namecheap uses its API key and IPv4 allowlist.
- Azure uses `DefaultAzureCredential` or a service principal.
- GoDaddy and Cloudflare use scoped bearer tokens.
- Route 53 uses AWS access credentials, including optional temporary session tokens.
- Google Cloud uses a service-account JSON file or Application Default Credentials.
- Windows DNS uses the interactive user's Kerberos identity to enter a named JEA endpoint. The application stores server and endpoint names only; it never receives or persists the Domain Admin credential used for one-time endpoint installation.

The complete settings document is stored under `%LOCALAPPDATA%\NSDeck\settings.json` as a Windows DPAPI-protected payload. Snapshots are stored beneath `%LOCALAPPDATA%\NSDeck\snapshots` and never contain provider credentials.

On first launch, NSDeck copies missing files from the former `%LOCALAPPDATA%\DomainDnsManager` location. Copying instead of moving leaves a rollback source, while DPAPI remains valid because the Windows user and machine are unchanged.

For Windows PowerShell 5.1 JEA discovery, the Windows DNS setup installs `NSDeck.Jea.psd1`, `NSDeck.Jea.psm1`, and the `RoleCapabilities` directory directly under `%ProgramFiles%\WindowsPowerShell\Modules\NSDeck.Jea`. The installer validates the manifest, module search path, and generated session configuration before registering the endpoint. The session preloads both `NSDeck.Jea` and the Windows `DnsServer` module so the restricted endpoint does not need to expose the FileSystem provider for module auto-loading.

The Windows DNS endpoint exposes only `Get-NSDeckDnsZone`, `Get-NSDeckDnsRecord`, and `Set-NSDeckDnsZoneRecords`. Those functions project and reconcile A, AAAA, CNAME, MX, NS, PTR, SRV, and TXT records; all other Windows DNS record types are deliberately left untouched. JEA uses a temporary virtual account, restricts the caller to the named functions, and writes server-side transcripts for accountability.

The desktop keeps one hidden Windows PowerShell 5.1 worker for all configured Windows DNS servers. That worker caches one authenticated JEA session per server and endpoint, so normal zone navigation reuses the Kerberos/WinRM connection instead of starting PowerShell and negotiating a new session for every click. A failed or cancelled request discards the affected session so the next request reconnects cleanly; closing NSDeck removes all cached sessions and stops the worker.

## DNS Change Lab

The Change Lab reads an editable inventory from every configured provider. A bulk replacement plan retains its source-zone fingerprint and is preflighted again before any writes occur. All affected zones are snapshotted before the transaction begins. Writes are applied and verified sequentially; if a later write fails, already-written zones are restored in reverse order and verified again.

Dependency analysis recognizes CNAME, MX, NS, PTR, SRV, and SPF include/redirect relationships, plus records that share the same value. Public propagation checks are deliberately informational: Cloudflare and Google recursive resolver caches may lag a successfully verified authoritative-provider update until the prior TTL expires.

## Diagnostics

Append-only JSON audit logs are written beneath `%LOCALAPPDATA%\NSDeck\logs`. They contain operation metadata and fingerprints, not provider credentials. Those local logs can contain zone and Windows DNS server names. The shareable diagnostic ZIP rewrites them with per-export aliases and removes fingerprints and provider error details before packaging them with a sanitized environment summary.
