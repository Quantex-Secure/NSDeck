## Summary

Describe what changed and why.

## DNS safety impact

- Provider(s) affected:
- Read/write scope:
- Concurrency, snapshot, verification, and rollback behavior:

## Verification

- [ ] `scripts\Test-PublicRelease.ps1` passes.
- [ ] `scripts\Test-JeaModule.ps1` passes in Windows PowerShell 5.1 when JEA code changed.
- [ ] `dotnet test .\NSDeck.slnx --configuration Release` passes.
- [ ] UI behavior was manually checked when applicable.
- [ ] Documentation was updated.
- [ ] No credentials, real domains, internal hostnames, user identities, or private diagnostics are included.
