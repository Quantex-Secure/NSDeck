using System.Text.Json;
using NSDeck.Core.Models;
using NSDeck.Providers.Windows;

namespace NSDeck.Tests;

public sealed class WindowsDnsProviderTests
{
    [Fact]
    public void Includes_the_persistent_windows_powershell_worker()
    {
        Assert.Contains(
            "NSDeck.WindowsDnsWorker.ps1",
            typeof(PowerShellJeaCommandRunner).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void Treats_windows_dns_as_internal_unless_public_checks_are_enabled()
    {
        var internalProvider = new WindowsDnsProvider(new WindowsDnsOptions("dns01"), new FakeRunner());
        var publicProvider = new WindowsDnsProvider(new WindowsDnsOptions("dns02", SupportsPublicDnsPropagation: true), new FakeRunner());

        Assert.False(internalProvider.SupportsPublicDnsPropagation);
        Assert.True(publicProvider.SupportsPublicDnsPropagation);
    }

    [Fact]
    public void Converts_clixml_access_denied_into_actionable_guidance()
    {
        const string error = """
            #< CLIXML
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><S S="Error">Connecting failed with the following error _x000D__x000A_</S><S S="Error">message : Access is denied._x000D__x000A_</S></Objs>
            """;

        var message = PowerShellJeaCommandRunner.FormatFailure("dns01.corp.example", "NSDeck.Dns", error);

        Assert.Contains("Windows denied access", message);
        Assert.Contains("fully sign out", message);
        Assert.DoesNotContain("CLIXML", message);
        Assert.DoesNotContain("_x000D_", message);
    }

    [Fact]
    public void Decodes_general_clixml_errors()
    {
        const string error = """
            #< CLIXML
            <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><S S="Error">The remote command failed._x000D__x000A_</S></Objs>
            """;

        var message = PowerShellJeaCommandRunner.FormatFailure("dns01", "NSDeck.Dns", error);

        Assert.Contains("The remote command failed.", message);
        Assert.DoesNotContain("CLIXML", message);
    }

    [Fact]
    public async Task Reads_zones_and_records_from_the_constrained_endpoint()
    {
        var runner = new FakeRunner
        {
            ZonesJson = """[{"Name":"corp.example"},{"Name":"lab.example"}]""",
            RecordsJson = """[{"Name":"www","Type":"A","Value":"192.0.2.25","TtlSeconds":300,"Priority":null},{"Name":"@","Type":"MX","Value":"mail.corp.example.","TtlSeconds":1800,"Priority":10}]"""
        };
        var provider = new WindowsDnsProvider(new WindowsDnsOptions("dns01.corp.example"), runner);

        var domains = await provider.GetDomainsAsync();
        var zone = await provider.GetZoneAsync("corp.example");

        Assert.Equal(2, domains.Count);
        Assert.Equal("Windows DNS — dns01.corp.example", domains[0].Provider);
        Assert.Equal("192.0.2.25", zone.Records.Single(record => record.Type == "A").Value);
        Assert.Equal(10, zone.Records.Single(record => record.Type == "MX").Priority);
        Assert.Equal("NSDeck.Dns", runner.LastEndpoint);
    }

    [Fact]
    public async Task Sends_only_normalized_record_data_to_the_jea_endpoint()
    {
        var runner = new FakeRunner();
        var provider = new WindowsDnsProvider(new WindowsDnsOptions("dns01", "NSDeck.Test"), runner);

        await provider.ReplaceZoneAsync("corp.example", [new DnsRecord
        {
            ProviderRecordId = "provider-only-value",
            Name = "mail",
            Type = "mx",
            Value = "mailhost.corp.example.",
            TtlSeconds = 900,
            Priority = 20
        }]);

        Assert.Equal(WindowsDnsOperation.ReplaceZone, runner.LastOperation);
        Assert.Equal("corp.example", runner.LastZone);
        Assert.Equal("NSDeck.Test", runner.LastEndpoint);
        using var document = JsonDocument.Parse(runner.LastRecordsJson!);
        var record = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal("MX", record.GetProperty("type").GetString());
        Assert.Equal(20, record.GetProperty("priority").GetInt32());
        Assert.False(record.TryGetProperty("providerRecordId", out _));
    }

    private sealed class FakeRunner : IWindowsDnsCommandRunner
    {
        public string ZonesJson { get; init; } = "[]";
        public string RecordsJson { get; init; } = "[]";
        public string? LastEndpoint { get; private set; }
        public WindowsDnsOperation LastOperation { get; private set; }
        public string? LastZone { get; private set; }
        public string? LastRecordsJson { get; private set; }

        public Task<string> InvokeAsync(string server, string endpointName, WindowsDnsOperation operation,
            string? zoneName = null, string? recordsJson = null, CancellationToken cancellationToken = default)
        {
            LastEndpoint = endpointName;
            LastOperation = operation;
            LastZone = zoneName;
            LastRecordsJson = recordsJson;
            return Task.FromResult(operation switch
            {
                WindowsDnsOperation.ListZones => ZonesJson,
                WindowsDnsOperation.ReadZone => RecordsJson,
                _ => "[{\"Added\":1,\"Removed\":0}]"
            });
        }
    }
}
