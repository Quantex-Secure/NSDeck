# Public release checklist

Use this checklist before making the repository public or uploading a GitHub release.

The first-publication sequence and recommended repository metadata are in [GITHUB_SETUP.md](GITHUB_SETUP.md).

1. Keep `.gitignore` in place before the first commit. Do not force-add `release`, `artifacts`, `work`, `bin`, `obj`, settings, credential JSON files, signing keys, diagnostic ZIPs, or PDB files.
2. Use only reserved examples such as `example.com`, `example.invalid`, `192.0.2.0/24`, `198.51.100.0/24`, and `203.0.113.0/24` in source, documentation, tests, and screenshots.
3. Run the public-release scan with organization-specific terms supplied locally:

   ```powershell
   .\scripts\Test-PublicRelease.ps1 -BlockedTerms @('your-company', 'your-domain.example', 'your-username', 'your-server-prefix')
   ```

   For repeatable local checks, place one term per line in `.public-release-blocked-terms.txt`. That file is ignored by Git and must never be committed because the terms themselves may disclose private infrastructure.

4. Run `dotnet test .\NSDeck.slnx --configuration Release`.
5. Build a clean release with `build-release.ps1`. The build also runs the generic public-release scan.
6. Upload only the current versioned executable or ZIP, checksum, and optional signed installer. Do not upload the entire local `release` directory because it may contain older private builds.
7. Open the ZIP and verify its contents before uploading it.
8. Generate screenshots with demo data only. Never publish screenshots of locally configured provider tabs.
9. Export only **Shareable Diagnostic Report** packages for public issues. Never attach `%LOCALAPPDATA%\NSDeck`, raw audit logs, settings, or snapshots.
10. If sensitive material was ever committed, remove it from Git history and rotate the exposed credential before making the repository public.

## Repository settings

Before changing visibility to public:

1. Confirm the repository owner is the intended Quantex Secure organization or account.
2. Add a concise description, topics, and the sanitized `design/nsdeck-render.png` social preview.
3. Enable branch protection for `main`, requiring the CI workflow to pass before merge.
4. Enable Dependabot alerts, secret scanning and push protection when available, and private vulnerability reporting.
5. Disable direct pushes to `main` for contributors and require pull requests.
6. Confirm GitHub recognizes `LICENSE`, `SECURITY.md`, `CONTRIBUTING.md`, and `CODE_OF_CONDUCT.md` in Community Standards.

NSDeck is released under Apache License 2.0. Do not remove `LICENSE`, `NOTICE`, or third-party notices from source or binary distributions.
