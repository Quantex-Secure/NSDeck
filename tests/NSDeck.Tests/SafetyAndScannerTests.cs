using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;
using NSDeck.Core.Storage;
using NSDeck.Providers.Cloud;
using Azure.Core;

namespace NSDeck.Tests;

public sealed class SafetyAndScannerTests
{
    private static DnsRecord A(string value = "192.0.2.1", string name = "www") => new() { Name = name, Type = "A", Value = value, TtlSeconds = 300 };
    private static DnsPlannedChange Plan(FakeProvider provider)
    {
        var zone = new DnsInventoryZone(provider, provider.ProviderName, "example.com", provider.Current.Select(r => r.Clone()).ToArray(), DateTimeOffset.Now);
        var updated = zone.Records[0].Clone(); updated.Value = "198.51.100.10";
        return new(zone, zone.Records[0], updated);
    }

    [Fact]
    public async Task A_write_that_mutates_then_throws_is_inspected_and_recovered()
    {
        var provider = new FakeProvider { FailAfterWriteOnce = true };
        var result = await new DnsChangeLabService(new MemoryStore(), verificationDelays: [TimeSpan.Zero]).ApplyAsync([Plan(provider)]);
        Assert.False(result.Succeeded); Assert.True(result.RollbackAttempted); Assert.True(result.RollbackSucceeded);
        Assert.Equal("192.0.2.1", provider.Current[0].Value);
    }

    [Fact]
    public async Task Recovery_does_not_overwrite_an_intervening_edit()
    {
        var first = new FakeProvider();
        var second = new FakeProvider { BeforeWrite = () => first.Current = [A("203.0.113.99")], FailBeforeWrite = true };
        var result = await new DnsChangeLabService(new MemoryStore(), verificationDelays: [TimeSpan.Zero]).ApplyAsync([Plan(first), Plan(second)]);
        Assert.False(result.RollbackSucceeded); Assert.Equal("203.0.113.99", first.Current[0].Value);
    }

    [Fact]
    public void Recovery_preserves_unrelated_records()
    {
        var restored = ZoneRecovery.Build([A()], [A("198.51.100.10")], [A("198.51.100.10"), A("203.0.113.99", "other")]);
        Assert.Equal("192.0.2.1", restored.Single(r => r.Name == "www").Value);
        Assert.Equal("203.0.113.99", restored.Single(r => r.Name == "other").Value);
    }

    [Fact]
    public void Ambiguous_partial_record_sets_require_manual_review()
    {
        Assert.Throws<InvalidOperationException>(() => ZoneRecovery.Build([A(), A("192.0.2.2")], [A("198.51.100.1"), A("198.51.100.2")], [A("198.51.100.1"), A("192.0.2.2")]));
    }

