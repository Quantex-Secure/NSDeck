using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Tests;

public sealed class CoreServicesTests
{
    [Fact]
    public void Diff_detects_add_update_and_delete()
    {
        var retained = Record("@", "A", "203.0.113.10");
        var updatedOriginal = Record("www", "CNAME", "example.com.");
        var deleted = Record("old", "A", "192.0.2.10");
        var updated = updatedOriginal.Clone();
        updated.Value = "app.example.com.";
        var added = Record("api", "A", "198.51.100.20");

        var changes = ZoneComparer.Diff([retained, updatedOriginal, deleted], [retained.Clone(), updated, added]);

        Assert.Contains(changes, change => change.Kind == ZoneChangeKind.Add && change.Record.Name == "api");
        Assert.Contains(changes, change => change.Kind == ZoneChangeKind.Update && change.Record.Name == "www");
        Assert.Contains(changes, change => change.Kind == ZoneChangeKind.Delete && change.Record.Name == "old");
    }

    [Fact]
    public void Fingerprint_is_order_independent()
    {
        var first = Record("@", "A", "203.0.113.10");
        var second = Record("www", "CNAME", "example.com.");
        Assert.Equal(ZoneComparer.Fingerprint([first, second]), ZoneComparer.Fingerprint([second, first]));
    }

    [Fact]
    public void Empty_zone_is_blocked()
    {
        var result = ZoneValidator.Validate([]);
        Assert.False(result.IsValid);
        Assert.Contains("empty", result.ErrorSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cname_cannot_share_a_name_with_other_records()
    {
        var result = ZoneValidator.Validate([
            Record("www", "CNAME", "example.com."),
            Record("www", "TXT", "verification")
        ]);
        Assert.False(result.IsValid);
        Assert.Contains("CNAME", result.ErrorSummary);
    }

    [Fact]
    public void Common_zone_is_valid()
    {
        var result = ZoneValidator.Validate([
            Record("@", "A", "203.0.113.10"),
            Record("www", "CNAME", "example.com."),
            Record("mail", "MX", "mail.example.com.", priority: 10),
            Record("_dmarc", "TXT", "v=DMARC1; p=reject")
        ]);
        Assert.True(result.IsValid, result.ErrorSummary);
    }

    [Fact]
    public void Shareable_diagnostics_remove_infrastructure_identity_and_sensitive_details()
    {
        var anonymizer = new DiagnosticAnonymizer();
        var first = anonymizer.Anonymize(new DnsAuditEntry(
            DateTimeOffset.UtcNow, "zone-apply", "Windows DNS — dns01.corp.example",
            "private.example", "failed", 2, "record-fingerprint", "Server dns01.corp.example rejected private.example."));
        var second = anonymizer.Anonymize(new DnsAuditEntry(
            DateTimeOffset.UtcNow, "zone-apply", "Windows DNS — dns01.corp.example",
            "private.example", "verified"));

        Assert.Equal("Windows DNS — server-001.example.invalid", first.Provider);
        Assert.Equal("zone-001.example.invalid", first.Domain);
        Assert.Equal(first.Provider, second.Provider);
        Assert.Equal(first.Domain, second.Domain);
        Assert.Null(first.Fingerprint);
        Assert.DoesNotContain("dns01", first.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private.example", first.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Product_rename_migration_copies_missing_local_data_without_overwriting()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"nsdeck-migration-{Guid.NewGuid():N}");
        var legacyRoot = Path.Combine(testRoot, "legacy");
        var currentRoot = Path.Combine(testRoot, "current");
        try
        {
            Directory.CreateDirectory(Path.Combine(legacyRoot, "snapshots"));
            Directory.CreateDirectory(currentRoot);
            File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), "legacy-settings");
            File.WriteAllText(Path.Combine(legacyRoot, "settings.json.tmp"), "partial-write");
            File.WriteAllText(Path.Combine(legacyRoot, "snapshots", "zone.json"), "snapshot");
            File.WriteAllText(Path.Combine(currentRoot, "settings.json"), "current-settings");

            AppDataMigration.MigrateMissingData(legacyRoot, currentRoot);

            Assert.Equal("current-settings", File.ReadAllText(Path.Combine(currentRoot, "settings.json")));
            Assert.Equal("snapshot", File.ReadAllText(Path.Combine(currentRoot, "snapshots", "zone.json")));
            Assert.False(File.Exists(Path.Combine(currentRoot, "settings.json.tmp")));
            Assert.True(File.Exists(Path.Combine(currentRoot, AppDataMigration.MigrationMarkerName)));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static DnsRecord Record(string name, string type, string value, int? priority = null) => new()
    {
        Name = name, Type = type, Value = value, TtlSeconds = 1800, Priority = priority
    };
}
