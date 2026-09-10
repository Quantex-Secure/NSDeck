using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NSDeck.Core.Services;

public sealed record UpdateManifest(string Version, string DownloadUrl, string Sha256);

public sealed record UpdateCheckResult(bool UpdateAvailable, Version CurrentVersion, Version? AvailableVersion, Uri? DownloadUri, string Message, string? Sha256 = null);

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly bool _ownsHttpClient;

    public UpdateService(HttpClient? httpClient = null, Version? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _currentVersion = currentVersion ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        _ownsHttpClient = httpClient is null;
    }

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, _currentVersion, null, null, "The update manifest must use a valid HTTPS address.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await GetHttpsAsync(manifestUri, timeout.Token);
        await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token);
        var manifest = await response.Content.ReadFromJsonAsync<UpdateManifest>(timeout.Token);
        if (manifest is null || !Version.TryParse(manifest.Version, out var available))
            return new UpdateCheckResult(false, _currentVersion, null, null, "The update manifest did not contain a valid version.");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            return new UpdateCheckResult(false, _currentVersion, available, null, "The update download must use a valid HTTPS address.");
        if (manifest.Sha256 is null || !Regex.IsMatch(manifest.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
            return new UpdateCheckResult(false, _currentVersion, available, null, "The manifest requires a valid SHA-256 checksum.");

        return available > _currentVersion
            ? new UpdateCheckResult(true, _currentVersion, available, downloadUri, $"NSDeck {available} is available.", manifest.Sha256)
            : new UpdateCheckResult(false, _currentVersion, available, downloadUri, $"NSDeck {_currentVersion.ToString(3)} is current.", manifest.Sha256);
    }

    public async Task<string> DownloadVerifiedAsync(UpdateCheckResult update, string directory, CancellationToken cancellationToken = default)
    {
        if (!update.UpdateAvailable || update.DownloadUri?.Scheme != Uri.UriSchemeHttps || update.Sha256 is null || !Regex.IsMatch(update.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidOperationException("Check a valid HTTPS update manifest before downloading.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"NSDeck-{update.AvailableVersion}-{Guid.NewGuid():N}.exe");
        var temporary = path + ".partial";
        try
        {
            using var response = await GetHttpsAsync(update.DownloadUri, cancellationToken);
            const long limit = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update exceeds the 512 MiB download limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > limit) throw new InvalidDataException("The update exceeds the download limit.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new InvalidDataException("The downloaded file does not match its SHA-256 checksum. It was removed.");
            }
            File.Move(temporary, path);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Update redirects must use HTTPS.");
            var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.RequestMessage?.RequestUri?.Scheme is { } scheme && scheme != Uri.UriSchemeHttps)
            { response.Dispose(); throw new InvalidOperationException("An update redirected to an insecure address."); }
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidOperationException("Too many update redirects.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
