# Changelog

All notable changes to NSDeck will be documented in this file. The project follows [Semantic Versioning](https://semver.org/).

## Unreleased

## 0.6.0 - 2026-08-18

Initial Quantex Secure public release candidate.

### Added

- Unified zone and record management for Namecheap, Azure DNS, GoDaddy, Cloudflare, AWS Route 53, Google Cloud DNS, and Microsoft Windows DNS Server.
- Least-privilege Windows DNS administration through a constrained PowerShell JEA endpoint.
- Persistent Windows DNS JEA sessions for fast zone navigation after the initial connection.
- Guarded writes with validation, optimistic-concurrency checks, pre-change snapshots, post-write verification, and rollback support.
- DNS Change Lab for coordinated multi-zone changes with dependency and blast-radius analysis.
- Public DNS propagation radar using Cloudflare and Google recursive resolvers.
- Credential-free audit logging and anonymized shareable diagnostic reports.
- Quantex Secure product metadata, open-source licensing, CI, dependency updates, and GitHub community health files.
