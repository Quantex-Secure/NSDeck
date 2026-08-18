# Security policy

NSDeck changes authoritative DNS data and can connect to internal Windows DNS infrastructure. Please report security defects privately and never include real credentials or production zone data in a public issue.

## Supported versions

Security fixes are made against the latest published minor release. Users should upgrade to the newest available release before requesting support for an older build.

## Reporting a vulnerability

Use the repository's **Security → Report a vulnerability** flow to open a private GitHub security advisory. If private vulnerability reporting is unavailable, contact a repository maintainer privately before sharing technical details.

Include:

- the affected NSDeck version;
- the provider and authentication path involved;
- minimal reproduction steps using reserved example domains and addresses;
- expected and observed security impact; and
- any suggested mitigation.

Do not attach API keys, tokens, service-account files, DPAPI settings, raw logs, snapshots, JEA transcripts, internal hostnames, or real DNS records. The maintainers will make a reasonable effort to acknowledge a complete report within five business days and will coordinate disclosure after a fix is available.

## Security design

- Provider settings are encrypted with Windows DPAPI for the current user and machine.
- Windows DNS uses a constrained JEA endpoint and the caller's Windows identity; NSDeck does not store Domain Admin credentials.
- Zone writes are validated, checked for concurrent changes, snapshotted, applied, and verified.
- Shareable diagnostics anonymize domains, server names, fingerprints, and provider error details.
- Release artifacts are expected to be distributed with SHA-256 checksums. Until Quantex Secure publishes signed binaries, Windows may identify releases as coming from an unknown publisher.
