# Release and signing

Run `build-release.ps1` to execute the privacy scan and tests, create the self-contained Windows executable, produce a ZIP archive, and generate SHA-256 checksums for the executable and ZIP.

The ZIP includes the versioned executable, Windows DNS JEA installer, Apache license, project notice, dependency notice, and the official .NET third-party notice file from the SDK used for the build.

If Inno Setup 6 is installed and `ISCC.exe` is available, the same command compiles the per-user installer from `installer\NSDeck.iss`.

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

Host the manifest and executable at that location, then enter the manifest address on the application's Updates settings tab. The application can check automatically and notify the user, but it deliberately requires confirmation before opening the download. Silent replacement remains disabled until signed releases are available.
