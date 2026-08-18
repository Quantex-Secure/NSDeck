using System.Text.Json;
using NSDeck.Core.Models;
using NSDeck.Core.Providers;
using NSDeck.Core.Services;

namespace NSDeck.Providers.Windows;

public sealed class WindowsDnsProvider : IDnsProvider, IDisposable
{
    private static readonly HashSet<string> SupportedRecordTypes =
        new(["A", "AAAA", "CNAME", "MX", "NS", "PTR", "SRV", "TXT"], StringComparer.OrdinalIgnoreCase);
    private readonly WindowsDnsOptions _options;
    private readonly IWindowsDnsCommandRunner _runner;
    private readonly IDisposable? _ownedRunner;

    public WindowsDnsProvider(WindowsDnsOptions options, IWindowsDnsCommandRunner? runner = null)
    {
        if (string.IsNullOrWhiteSpace(options.Server)) throw new ArgumentException("A Windows DNS server name is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.EndpointName)) throw new ArgumentException("A JEA endpoint name is required.", nameof(options));
        _options = options with { Server = options.Server.Trim(), EndpointName = options.EndpointName.Trim() };
        if (runner is null)
        {
            var ownedRunner = new PowerShellJeaCommandRunner();
            _runner = ownedRunner;
            _ownedRunner = ownedRunner;
        }
        else
        {
            _runner = runner;
        }
    }

    public string ProviderName => $"Windows DNS — {_options.Server}";
    public bool SupportsPublicDnsPropagation => _options.SupportsPublicDnsPropagation;

    public async Task<IReadOnlyList<DomainSummary>> GetDomainsAsync(CancellationToken cancellationToken = default)
    {
        var json = await _runner.InvokeAsync(_options.Server, _options.EndpointName, WindowsDnsOperation.ListZones,
            cancellationToken: cancellationToken);
        return ParseItems(json)
            .Select(item => GetString(item, "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new DomainSummary(name, ProviderName, IsUsingProviderDns: true))
            .ToArray();
    }

    public async Task<DnsZone> GetZoneAsync(string domain, CancellationToken cancellationToken = default)
    {
        var json = await _runner.InvokeAsync(_options.Server, _options.EndpointName, WindowsDnsOperation.ReadZone,
            domain, cancellationToken: cancellationToken);
        var records = ParseItems(json).Select(item => new DnsRecord
        {
            Name = GetString(item, "Name"),
            Type = GetString(item, "Type").ToUpperInvariant(),
            Value = GetString(item, "Value"),
            TtlSeconds = GetInt32(item, "TtlSeconds") ?? 1800,
            Priority = GetInt32(item, "Priority")
        }).ToArray();
        return new DnsZone(domain, ProviderName, records, DateTimeOffset.Now);
    }

    public async Task ReplaceZoneAsync(string domain, IReadOnlyList<DnsRecord> records, CancellationToken cancellationToken = default)
    {
        var validation = ZoneValidator.Validate(records);
        if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorSummary);
        var unsupported = records.Select(record => record.Type).Where(type => !SupportedRecordTypes.Contains(type))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidOperationException($"The constrained Windows DNS endpoint does not enable these record types: {string.Join(", ", unsupported)}. They were not sent to the server.");

        var payload = JsonSerializer.Serialize(records.Select(record => new
        {
            name = record.Name,
            type = record.Type.ToUpperInvariant(),
            value = record.Value,
            ttlSeconds = record.TtlSeconds,
            priority = record.Priority
        }));
        await _runner.InvokeAsync(_options.Server, _options.EndpointName, WindowsDnsOperation.ReplaceZone,
            domain, payload, cancellationToken);
    }

    private static IReadOnlyList<JsonElement> ParseItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(),
            JsonValueKind.Object => [document.RootElement.Clone()],
            JsonValueKind.Null => [],
            _ => throw new InvalidOperationException("The Windows DNS JEA endpoint returned an unexpected response.")
        };
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : string.Empty;

    private static int? GetInt32(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), out number) ? number : null;
    }

    public void Dispose()
    {
        _ownedRunner?.Dispose();
    }
}
