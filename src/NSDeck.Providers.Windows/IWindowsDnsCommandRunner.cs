namespace NSDeck.Providers.Windows;

public interface IWindowsDnsCommandRunner
{
    Task<string> InvokeAsync(
        string server,
        string endpointName,
        WindowsDnsOperation operation,
        string? zoneName = null,
        string? recordsJson = null,
        CancellationToken cancellationToken = default);
}

public enum WindowsDnsOperation
{
    ListZones,
    ReadZone,
    ReplaceZone
}
