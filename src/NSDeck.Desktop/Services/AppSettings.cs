namespace NSDeck.Desktop.Services;

public sealed class AppSettings
{
    public List<AccountProfile> Profiles { get; init; } = [];
    public string? ActiveProfileId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public AccountProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();
    public NamecheapConnectionSettings Namecheap { get; init; } = new();
    public AzureConnectionSettings Azure { get; init; } = new();
    public TokenConnectionSettings GoDaddy { get; init; } = new();
    public TokenConnectionSettings Cloudflare { get; init; } = new();
    public AwsConnectionSettings Route53 { get; init; } = new();
    public GoogleConnectionSettings Google { get; init; } = new();
    public WindowsDnsConnectionSettings WindowsDns { get; init; } = new();
    public UpdateSettings Updates { get; init; } = new();

    public bool HasEnabledProvider => ActiveProfile is { } profile ? profile.Connections.HasEnabledProvider : Namecheap.Enabled || Azure.Enabled || GoDaddy.Enabled || Cloudflare.Enabled || Route53.Enabled || Google.Enabled || WindowsDns.Enabled;
}

public sealed class AccountProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Default";
    public bool ReadOnly { get; init; }
    public AppSettings Connections { get; init; } = new();
    public override string ToString() => Name + (ReadOnly ? " (read-only)" : "");
}

public sealed class UpdateSettings
{
    public string TrustedSignerThumbprint { get; init; } = "";
    public bool CheckAutomatically { get; init; }
    public string ManifestUrl { get; init; } = "";
}

public sealed class NamecheapConnectionSettings
{
    public bool Enabled { get; init; }
    public string ApiUser { get; init; } = "";
    public string UserName { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string ClientIp { get; init; } = "";
    public bool UseSandbox { get; init; }
}

public sealed class AzureConnectionSettings
{
    public bool Enabled { get; init; }
    public string SubscriptionId { get; init; } = "";
    public string TenantId { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}

public sealed class TokenConnectionSettings
{
    public bool Enabled { get; init; }
    public string Token { get; init; } = "";
}

public sealed class AwsConnectionSettings
{
    public bool Enabled { get; init; }
    public string AccessKeyId { get; init; } = "";
    public string SecretAccessKey { get; init; } = "";
    public string SessionToken { get; init; } = "";
}

public sealed class GoogleConnectionSettings
{
    public bool Enabled { get; init; }
    public string ProjectId { get; init; } = "";
    public string ServiceAccountJsonPath { get; init; } = "";
}

public sealed class WindowsDnsConnectionSettings
{
    public bool Enabled { get; init; }
    public string Servers { get; init; } = "";
    public string EndpointName { get; set; } = "NSDeck.Dns";
    public bool SupportsPublicDnsPropagation { get; init; }
}
