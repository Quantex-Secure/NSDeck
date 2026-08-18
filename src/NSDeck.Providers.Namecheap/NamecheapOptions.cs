namespace NSDeck.Providers.Namecheap;

public sealed record NamecheapOptions(
    string ApiUser,
    string UserName,
    string ApiKey,
    string ClientIp,
    bool UseSandbox = false)
{
    public Uri Endpoint => new(UseSandbox
        ? "https://api.sandbox.namecheap.com/xml.response"
        : "https://api.namecheap.com/xml.response");

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ApiUser) &&
        !string.IsNullOrWhiteSpace(UserName) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ClientIp);
}
