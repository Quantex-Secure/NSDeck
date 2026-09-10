# Release and signing

Run `build-release.ps1` to execute the privacy scan and tests, create the self-contained Windows executable, produce a ZIP archive, and generate SHA-256 checksums for the executable and ZIP.

The ZIP includes the versioned executable, Windows DNS JEA installer, Apache license, project notice, dependency notice, and the official .NET third-party notice file from the SDK used for the build.

If Inno Setup 6 or later is installed and `ISCC.exe` is available, the same command compiles the per-user installer from `installer\NSDeck.iss`.

To Authenticode-sign the executable and installer, install a trusted code-signing certificate in the Windows certificate store and pass its thumbprint:

```powershell
.\build-release.ps1 -CertificateThumbprint "CERTIFICATE_THUMBPRINT"
```

The signing step uses SHA-256 and a trusted timestamp. A certificate is intentionally not stored in the repository.

Automatic updates require a stable HTTPS release location and signing identity. Those external values must be chosen before an updater can safely be enabled; the application must never install an unsigned update merely because a remote version number is newer.

Supplying `-ReleaseBaseUri` fills the generated `update-manifest.json` with the versioned executable's HTTPS address:

```powershell
.\build-release.ps1 -ReleaseBaseUri "https://downloads.example.com/nsdeck"
```

Host the manifest and executable at that location, then enter the manifest address on the application's Updates settings tab. The application asks before downloading and verifies the manifest SHA-256 against the streamed artifact. Starting that artifact additionally requires Windows Authenticode validation and a match to the trusted publisher thumbprint configured independently in Settings. Unsigned previews remain download-only in the updater; silent installation is disabled.

## Preview packaging

The `Package preview release` workflow accepts an existing tag, checks it against the project version, runs the release build, and uploads reviewable artifacts. It does not publish automatically. It uses Inno Setup when that compiler is present on the Windows runner.

For this preview:

```powershell
.\build-release.ps1 -ReleaseBaseUri "https://github.com/Quantex-Secure/NSDeck/releases/download/v0.7.0-preview.1"
```

Publish the versioned executable, portable ZIP, installer when available, SHA-256 file, and `update-manifest.json` as a GitHub **prerelease**, using `docs/RELEASE_NOTES_0.7.md`. The tag must refer to the source used to build those artifacts. Do not label an unsigned preview as a signed or production-validated build. A signing certificate must be provisioned separately; never generate a self-signed certificate to impersonate trusted publisher status.
