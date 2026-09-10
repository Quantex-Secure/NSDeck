# NSDeck 0.7.0 preview — DNS safety and guided setup

This free, Apache-2.0-licensed Windows preview adds a best-practice scanner and guided record setup, strengthens failure recovery, and introduces named account profiles.

## Downloads

- `NSDeck-Setup-0.7.0.exe`: per-user installer with shortcuts and uninstall support.
- `NSDeck-0.7.0-win-x64.zip`: portable executable, JEA script, and license notices.
- `NSDeck-0.7.0.exe`: standalone executable.
- `NSDeck-0.7.0-SHA256.txt`: checksums for the executable, ZIP, and installer.

Windows x64; Windows 11 is the tested desktop platform. The .NET runtime is included. Windows DNS access additionally requires Windows PowerShell 5.1 and an installed JEA endpoint.

This preview is **unsigned**. Windows may show an unknown-publisher warning. Compare the SHA-256 hash with the release's checksum file before running a downloaded file:

```powershell
Get-FileHash .\NSDeck-Setup-0.7.0.exe -Algorithm SHA256
```

## Changes

- Scanner findings and guided A/AAAA, CNAME, MX, SPF, DMARC, DKIM, CAA, and explicit no-mail templates. All suggestions are previewed and staged before normal guarded apply.
- Attempted writes are inspected after failures, including responses lost after mutation. Recovery preserves unrelated edits and stops for conflicting or ambiguous record-set state.
- Cloudflare snapshot restores remap obsolete record IDs and preserve available proxy/comment/tag/settings metadata.
- Azure validates complete payloads before deletions and compares the intended baseline at its final read.
- Stable provider zone IDs distinguish same-name hosted zones. Private zones never enter public propagation checks.
- Named account profiles, write-blocking read-only access, account/zone-scoped snapshot history, and encrypted persistent drafts.
- Failed zone selection retains the previous target. Busy-state guards and cancellation prevent overlapping desktop operations.
- Custom TTLs survive editing. Protected and unsupported records are visible as read-only where returned by the provider.
- TXT formatting handles quoted/chunked provider records and long DKIM values; verification recognizes equivalent DNS target formatting.
- Update downloads require a valid SHA-256 match. Starting an update additionally requires a valid Authenticode signature matching an independently configured publisher certificate. Silent installation is disabled.
- Expanded offline core/provider tests and Windows desktop interaction tests.

## Upgrade and recovery notes

Existing credentials appear in the **Default** profile. Settings and drafts remain encrypted for the current Windows account. Old unscoped snapshots are retained on disk; import a chosen legacy snapshot explicitly. New history never mixes accounts or same-name provider zones.

Reinstall the bundled JEA setup script on Windows DNS servers to display protected records. Older endpoints remain usable with their existing editable projection.

Snapshots and saved drafts support recovery; coordinated DNS changes are not distributed ACID transactions. Providers without conditional-write support retain a small race window between reading and writing. A timeout can represent an uncertain outcome. Conflicts require inspection rather than a blind overwrite.

The automated suite uses reserved example data and mocks; live credentials and production zones are excluded. Use independent provider access and verify provider-specific behavior on a disposable zone before production rollout.
