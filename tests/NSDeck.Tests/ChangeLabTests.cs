using System.Net;
using System.Text;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;
using NSDeck.Core.Storage;

namespace NSDeck.Tests;

public sealed class ChangeLabTests
{
    [Fact]
    public void Risk_analyzer_flags_removal_of_all_mail_routing()
    {
        var original = new[] { Record("@", "MX", "mail.example.com.", 10), Record("mail", "A", "203.0.113.10") };

        var report = ZoneRiskAnalyzer.Analyze(original, [original[1].Clone()]);

        Assert.True(report.HasCriticalRisks);
        Assert.Contains(report.Risks, risk => risk.Message.Contains("inbound email", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dependency_analyzer_finds_records_that_use_a_selected_target()
    {
        var mail = new DnsInventoryRecord("Test", "example.com", Record("mail", "A", "203.0.113.10"));
        var mx = new DnsInventoryRecord("Test", "example.com", Record("@", "MX", "mail.example.com.", 10));

        var dependencies = DnsDependencyAnalyzer.Analyze([mail, mx], [mail]);

        Assert.Contains(dependencies, dependency => dependency.Source == mx && dependency.Relationship.Contains("depends", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Public_resolver_queries_cloudflare_and_google()
    {
        const string json = """{"Status":0,"Answer":[{"name":"www.example.com.","type":1,"TTL":300,"data":"203.0.113.10"}]}""";
        using var service = new PublicDnsResolverService(new HttpClient(new StaticJsonHandler(json)));

        var results = await service.ResolveAsync("www.example.com", "A");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("203.0.113.10", Assert.Single(result.Answers).Data));
    }

    [Fact]
    public async Task Update_check_reports_a_newer_https_release()
    {
        const string json = """{"version":"0.4.0","downloadUrl":"https://downloads.example.com/NSDeck.exe","sha256":"ABC"}""";
        using var service = new UpdateService(new HttpClient(new StaticJsonHandler(json)), new Version(0, 3, 0));

        var result = await service.CheckAsync("https://downloads.example.com/update-manifest.json");

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(0, 4, 0), result.AvailableVersion);
        Assert.Equal(Uri.UriSchemeHttps, result.DownloadUri?.Scheme);
    }

    [Fact]
    public async Task Coordinated_apply_rolls_back_completed_zones_when_a_later_provider_fails()
    {
        var first = new FakeProvider("First", "one.example", [Record("www", "A", "192.0.2.1")]);
        var second = new FakeProvider("Second", "two.example", [Record("www", "A", "192.0.2.2")]) { FailWrites = true };
        var firstZone = Inventory(first);
        var secondZone = Inventory(second);
        var firstUpdated = firstZone.Records[0].Clone(); firstUpdated.Value = "198.51.100.1";
        var secondUpdated = secondZone.Records[0].Clone(); secondUpdated.Value = "198.51.100.2";
        var snapshots = new MemorySnapshotStore();
        var service = new DnsChangeLabService(snapshots, verificationDelays: [TimeSpan.Zero]);

        var result = await service.ApplyAsync([
            new DnsPlannedChange(firstZone, firstZone.Records[0], firstUpdated),
            new DnsPlannedChange(secondZone, secondZone.Records[0], secondUpdated)
        ]);

        Assert.False(result.Succeeded);
        Assert.True(result.RollbackAttempted);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal("192.0.2.1", Assert.Single((await first.GetZoneAsync("one.example")).Records).Value);
        Assert.Equal(2, snapshots.Snapshots.Count);
    }

    private static DnsInventoryZone Inventory(FakeProvider provider)
    {
        var records = provider.Current.Select(record => record.Clone()).ToArray();
        return new DnsInventoryZone(provider, provider.ProviderName, provider.Domain, records, DateTimeOffset.Now);
    }

    private static DnsRecord Record(string name, string type, string value, int? priority = null) => new()
    {
        Name = name, Type = type, Value = value, TtlSeconds = 1800, Priority = priority
    };

    private sealed class StaticJsonHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/dns-json")
            });
    }

    private sealed class FakeProvider(string name, string domain, IReadOnlyList<DnsRecord> records) : IDnsProvider
    {
        public string ProviderName { get; } = name;
        public string Domain { get; } = domain;
        public bool FailWrites { get; set; }
        public IReadOnlyList<DnsRecord> Current { get; private set; } = records.Select(record => record.Clone()).ToArray();

        public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DomainSummary>>([new DomainSummary(Domain, ProviderName)]);

        public Task<DnsZone> GetZoneAsync(string requestedDomain, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DnsZone(requestedDomain, ProviderName, Current.Select(record => record.Clone()).ToArray(), DateTimeOffset.Now));

        public Task ReplaceZoneAsync(string requestedDomain, IReadOnlyList<DnsRecord> replacement, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new InvalidOperationException("Simulated provider failure.");
            Current = replacement.Select(record => record.Clone()).ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySnapshotStore : IZoneSnapshotStore
    {
        public List<ZoneSnapshot> Snapshots { get; } = [];
        public Task SaveAsync(ZoneSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ZoneSnapshot>> GetRecentAsync(string domain, int count = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ZoneSnapshot>>(Snapshots.Where(snapshot => snapshot.Domain == domain).Take(count).ToArray());
    }
}
