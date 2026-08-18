using System.Net;
using System.Text;
using NSDeck.Core.Models;
using NSDeck.Providers.Namecheap;

namespace NSDeck.Tests;

public sealed class NamecheapDnsProviderTests
{
    [Fact]
    public async Task GetDomains_parses_the_account_domain_list()
    {
        const string xml = """
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse><DomainGetListResult>
                <Domain Name="example.com" IsExpired="false" IsLocked="true" IsOurDNS="true" />
                <Domain Name="example.net" IsExpired="false" IsLocked="false" IsOurDNS="true" />
                <Domain Name="external.example" IsExpired="false" IsLocked="false" IsOurDNS="false" />
              </DomainGetListResult><Paging><TotalItems>3</TotalItems></Paging></CommandResponse>
            </ApiResponse>
            """;
        using var provider = CreateProvider(new StaticHandler(xml));

        var domains = await provider.GetDomainsAsync();

        Assert.Equal(2, domains.Count);
        Assert.Equal("example.com", domains[0].Name);
        Assert.True(domains[0].IsLocked);
        Assert.DoesNotContain(domains, domain => domain.Name == "external.example");
        Assert.All(domains, domain => Assert.True(domain.IsUsingProviderDns));
    }

    [Fact]
    public async Task GetZone_preserves_record_fields()
    {
        const string xml = """
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse><DomainDNSGetHostsResult Domain="example.com" IsUsingOurDNS="true">
                <host HostId="12" Name="@" Type="A" Address="203.0.113.10" MXPref="10" TTL="300" />
                <host HostId="14" Name="mail" Type="MX" Address="mail.example.com." MXPref="20" TTL="1800" />
              </DomainDNSGetHostsResult></CommandResponse>
            </ApiResponse>
            """;
        using var provider = CreateProvider(new StaticHandler(xml));

        var zone = await provider.GetZoneAsync("example.com");

        Assert.True(zone.IsUsingProviderDns);
        Assert.Equal(2, zone.Records.Count);
        Assert.Equal(20, zone.Records.Single(record => record.Type == "MX").Priority);
    }

    [Fact]
    public async Task GetZone_normalizes_namecheap_verification_metadata()
    {
        const string xml = """
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse><DomainDNSGetHostsResult Domain="example.com" IsUsingOurDNS="true">
                <host HostId="12" Name="www" Type="A" Address="203.0.113.10" MXPref="10" TTL="1799" />
              </DomainDNSGetHostsResult></CommandResponse>
            </ApiResponse>
            """;
        using var provider = CreateProvider(new StaticHandler(xml));

        var zone = await provider.GetZoneAsync("example.com");
        var record = Assert.Single(zone.Records);

        Assert.Equal(1800, record.TtlSeconds);
        Assert.Null(record.Priority);
    }

    [Fact]
    public async Task ReplaceZone_sends_every_record_in_one_request()
    {
        var handler = new CaptureHandler("""
            <ApiResponse Status="OK" xmlns="http://api.namecheap.com/xml.response">
              <CommandResponse><DomainDNSSetHostsResult Domain="example.com" IsSuccess="true" /></CommandResponse>
            </ApiResponse>
            """);
        using var provider = CreateProvider(handler);

        await provider.ReplaceZoneAsync("example.com", [
            Record("@", "A", "203.0.113.10"),
            Record("www", "CNAME", "example.com.")
        ]);

        Assert.Contains("HostName1=%40", handler.RequestBody);
        Assert.Contains("HostName2=www", handler.RequestBody);
        Assert.Contains("Address1=203.0.113.10", handler.RequestBody);
        Assert.Contains("Address2=example.com.", handler.RequestBody);
    }

    [Fact]
    public async Task ReplaceZone_refuses_an_empty_zone_without_calling_the_api()
    {
        var handler = new CaptureHandler("<ApiResponse Status=\"OK\" />");
        using var provider = CreateProvider(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReplaceZoneAsync("example.com", []));
        Assert.Null(handler.RequestBody);
    }

    private static NamecheapDnsProvider CreateProvider(HttpMessageHandler handler) => new(
        new NamecheapOptions("apiuser", "username", "secret", "203.0.113.40"),
        new HttpClient(handler));

    private static DnsRecord Record(string name, string type, string value) => new()
    {
        Name = name, Type = type, Value = value, TtlSeconds = 1800
    };

    private sealed class StaticHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/xml") });
    }

    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/xml") };
        }
    }
}
