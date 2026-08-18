# GitHub publication setup

Use these values when creating the public repository:

- **Repository name:** `NSDeck`
- **Description:** `A guarded multi-provider DNS administration console for Windows by Quantex Secure.`
- **Visibility:** Public
- **Topics:** `dns`, `dns-management`, `dotnet`, `wpf`, `windows`, `azure-dns`, `cloudflare`, `route53`, `namecheap`, `windows-dns`, `jea`
- **Social preview:** `design/nsdeck-render.png`

Do not ask GitHub to generate a README, `.gitignore`, or license. The local repository already contains all three.

## First push

Choose the intended Quantex Secure GitHub organization or account and a verified public commit identity. Then create the empty repository and run:

```powershell
git add .
git commit -m "Initial public release of NSDeck 0.6.0"
git remote add origin https://github.com/QUANTEX-SECURE-OWNER/NSDeck.git
git push -u origin main
```

Replace `QUANTEX-SECURE-OWNER` before running the commands. Review `git status --ignored` immediately before the first commit. The `release`, `artifacts`, and `work` directories and the local identity-term list must remain ignored.

## Repository protections

After the first push:

1. Require pull requests and the **Build and test** CI check for `main`.
2. Enable Dependabot alerts and security updates.
3. Enable secret scanning and push protection when available.
4. Enable private vulnerability reporting.
5. Restrict release creation and workflow changes to trusted maintainers.
6. Add the sanitized screenshot as the repository social preview.

Do not configure provider API credentials as GitHub secrets. Automated tests are intentionally offline. Add signing credentials only as part of a separately reviewed, hardware-backed release-signing design.

## First release

Create tag `v0.6.0` after CI passes. Upload only:

- `NSDeck-0.6.0.exe`
- `NSDeck-0.6.0-win-x64.zip`
- `NSDeck-0.6.0-SHA256.txt`
- a signed installer, when one is available

The executable is currently unsigned. Label it clearly as a preview until Quantex Secure has a trusted Authenticode signing certificate and a protected release process.
