using System.Net;
using System.Text;
using Azure.Core;
using NSDeck.Core.Models;
using NSDeck.Providers.Cloud;

namespace NSDeck.Tests;

public sealed class CloudProviderTests
{
    [Fact]
    public async Task GoDaddy_reads_common_and_srv_records()
    {
        const string json = """
            [
              {"type":"A","name":"@","data":"203.0.113.8","ttl":600},
              {"type":"MX","name":"@","data":"mail.example.com","ttl":1800,"priority":10},
              {"type":"SRV","name":"_sip._tcp","data":"sip.example.com","ttl":600,"priority":20,"weight":5,"port":5060}
            ]
            """;
        var handler = new RouterHandler(_ => Json(json));
        using var provider = new GoDaddyDnsProvider(new GoDaddyDnsOptions("token"), new HttpClient(handler));

        var zone = await provider.GetZoneAsync("example.com");

        Assert.Equal(3, zone.Records.Count);
        Assert.Equal(10, zone.Records.Single(record => record.Type == "MX").Priority);
        Assert.Equal("20 5 5060 sip.example.com", zone.Records.Single(record => record.Type == "SRV").Value);
        Assert.Equal("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task GoDaddy_blocks_ttl_below_provider_minimum()
    {
        using var provider = new GoDaddyDnsProvider(new GoDaddyDnsOptions("token"), new HttpClient(new RouterHandler(_ => Json("{}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReplaceZoneAsync("example.com",
            [new DnsRecord { Name = "@", Type = "A", Value = "203.0.113.8", TtlSeconds = 300 }]));
        Assert.Contains("600", error.Message);
    }

    [Fact]
    public async Task Cloudflare_maps_zone_and_relative_record_names()
    {
        var handler = new RouterHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/zones", StringComparison.Ordinal)
            ? Json("""{"success":true,"result":[{"id":"zone1","name":"example.com","status":"active"}],"result_info":{"total_pages":1}}""")
            : Json("""{"success":true,"result":[{"id":"r1","type":"A","name":"www.example.com","content":"203.0.113.9","ttl":300}],"result_info":{"total_pages":1}}"""));
        using var provider = new CloudflareDnsProvider(new CloudflareDnsOptions("token"), new HttpClient(handler));

        var domains = await provider.GetDomainsAsync();
        var zone = await provider.GetZoneAsync("example.com");

        Assert.Single(domains);
        Assert.True(domains[0].IsUsingProviderDns);
        Assert.Equal("www", Assert.Single(zone.Records).Name);
        Assert.Equal("r1", zone.Records[0].ProviderRecordId);
    }

    [Fact]
    public async Task Azure_updates_existing_record_sets_with_their_etag()
    {
        var headers = new List<string>();
        var handler = new RouterHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                headers.AddRange(request.Headers.TryGetValues("If-Match", out var values) ? values : []);
                return Json("{}");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/dnszones", StringComparison.OrdinalIgnoreCase))
                return Json("""{"value":[{"name":"example.com","id":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/dnszones/example.com"}]}""");
            return Json("""{"value":[{"id":"record-id","name":"www","type":"Microsoft.Network/dnszones/A","etag":"W/\"etag-123\"","properties":{"TTL":300,"ARecords":[{"ipv4Address":"192.0.2.1"}]}}]}""");
        });
        using var provider = new AzureDnsProvider(new AzureDnsOptions("subscription"), new HttpClient(handler), new StaticCredential());

        await provider.ReplaceZoneAsync("example.com", [new DnsRecord
        {
            Name = "www", Type = "A", Value = "198.51.100.1", TtlSeconds = 300
        }]);

        Assert.Equal("W/\"etag-123\"", Assert.Single(headers));
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class RouterHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StaticCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AccessToken("test-token", DateTimeOffset.MaxValue));
    }
}
