using System.Net.Http.Json;

namespace NSDeck.Core.Services;

public sealed record UpdateManifest(string Version, string DownloadUrl, string Sha256);

public sealed record UpdateCheckResult(bool UpdateAvailable, Version CurrentVersion, Version? AvailableVersion, Uri? DownloadUri, string Message);

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly bool _ownsHttpClient;

    public UpdateService(HttpClient? httpClient = null, Version? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _currentVersion = currentVersion ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        _ownsHttpClient = httpClient is null;
    }

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, _currentVersion, null, null, "The update manifest must use a valid HTTPS address.");

        var manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(manifestUri, cancellationToken);
        if (manifest is null || !Version.TryParse(manifest.Version, out var available))
            return new UpdateCheckResult(false, _currentVersion, null, null, "The update manifest did not contain a valid version.");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, _currentVersion, available, null, "The update download must use a valid HTTPS address.");

        return available > _currentVersion
            ? new UpdateCheckResult(true, _currentVersion, available, downloadUri, $"NSDeck {available} is available.")
            : new UpdateCheckResult(false, _currentVersion, available, downloadUri, $"NSDeck {_currentVersion.ToString(3)} is current.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
