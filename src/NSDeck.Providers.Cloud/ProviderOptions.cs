namespace NSDeck.Providers.Cloud;

public sealed record AzureDnsOptions(
    string SubscriptionId,
    string TenantId = "",
    string ClientId = "",
    string ClientSecret = "");

public sealed record GoDaddyDnsOptions(string AccessToken);

public sealed record CloudflareDnsOptions(string ApiToken);

public sealed record Route53DnsOptions(
    string AccessKeyId,
    string SecretAccessKey,
    string SessionToken = "");

public sealed record GoogleCloudDnsOptions(
    string ProjectId,
    string ServiceAccountJsonPath = "");
