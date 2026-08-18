using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NSDeck.Providers.Cloud;

public abstract class JsonDnsProviderBase : IDisposable
{
    private readonly bool _ownsHttpClient;

    protected JsonDnsProviderBase(HttpClient? httpClient = null)
    {
        HttpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        HttpClient.Timeout = TimeSpan.FromSeconds(45);
    }

    protected HttpClient HttpClient { get; }

    protected async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string url,
        object? body,
        string? bearerToken,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        if (headers is not null)
        {
            foreach (var header in headers)
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = payload.Length > 700 ? payload[..700] + "…" : payload;
            throw new InvalidOperationException($"The DNS provider returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}".Trim());
        }
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) HttpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