    [Fact]
    public async Task Cancelled_inventory_stops_instead_of_swallowing_cancellation()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var service = new DnsChangeLabService(new MemoryStore());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadInventoryAsync([new(new FakeProvider(), [new("example.com", "test")])], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Read_only_account_blocks_direct_and_guarded_writes()
    {
        using var provider = new AccountDnsProvider(new FakeProvider(), "profile", "Customer", true);
        var target = ((IDnsProvider)provider).ForZone(new("example.com", "test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ReplaceZoneAsync("example.com", [A()]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.ReplaceZoneGuardedAsync("example.com", [A()], [A("198.51.100.10")]));
    }

    [Fact]
    public async Task Snapshot_history_is_separated_by_account_and_zone_id()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NSDeckTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonZoneSnapshotStore(directory);
            foreach (var target in new[] { ("one", "public"), ("one", "private"), ("two", "public") })
                await store.SaveAsync(new("example.com", "test", DateTimeOffset.Now, "", [A()], target.Item1, target.Item2));
            var snapshots = await store.GetRecentForTargetAsync("example.com", "one", "public");
            Assert.Single(snapshots); Assert.Equal("one", snapshots[0].AccountId); Assert.Equal("public", snapshots[0].ZoneId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Cloudflare_snapshot_reuses_current_id_instead_of_deleting_then_patching_missing_id()
    {
        var writes = new List<string>();
        using var provider = new CloudflareDnsProvider(new("test"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) { writes.Add(request.Method + " " + request.RequestUri!.AbsolutePath); return Json("{\"success\":true}"); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/zones")) return Json("""{"success":true,"result":[{"id":"z1","name":"example.com","status":"active"}]}""");
            return Json("""{"success":true,"result":[{"id":"new-id","type":"A","name":"www.example.com","content":"198.51.100.2","ttl":300,"proxied":true}]}""");
        })));
        await provider.ReplaceZoneAsync("example.com", [new() { Name = "www", Type = "A", Value = "192.0.2.1", TtlSeconds = 300, ProviderRecordId = "old-id", ProviderMetadata = "{\"proxied\":true}" }]);
        Assert.Equal("PATCH /client/v4/zones/z1/dns_records/new-id", Assert.Single(writes));
    }

    [Fact]
    public async Task Cloudflare_restores_a_deleted_record_with_post_and_preserves_metadata()
    {
        string? payload = null;
        using var provider = new CloudflareDnsProvider(new("test"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) { Assert.Equal(HttpMethod.Post, request.Method); payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Json("{\"success\":true}"); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/zones")) return Json("""{"success":true,"result":[{"id":"z1","name":"example.com","status":"active"}]}""");
            return Json("{\"success\":true,\"result\":[]}");
        })));
        await provider.ReplaceZoneAsync("example.com", [new() { Name = "www", Type = "A", Value = "192.0.2.1", TtlSeconds = 1, ProviderRecordId = "deleted", ProviderMetadata = "{\"proxied\":true,\"comment\":\"managed\"}" }]);
        Assert.Contains("\"proxied\":true", payload); Assert.Contains("managed", payload);
    }

    [Fact]
    public async Task Azure_rejects_invalid_plan_before_sending_deletions()
    {
        var writes = 0;
        using var provider = new AzureDnsProvider(new("subscription"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return request.RequestUri!.AbsolutePath.EndsWith("/dnszones")
                ? Json("""{"value":[{"name":"example.com","id":"/zones/z1"}]}""")
                : Json("""{"value":[{"name":"old","type":"Microsoft.Network/dnszones/A","properties":{"TTL":300,"ARecords":[{"ipv4Address":"192.0.2.1"}]}}]}""");
        })), new Credential());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReplaceZoneAsync("example.com", [A("198.51.100.1"), new() { Name = "www", Type = "A", Value = "198.51.100.2", TtlSeconds = 600 }]));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Azure_rejects_a_change_between_preflight_and_its_own_read()
    {
        var writes = 0;
        using var provider = new AzureDnsProvider(new("subscription"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Json("""{"value":[{"name":"www","type":"Microsoft.Network/dnszones/A","etag":"new","properties":{"TTL":300,"ARecords":[{"ipv4Address":"203.0.113.99"}]}}]}""");
        })), new Credential());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReplaceZoneGuardedAsync(new DomainSummary("example.com", "Azure", ZoneId: "/zones/z1"), [A()], [A("198.51.100.10")]));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Route53_duplicate_names_retain_their_zone_ids_and_visibility()
    {
        using var client = new RouteClient(); using var provider = new Route53DnsProvider(new("", ""), client);
        var zones = await provider.GetDomainsAsync();
        Assert.Equal(2, zones.Count);
        var privateTarget = ((IDnsProvider)provider).ForZone(zones.Single(z => !z.IsPublic));
        await privateTarget.GetZoneAsync("example.com"); Assert.Equal("private", client.ReadId); Assert.False(privateTarget.SupportsPublicDnsPropagation);
        var publicTarget = ((IDnsProvider)provider).ForZone(zones.Single(z => z.IsPublic));
        await publicTarget.GetZoneAsync("example.com"); Assert.Equal("public", client.ReadId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetZoneAsync("example.com"));
    }

    [Fact]
    public void Scanner_identifies_duplicate_and_allow_all_spf()
    {
        var records = new[] { Txt("@", "v=spf1 +all"), Txt("@", "v=spf1 -all") };
        var findings = DnsBestPracticeScanner.Scan("example.com", records);
        Assert.Contains(findings, f => f.Rule == "SPF-DUPLICATE"); Assert.Contains(findings, f => f.Rule == "SPF-ALLOW-ALL");
    }

    [Fact]
    public void Guided_setup_does_not_duplicate_or_replace_existing_policies()
    {
        Assert.Throws<InvalidOperationException>(() => DnsBestPracticeScanner.Stage([Txt("@", "v=spf1 include:mail.example.com -all")], [Txt("@", "v=spf1 -all")]));
        Assert.Throws<InvalidOperationException>(() => DnsBestPracticeScanner.Stage([Txt("_dmarc", "v=DMARC1; p=reject")], [Txt("_dmarc", "v=DMARC1; p=none")]));
    }

    [Fact]
    public void No_mail_template_requires_confirmation_and_rejects_existing_mail_routing()
    {
        Assert.Throws<InvalidOperationException>(() => DnsBestPracticeScanner.Create(DnsSetupTemplate.NoMail, "example.com", "@", "", 3600));
        var proposed = DnsBestPracticeScanner.Create(DnsSetupTemplate.NoMail, "example.com", "@", "", 3600, noMailConfirmed: true);
        Assert.Equal(3, proposed.Count);
        Assert.Throws<InvalidOperationException>(() => DnsBestPracticeScanner.Stage([new() { Name = "@", Type = "MX", Value = "mail.example.com.", Priority = 10, TtlSeconds = 3600 }], proposed));
    }

    [Theory]
    [InlineData("bad; p=reject@example.com")]
    [InlineData("not-an-email")]
    public void Dmarc_template_rejects_invalid_reporting_input(string value) => Assert.Throws<InvalidOperationException>(() => DnsBestPracticeScanner.Create(DnsSetupTemplate.DmarcMonitoring, "example.com", "@", value, 3600));

    [Fact]
    public void Dmarc_setup_starts_in_monitoring_mode()
    {
        var record = Assert.Single(DnsBestPracticeScanner.Create(DnsSetupTemplate.DmarcMonitoring, "example.com", "@", "reports@example.com", 3600));
        Assert.Equal("_dmarc", record.Name); Assert.Equal("v=DMARC1; p=none; rua=mailto:reports@example.com", record.Value);
    }

    [Fact]
    public void Protected_record_sets_cannot_be_removed_or_overwritten()
    {
        var managed = new DnsRecord { Name = "@", Type = "A", Value = "provider alias", IsReadOnly = true };
        Assert.Throws<InvalidOperationException>(() => ZoneWriteGuard.Check([managed], null, [A(name: "@")]));
        Assert.Throws<InvalidOperationException>(() => ZoneWriteGuard.Check([managed], null, [managed, A(name: "@")]));
    }

    [Fact]
    public async Task Update_download_verifies_hash_and_removes_corrupt_payload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "NSDeckUpdateTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new UpdateService(new HttpClient(new Handler(_ => Json("payload"))));
            var update = new UpdateCheckResult(true, new(0, 6), new(0, 7), new("https://example.com/app.exe"), "", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("payload"))));
            var path = await service.DownloadVerifiedAsync(update, directory); Assert.Equal("payload", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadVerifiedAsync(update with { Sha256 = new string('0', 64) }, directory));
            Assert.Single(Directory.GetFiles(directory)); Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Update_rejects_insecure_redirect()
    {
        using var service = new UpdateService(new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new("http://example.com/manifest") } })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync("https://example.com/manifest"));
    }

    [Fact]
    public void Diagnostic_exports_anonymize_profile_names()
    {
        var name = new DiagnosticAnonymizer().AnonymizeProvider("Customer confidential / Windows DNS — dc.example.com");
        Assert.DoesNotContain("Customer", name); Assert.DoesNotContain("dc.example.com", name);
    }

    [Fact]
    public void Verification_accepts_equivalent_dns_targets_and_default_metadata()
    {
        var expected = new DnsRecord { Name = "www", Type = "CNAME", Value = "Host.Example.com.", TtlSeconds = 300 };
        var actual = new DnsRecord { Name = "www", Type = "CNAME", Value = "host.example.com", TtlSeconds = 300, ProviderMetadata = "{\"proxied\":false,\"tags\":[],\"comment\":null,\"settings\":{\"ipv4_only\":false}}" };
        Assert.Equal(ZoneComparer.Fingerprint([expected]), ZoneComparer.Fingerprint([actual]));
        actual = new DnsRecord { Name = "www", Type = "CNAME", Value = "host.example.com", TtlSeconds = 300, ProviderMetadata = "{\"proxied\":true}" };
        Assert.NotEqual(ZoneComparer.Fingerprint([expected]), ZoneComparer.Fingerprint([actual]));
    }

    [Fact]
    public void Long_txt_values_round_trip_with_utf8_chunks_within_dns_limits()
    {
        var value = "v=DKIM1; p=" + new string('A', 700) + "é😀\\quoted\"text";
        Assert.All(DnsRecordSemantics.TextChunks(value), chunk => Assert.InRange(Encoding.UTF8.GetByteCount(chunk), 1, 255));
        Assert.Equal(value, DnsRecordSemantics.DecodeText(DnsRecordSemantics.EncodeText(value)));
        Assert.Equal("v=spf1 -all", DnsRecordSemantics.DecodeText("\"v=spf1 \" \"-all\""));
    }

    [Fact]
    public void Automatic_soa_changes_do_not_block_writes_but_removal_is_rejected()
    {
        var original = new DnsRecord { Name = "@", Type = "SOA", Value = "serial 1", TtlSeconds = 3600, IsReadOnly = true, ProviderMetadata = "{\"serial\":1}" };
        var current = new DnsRecord { Name = "@", Type = "SOA", Value = "serial 2", TtlSeconds = 3600, IsReadOnly = true, ProviderMetadata = "{\"serial\":2}" };
        ZoneWriteGuard.Check([current, A()], [original, A()], [original, A("198.51.100.1")]);
        Assert.Throws<InvalidOperationException>(() => ZoneWriteGuard.Check([current, A()], [original, A()], [A()]));
    }

    [Fact]
    public async Task Cloudflare_restores_explicit_dns_only_proxy_setting()
    {
        string? payload = null;
        using var provider = new CloudflareDnsProvider(new("test"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) { payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Json("{\"success\":true}"); }
            return Json("""{"success":true,"result":[{"id":"r1","type":"A","name":"www.example.com","content":"192.0.2.1","ttl":300,"proxied":true}]}""");
        })));
        await provider.ReplaceZoneAsync(new DomainSummary("example.com", "Cloudflare", ZoneId: "z1"), [new() { Name = "www", Type = "A", Value = "192.0.2.1", TtlSeconds = 300, ProviderMetadata = "{\"proxied\":false}" }]);
        Assert.Contains("\"proxied\":false", payload);
    }

    [Fact]
    public async Task Cloudflare_srv_round_trip_uses_structured_api_data()
    {
        string? payload = null;
        using var provider = new CloudflareDnsProvider(new("test"), new HttpClient(new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) { payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Json("{\"success\":true}"); }
            return Json("""{"success":true,"result":[{"id":"r1","type":"SRV","name":"_sip._tcp.example.com","ttl":300,"data":{"priority":10,"weight":20,"port":5060,"target":"sip.example.com"}}]}""");
        })));
        var target = new DomainSummary("example.com", "Cloudflare", ZoneId: "z1");
        var zone = await provider.GetZoneAsync(target);
        var record = Assert.Single(zone.Records); Assert.Equal("10 20 5060 sip.example.com", record.Value);
        record.Value = "10 30 5061 sip.example.com";
        await provider.ReplaceZoneAsync(target, [record]);
        Assert.Contains("\"data\":{\"priority\":10,\"weight\":30,\"port\":5061", payload);
        Assert.DoesNotContain("\"content\"", payload);
    }

    private static DnsRecord Txt(string name, string value) => new() { Name = name, Type = "TXT", Value = value, TtlSeconds = 3600 };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => new("test", DateTimeOffset.MaxValue);
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => ValueTask.FromResult(GetToken(c, ct));
    }
    private sealed class RouteClient() : AmazonRoute53Client(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public string? ReadId { get; private set; }
        public override Task<ListHostedZonesResponse> ListHostedZonesAsync(ListHostedZonesRequest request, CancellationToken ct = default) => Task.FromResult(new ListHostedZonesResponse { HostedZones = [new() { Name = "example.com.", Id = "public", Config = new() { PrivateZone = false } }, new() { Name = "example.com.", Id = "private", Config = new() { PrivateZone = true } }] });
        public override Task<ListResourceRecordSetsResponse> ListResourceRecordSetsAsync(ListResourceRecordSetsRequest request, CancellationToken ct = default) { ReadId = request.HostedZoneId; return Task.FromResult(new ListResourceRecordSetsResponse { ResourceRecordSets = [] }); }
    }
    private sealed class FakeProvider : IDnsProvider
    {
        public string ProviderName { get; } = Guid.NewGuid().ToString();
        public IReadOnlyList<DnsRecord> Current { get; set; } = [A()];
        public bool FailAfterWriteOnce { get; set; }
        public bool FailBeforeWrite { get; init; }
        public Action? BeforeWrite { get; init; }
        public Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DomainSummary>>([new("example.com", ProviderName)]);
        public Task<DnsZone> GetZoneAsync(string domain, CancellationToken ct = default) => Task.FromResult(new DnsZone(domain, ProviderName, Current.Select(r => r.Clone()).ToArray(), DateTimeOffset.Now));
        public Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken ct = default)
        {
            BeforeWrite?.Invoke(); if (FailBeforeWrite) throw new InvalidOperationException("Write failed before mutation.");
            Current = records.Select(r => r.Clone()).ToArray();
            if (FailAfterWriteOnce) { FailAfterWriteOnce = false; throw new InvalidOperationException("Connection lost after mutation."); }
            return Task.CompletedTask;
        }
    }
    private sealed class MemoryStore : IZoneSnapshotStore
    {
        public Task SaveAsync(ZoneSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ZoneSnapshot>> GetRecentAsync(string domain, int count = 20, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ZoneSnapshot>>([]);
    }
}
