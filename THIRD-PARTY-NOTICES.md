# Third-party notices

NSDeck depends on open-source software distributed under its own license terms.
The primary runtime dependency families are:

- AWS SDK for .NET (`AWSSDK.Route53` and `AWSSDK.Core`) — Apache License 2.0.
- Azure SDK for .NET (`Azure.Identity`, `Azure.Core`, and supporting Microsoft packages) — MIT License.
- Google APIs Client Library for .NET (`Google.Apis.Dns.v1`, `Google.Apis`, and `Google.Apis.Auth`) — Apache License 2.0.
- Newtonsoft.Json — MIT License.
- .NET and Windows Desktop Runtime components — MIT License and the third-party terms identified by Microsoft.

Exact resolved versions can be inspected from the restored dependency graph with:

```powershell
dotnet list .\src\NSDeck.Desktop\NSDeck.Desktop.csproj package --include-transitive
```

Each NuGet package contains authoritative license metadata. Binary release ZIPs also include the `ThirdPartyNotices.txt` distributed with the .NET SDK used to create that release.

Product and service names belong to their respective owners. Their appearance identifies interoperability only and does not imply endorsement of NSDeck or Quantex Secure.
