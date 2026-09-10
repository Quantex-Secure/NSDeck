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

The view model owns the active profile's configured provider instances, loads their zones independently, and selects the correct provider when a domain is opened. A failure in one account does not prevent successfully loaded providers from appearing.

## Consistency model

The application performs an optimistic-concurrency check using a canonical SHA-256 fingerprint. Record order and provider record IDs are excluded. A pre-change snapshot is saved only after the second read confirms that the provider has not changed since the zone was opened.

Azure compares the expected baseline at its final provider read, materializes every payload before deleting records, and then uses native record-set ETags with `If-Match` or `If-None-Match`. Other providers retain their documented conditional-write limitations; multi-provider changes are compensating operations rather than distributed ACID transactions.

Namecheap and GoDaddy expose complete-record replacement operations. Azure and Google operate on record sets, Route 53 applies a transactional change batch, and Cloudflare uses record-level create, patch, and delete calls. Provider-managed and unsupported records have explicit read-only markers. They remain visible but cannot be altered by a normal plan. Provider-maintained SOA serials and DNSSEC signatures are excluded from normal concurrency/verification fingerprints; protected-record presence is still checked.

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

Each configured Windows DNS connection owns a hidden Windows PowerShell 5.1 worker. That worker caches one authenticated JEA session per server and endpoint, so normal zone navigation reuses the Kerberos/WinRM connection instead of starting PowerShell and negotiating a new session for every click. A failed or cancelled request discards the affected session so the next request reconnects cleanly; closing NSDeck removes all cached sessions and stops the worker.

## DNS Change Lab

The Change Lab reads an editable inventory from every configured provider in the active profile. A bulk replacement plan retains its source-zone fingerprint and is preflighted again before any writes occur. All affected zones are snapshotted before the transaction begins. Writes are marked attempted before invoking the provider and verified sequentially. After a failure, attempted zones are re-read in reverse order. Recovery restores changed record sets only when their state matches the original or intended plan, preserves unrelated changes, and stops on ambiguous/conflicting sets. Recovery writes repeat the baseline check and use a bounded cancellation deadline.

Dependency analysis recognizes CNAME, MX, NS, PTR, SRV, and SPF include/redirect relationships, plus records that share the same value. Public propagation checks are deliberately informational: Cloudflare and Google recursive resolver caches may lag a successfully verified authoritative-provider update until the prior TTL expires.

## Diagnostics

Append-only JSON audit logs are written beneath `%LOCALAPPDATA%\NSDeck\logs`. They contain operation metadata and fingerprints, not provider credentials. Those local logs can contain zone and Windows DNS server names. The shareable diagnostic ZIP rewrites them with per-export aliases and removes fingerprints and provider error details before packaging them with a sanitized environment summary.

## Target identity, profiles, and drafts

`AppSettings.ActiveProfileId` persists the profile selected in the Accounts dropdown or the account dialog. An empty active profile loads no provider and no demo records. Older profile lists without a saved selection initially use their first profile.

`AccountDnsProvider` enforces read-only access and carries the profile plus provider-account identity. `ZoneTargetProvider` binds a selected `DomainSummary` to its stable provider zone ID and public/private visibility. Azure, Route 53, Cloudflare, and Google expose ID-aware read/write overloads. Ambiguous legacy name-only lookup fails instead of picking an arbitrary zone. Snapshot directories hash the account/zone/domain tuple; account profile names are anonymized in diagnostic exports.

Zone selection loads a candidate before committing the provider and displayed records. `DraftStore` persists the expected baseline fingerprint and desired records under DPAPI encryption. Matching drafts restore automatically; changed baselines require explicit review.

## Scanner and record templates

`DnsBestPracticeScanner` contains deterministic local checks and guarded template construction. It never uses DNS network queries, infers signing keys, replaces existing email policies, or publishes records. The desktop previews candidates, validates them against the selected provider, and stages them for the ordinary apply workflow. See `BEST_PRACTICES.md` for scope and references.

## Verified update downloads

`UpdateService` follows bounded HTTPS-only redirects, limits manifest/download size, and hashes streamed downloads before moving a temporary file into place. Hash failures remove partial files. The desktop checks the Authenticode trust status and independently configured certificate thumbprint before offering execution; it rechecks the hash under a non-writable file handle immediately before launch. Unsigned preview files can be downloaded but cannot be launched through this update flow.
