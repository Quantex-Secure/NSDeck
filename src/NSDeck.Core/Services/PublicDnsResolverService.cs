using System.Net.Http.Headers;
using System.Text.Json;

namespace NSDeck.Core.Services;

public sealed record DnsPublicAnswer(string Name, int Type, int Ttl, string Data);

public sealed record DnsResolverResult(
    string Resolver,
    int? ResponseCode,
    IReadOnlyList<DnsPublicAnswer> Answers,
    string? Error = null);

public sealed class PublicDnsResolverService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public PublicDnsResolverService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyList<DnsResolverResult>> ResolveAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default)
    {
        var encodedName = Uri.EscapeDataString(name.Trim().TrimEnd('.'));
        var encodedType = Uri.EscapeDataString(type.Trim().ToUpperInvariant());
        var queries = new[]
        {
            QueryAsync("Cloudflare 1.1.1.1", $"https://cloudflare-dns.com/dns-query?name={encodedName}&type={encodedType}", cancellationToken),
            QueryAsync("Google Public DNS", $"https://dns.google/resolve?name={encodedName}&type={encodedType}", cancellationToken)
        };
        return await Task.WhenAll(queries);
    }

    private async Task<DnsResolverResult> QueryAsync(string resolver, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var responseCode = root.TryGetProperty("Status", out var status) && status.TryGetInt32(out var parsedStatus) ? parsedStatus : (int?)null;
            var answers = new List<DnsPublicAnswer>();
            if (root.TryGetProperty("Answer", out var answerArray) && answerArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var answer in answerArray.EnumerateArray())
                {
                    answers.Add(new DnsPublicAnswer(
                        answer.TryGetProperty("name", out var answerName) ? answerName.GetString() ?? string.Empty : string.Empty,
                        answer.TryGetProperty("type", out var answerType) && answerType.TryGetInt32(out var parsedType) ? parsedType : 0,
                        answer.TryGetProperty("TTL", out var ttl) && ttl.TryGetInt32(out var parsedTtl) ? parsedTtl : 0,
                        answer.TryGetProperty("data", out var data) ? data.GetString() ?? string.Empty : string.Empty));
                }
            }
            return new DnsResolverResult(resolver, responseCode, answers);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DnsResolverResult(resolver, null, [], exception.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
