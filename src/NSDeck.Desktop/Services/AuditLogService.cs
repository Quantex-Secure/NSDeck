using System.IO;
using System.Text.Json;
using NSDeck.Core.Models;

namespace NSDeck.Desktop.Services;

public sealed class AuditLogService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _logPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AuditLogService(string appRoot)
    {
        _logPath = Path.Combine(appRoot, "logs");
        Directory.CreateDirectory(_logPath);
    }

    public IReadOnlyList<string> LogFiles => Directory.Exists(_logPath)
        ? Directory.EnumerateFiles(_logPath, "audit-*.jsonl").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    public async Task WriteAsync(DnsAuditEntry entry, CancellationToken cancellationToken = default)
    {
        var safeEntry = entry with { Detail = Redact(entry.Detail) };
        var line = JsonSerializer.Serialize(safeEntry, JsonOptions) + Environment.NewLine;
        var path = Path.Combine(_logPath, $"audit-{entry.Timestamp:yyyyMMdd}.jsonl");
        var lockTaken = false;
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            lockTaken = true;
            await File.AppendAllTextAsync(path, line, cancellationToken);
        }
        catch
        {
            // Audit logging is best effort and must never turn a successful DNS operation into a reported failure.
        }
        finally
        {
            if (lockTaken) _writeLock.Release();
        }
    }

    private static string? Redact(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return detail;
        var redacted = detail;
        foreach (var marker in new[] { "ApiKey", "api_key", "token", "secret", "authorization" })
        {
            var index = redacted.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return "Sensitive provider detail was redacted from the diagnostic log.";
        }
        return redacted.Length > 2000 ? redacted[..2000] + "…" : redacted;
    }
}
