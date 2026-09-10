<p align="center">
  <img src="assets/nsdeck-icon.png" width="112" alt="NSDeck icon">
</p>

# NSDeck

**Every zone. One deck.**

**A Quantex Secure product. Free and open source under Apache 2.0.**

**[Download the Windows preview](https://github.com/Quantex-Secure/NSDeck/releases)** · [Scanner and guided setup](docs/BEST_PRACTICES.md) · [0.7 release notes](docs/RELEASE_NOTES_0.7.md)

Choose the per-user installer or portable ZIP. Windows 11 x64 is the tested desktop platform; the .NET runtime is included. Preview binaries are unsigned. See the release notes for checksum verification.

[![License: Apache 2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D4)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)

NSDeck is a .NET 10 WPF administration console for managing public and private DNS across multiple providers. Its provider-and-zone tree follows the familiar workflow of Microsoft DNS Manager, while its guarded apply process protects against stale edits and keeps a pre-change snapshot.

![NSDeck application preview](design/nsdeck-render.png)

## Supported providers

- Namecheap BasicDNS and PremiumDNS
- Microsoft Azure DNS
- GoDaddy DNS
- Cloudflare DNS
- AWS Route 53
- Google Cloud DNS
- Microsoft Windows DNS Server through a constrained JEA endpoint

PowerDNS is intentionally not included yet.

## Current features

- Organizes connections into named customer/environment profiles, including read-only accounts.
- Identifies zones by account and provider zone ID, including same-name public/private Route 53 zones.
- Scans loaded records for DNS and email-authentication issues, explains findings with source links, and guides record setup.
- Previews and stages website, MX, SPF, DMARC monitoring, provider-issued DKIM, CAA, and explicitly confirmed no-mail records.
- Saves encrypted drafts as you edit and restores them for the matching account and zone.
- Loads zones from every enabled provider into one account tree.
- Shows only Namecheap domains whose zones are hosted by Namecheap BasicDNS or PremiumDNS; externally delegated registrations remain hidden from the Namecheap node.
- Provides an in-app setup checklist and official documentation link on every provider tab.
- Reads and edits common A, AAAA, CAA, CNAME, MX, NS delegation, PTR, SRV, and TXT records.
- Connects to Windows DNS without storing Domain Admin credentials; the current Windows identity is limited by a server-side JEA role and every remote command is transcribed.
- Filters records by text and record type.
- Supports Ctrl-click and Shift-click multi-selection so several records can be staged for deletion together.
- Stages add, edit, and delete operations locally.
- Searches records globally across every configured provider and zone in the DNS Change Lab.
- Builds coordinated multi-zone find-and-replace plans with dependency and shared-value analysis.
- Snapshots affected zones, applies and verifies each provider, and inspects attempted writes after failures. Recovery preserves unrelated changes and stops for conflicting or ambiguous record sets.
- Reviews dangerous MX, apex, SPF, DKIM, DMARC, CAA, NS, and bulk-deletion changes before applying.
- Re-reads the provider before applying and stops if something else changed the zone.
- Saves a DPAPI-protected provider configuration and a local pre-change zone snapshot.
- Applies provider-appropriate changes and then retries verification for up to 30 seconds while provider control planes settle.
- Uses Azure DNS record-set ETags to block last-moment concurrent overwrites.
- Checks expected records through Cloudflare and Google public resolvers with an auto-refreshing propagation radar.
- Writes credential-free local JSON audit logs and exports public-safe diagnostic ZIP reports with zone names, Windows server names, fingerprints, and provider details anonymized.
- Supports HTTPS update checks and user-confirmed downloads with SHA-256 verification. Starting a downloaded update requires a valid, pinned Authenticode publisher signature; silent installation is disabled.
- Exports, imports, and stages JSON zone snapshots.
- Uses safe sample data when no provider is enabled.

Provider-managed and unsupported records are displayed read-only where returned by the provider. Route 53 aliases and policy records, Google routing-policy records, and Azure aliases remain managed through their provider consoles.

## Running

Download a Windows preview package from the repository's [Releases page](https://github.com/Quantex-Secure/NSDeck/releases). Verify the accompanying SHA-256 checksum before running it. Until Quantex Secure publishes Authenticode-signed builds, Windows may display an unknown-publisher warning.

To run from source:

```powershell
dotnet run --project .\src\NSDeck.Desktop\NSDeck.Desktop.csproj
```

The solution targets `net10.0-windows` and builds with the .NET 10 SDK.

## Connecting providers

Open **File → DNS Provider Accounts**, add or select a named profile, choose its read-only/editing mode, and click **Configure providers**. Enable any combination of providers within the profile.

- **Namecheap:** API user, username, API key, and whitelisted public IPv4 address.
- **Azure DNS:** subscription ID. Use an existing Azure CLI, Visual Studio, or environment sign-in, or provide a tenant ID, application ID, and client secret.
- **GoDaddy:** Personal Access Token with domain read and DNS update scopes.
- **Cloudflare:** scoped API token with Zone Read, DNS Read, and DNS Edit.
- **Route 53:** IAM access key and secret with hosted-zone list/read/change permissions. Temporary session tokens are supported.
- **Google Cloud DNS:** project ID plus a service-account JSON file, or Application Default Credentials.
- **Windows DNS:** one or more DNS server names and the default `NSDeck.Dns` JEA endpoint. The Windows DNS tab exports the one-time server setup script and tests the constrained connection using the current Windows account.

All profiles and provider configuration—including identifiers and secrets—are serialized into one payload encrypted with Windows DPAPI. It can be decrypted only by the same Windows account on the same computer.

When NSDeck starts for the first time, it copies existing settings, snapshots, and audit logs from the former `%LOCALAPPDATA%\DomainDnsManager` folder into `%LOCALAPPDATA%\NSDeck`. The original folder is retained as a rollback copy. A saved legacy Windows DNS endpoint name is upgraded to `NSDeck.Dns` in memory.

## Safe update behavior

Every save follows the same guarded workflow:

1. Read and fingerprint the original editable records.
2. Stage changes locally.
3. Validate the intended records.
4. Re-read the provider and stop if the zone changed.
5. Save a pre-change snapshot.
6. Apply the provider-specific change set.
7. Re-read and verify the resulting fingerprint, retrying for up to 30 seconds before warning that the update may still be propagating.

Namecheap and GoDaddy use complete editable-record replacement. Azure, Cloudflare, Route 53, and Google Cloud DNS use record-set or record-level changes. Provider-managed and advanced routing records are not deleted by this process.

Windows DNS reconciles A, AAAA, CNAME, MX, delegation NS, PTR, SRV, and TXT records through three purpose-built JEA functions. The updated endpoint displays SOA, apex NS, DNSSEC, CAA, and unsupported record types read-only. Reinstall the bundled JEA setup script to enable that expanded display.

## Windows DNS least-privilege setup

The desktop application never needs to run as a Domain Admin. In **Settings → Windows DNS**, save `Install-NSDeckJea.ps1`, copy it to each DNS server, and run it once from an elevated Windows PowerShell 5.1 session using a privileged setup identity:

```powershell
.\Install-NSDeckJea.ps1 -OperatorGroup 'CONTOSO\NSDeck-DnsOperators'
```

When run without `-OperatorGroup`, the script now prompts for the group and offers `USERDOMAIN\NSDeck-DnsOperators` as the default when the current account is signed into a domain.

Windows DNS servers are treated as internal by default, so NSDeck does not offer public-resolver propagation checks for their zones. Enable **These Windows DNS servers host public authoritative zones** only when the configured servers genuinely publish those zones to the internet.

Create the operator group and add the intended users before running the script. The script installs a restricted endpoint named `NSDeck.Dns`, exposes only the application's zone-list, record-read, and record-reconciliation functions, uses a temporary virtual account for those functions, and writes JEA transcripts beneath `%ProgramData%\NSDeck\JEA-Transcripts`. Sign out and back in after changing group membership.

The installer removes the pre-release `DomainDnsManager.Dns` endpoint and module when found. Use `-KeepLegacyEndpoint` only when an older application build must remain operational during a staged migration.

To remove the endpoint and its module later:

```powershell
.\Install-NSDeckJea.ps1 -Remove
```

Namecheap's harmless one-second TTL drift and its placeholder priority on non-MX records are normalized before verification so they do not create false mismatch warnings.

## DNS Change Lab

Open **Action → DNS Change Lab** to build one guarded change across multiple providers and domains:

1. Refresh the global inventory.
2. Search by provider, domain, record name, type, IP address, or target.
3. Select all matching records and review the blast-radius tree.
4. Enter the text to find and its replacement, then add the selected records to the coordinated plan.
5. Review the complete before/after table and apply it.

The Change Lab re-reads every affected zone before writing anything. If preflight checks pass, it saves every snapshot, applies and verifies zones sequentially, and inspects attempted writes in reverse order after a failure. It restores touched record sets only when their current state can be matched safely to the plan; unrelated administrator changes survive. Ambiguous partial writes and conflicting edits require manual review. Providers without native conditional writes retain a final read/write race window. These coordinated changes are not distributed ACID transactions. After a successful transaction, the propagation radar opens with every changed record.

Public resolver results are informational. Cloudflare or Google can continue returning a cached prior value until that value's old TTL expires even though the authoritative provider has already verified the update.

## Drafts and recovery

Edits are saved as DPAPI-encrypted drafts beneath `%LOCALAPPDATA%\NSDeck\drafts`. Matching drafts restore when you reopen their zone. If the zone changed, **Action → Restore Saved Draft** stages the saved desired state for explicit review. **Clear All** discards the current draft.

New snapshot history is scoped by profile, provider account, and zone ID. Legacy unscoped snapshots remain on disk and can be imported explicitly. Snapshots from another identified account/zone are rejected by normal restore.

Use **Cancel operation** for long reads or coordinated operations. During an apply, cancellation can occur after a provider accepted a write; wait for recovery results before closing or retrying.

## Tests

```powershell
dotnet test .\NSDeck.slnx
```

The automated tests do not contact live DNS accounts. Live verification requires credentials supplied by the account owner.

The GitHub Actions workflow runs the public-source scan, Windows PowerShell 5.1 JEA compatibility test, Release build, and automated test suite on every pull request.

## Release packaging

`build-release.ps1` runs the tests and produces a versioned executable, ZIP archive, and SHA-256 checksum. If Inno Setup is installed, it also builds the per-user Windows installer. Authenticode signing is supported when a certificate thumbprint is supplied; see `docs\RELEASE.md`.

Before publishing the repository or a GitHub release, follow [the public release checklist](docs/PUBLIC_RELEASE.md). The release build runs a generic secret and local-path scan. Organization-specific blocked terms can be supplied through `-BlockedTerms` or an ignored local `.public-release-blocked-terms.txt` file.

## Security and support

Do not report vulnerabilities or expose production DNS data in public issues. Follow [SECURITY.md](SECURITY.md) for private vulnerability reporting and [SUPPORT.md](SUPPORT.md) for safe diagnostic guidance. NSDeck can make authoritative DNS changes; review staged changes and retain independent recovery access to every provider.

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Tests and examples must use reserved domains, addresses, and sanitized provider data.

Release history is maintained in [CHANGELOG.md](CHANGELOG.md). Maintainers preparing releases should follow [docs/RELEASE.md](docs/RELEASE.md).

## License

Copyright © 2026 Quantex Secure. NSDeck is licensed under [Apache License 2.0](LICENSE). Third-party components remain under their respective licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Current limitations

- The executable is unsigned, so Windows may show an unrecognized-publisher warning.
- Live provider calls are not part of the automated build because credentials are deliberately excluded.
- Cloudflare proxy state is preserved when existing records are patched; newly created records default to DNS-only.
- Advanced Route 53 aliases/routing policies and Google routing-policy records remain untouched and require their provider consoles.
- DNSSEC configuration and registrar nameserver changes are outside the record editor.
- Silent installation is disabled. The updater verifies hashes, and only offers to start a download if its signature matches the independently configured trusted publisher certificate.

## Project layout

```text
src/NSDeck.Core                 DNS models, validation, comparison, snapshots
src/NSDeck.Providers.Namecheap Namecheap API provider
src/NSDeck.Providers.Cloud     Azure, GoDaddy, Cloudflare, Route 53, Google
src/NSDeck.Providers.Windows   Windows DNS through PowerShell JEA
src/NSDeck.Desktop             .NET 10 WPF application
tests/NSDeck.Tests             Automated safety and provider tests
design/                        Sanitized interface preview
.github/                       CI and community health files
```
