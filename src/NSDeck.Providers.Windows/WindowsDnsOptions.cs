namespace NSDeck.Providers.Windows;

public sealed record WindowsDnsOptions(
    string Server,
    string EndpointName = "NSDeck.Dns",
    bool SupportsPublicDnsPropagation = false);
