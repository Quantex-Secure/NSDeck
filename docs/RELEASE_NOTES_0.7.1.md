# NSDeck 0.7.1 preview — profile switching

Fixes the 0.7.0 preview bug where **Save and connect** ignored the selected profile, loaded every profile, and opened the first available domain.

## Switching profiles

Use the dropdown immediately beside **Accounts**. Selecting a profile connects only its providers and remembers that selection after restart. The same behavior applies to **Save and connect** in **File → DNS Provider Accounts**. Saved profiles and their credentials remain intact; staged drafts stay scoped to their original account and zone.

For a new profile, use **Configure providers** to enable connections. An empty profile now shows setup guidance without loading Default or demo records. Change Lab operates within the active profile.

## Downloads

- `NSDeck-Setup-0.7.1.exe`: Windows x64 per-user installer.
- `NSDeck-0.7.1-win-x64.zip`: portable executable and license notices.
- `NSDeck-0.7.1.exe`: standalone executable.
- `NSDeck-0.7.1-SHA256.txt`: checksums for all three packages.

This preview is **unsigned**, as was 0.7.0. The .NET runtime is included. Close NSDeck before updating. Verify the download against the published checksum file:

```powershell
Get-FileHash .\NSDeck-Setup-0.7.1.exe -Algorithm SHA256
```

Validation includes automated profile-selection, persistence, empty-profile, and real WPF dropdown/dialog interaction checks, alongside the existing core/provider tests and JEA integration check. Tests use reserved example data and do not access production DNS.
