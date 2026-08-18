using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NSDeck.Core.Models;
using NSDeck.Core.Services;

namespace NSDeck.Desktop.Services;

public static class DiagnosticsExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);

    public static async Task ExportAsync(
        string destination,
        AuditLogService auditLog,
        IReadOnlyCollection<string> enabledProviders,
        CancellationToken cancellationToken = default)
    {
        var anonymizer = new DiagnosticAnonymizer();
        var shareableLogs = new List<ShareableAuditLog>();
        var skippedAuditLines = 0;
        foreach (var path in auditLog.LogFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<DnsAuditEntry>();
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<DnsAuditEntry>(line, JsonLineOptions);
                    if (entry is not null) entries.Add(anonymizer.Anonymize(entry));
                }
                catch (JsonException)
                {
                    skippedAuditLines++;
                }
            }
            shareableLogs.Add(new ShareableAuditLog(Path.GetFileName(path), entries));
        }

        await using var output = File.Create(destination);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);

        var summaryEntry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
        await using (var stream = summaryEntry.Open())
        {
            await JsonSerializer.SerializeAsync(stream, new
            {
                generatedAt = DateTimeOffset.Now,
                application = "NSDeck",
                version = typeof(DiagnosticsExporter).Assembly.GetName().Version?.ToString(),
                operatingSystem = Environment.OSVersion.VersionString,
                dotnet = Environment.Version.ToString(),
                providers = enabledProviders.Select(anonymizer.AnonymizeProvider).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
                skippedAuditLines,
                note = "This report is intended for public issue sharing. Credentials, provider settings, real zone names, Windows DNS server names, record fingerprints, and provider error details are intentionally excluded."
            }, JsonOptions, cancellationToken);
        }

        foreach (var log in shareableLogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveEntry = archive.CreateEntry($"logs/{log.FileName}", CompressionLevel.Optimal);
            await using var target = archiveEntry.Open();
            await using var writer = new StreamWriter(target, new UTF8Encoding(false));
            foreach (var entry in log.Entries)
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonLineOptions).AsMemory(), cancellationToken);
        }
    }

    private sealed record ShareableAuditLog(string FileName, IReadOnlyList<DnsAuditEntry> Entries);
}
