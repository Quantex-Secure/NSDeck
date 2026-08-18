# Contributing to NSDeck

Thank you for helping improve NSDeck. By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md) and license your contribution under the [Apache License 2.0](LICENSE).

## Before opening an issue

- Search existing issues first.
- Use GitHub private vulnerability reporting for security problems.
- Remove provider credentials, tenant and subscription IDs, real domains, DNS server names, usernames, and internal IP addresses.
- Prefer the reserved examples `example.com`, `example.net`, `example.org`, `192.0.2.0/24`, `198.51.100.0/24`, and `203.0.113.0/24`.
- Attach only NSDeck's **Shareable Diagnostic Report**, never raw settings, logs, snapshots, or JEA transcripts.

## Development setup

Requirements:

- Windows 10 or later
- .NET 10 SDK
- Windows PowerShell 5.1 for the JEA compatibility test

```powershell
dotnet restore .\NSDeck.slnx
dotnet test .\NSDeck.slnx --configuration Release
dotnet run --project .\src\NSDeck.Desktop\NSDeck.Desktop.csproj
```

The automated tests use fakes and reserved example data. Do not add live provider credentials or organization-specific infrastructure to tests.

## Pull requests

1. Keep changes focused and explain their DNS safety impact.
2. Add or update tests for provider parsing, validation, diffing, or failure behavior.
3. Preserve optimistic-concurrency checks, pre-change snapshots, guarded apply, and post-write verification.
4. Run `scripts\Test-PublicRelease.ps1` and the full Release test suite.
5. Update documentation when settings, permissions, provider behavior, or release packaging changes.

Provider additions should keep credentials behind provider-specific options, project only editable records, preserve provider-managed records, implement cancellation, and return actionable errors without leaking secrets.

## Licensing

Unless you explicitly state otherwise, contributions intentionally submitted for inclusion in NSDeck are provided under Apache License 2.0, as described in the project license.
