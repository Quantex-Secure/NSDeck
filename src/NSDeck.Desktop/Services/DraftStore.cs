using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSDeck.Core.Models;

namespace NSDeck.Desktop.Services;

public sealed record ZoneDraft(string Domain, string AccountId, string? ZoneId, string OriginalFingerprint, IReadOnlyList<DnsRecord> Records);

public sealed class DraftStore(string root)
{
    public void Save(ZoneDraft draft)
    {
        var path = PathFor(draft.Domain, draft.AccountId, draft.ZoneId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, SettingsStore.Protect(JsonSerializer.SerializeToUtf8Bytes(draft)));
        File.Move(temporary, path, true);
    }
    public ZoneDraft? Load(string domain, string account, string? zone)
    {
        var path = PathFor(domain, account, zone);
        return !File.Exists(path) ? null : JsonSerializer.Deserialize<ZoneDraft>(SettingsStore.Unprotect(File.ReadAllBytes(path)));
    }
    public void Delete(string domain, string account, string? zone) => File.Delete(PathFor(domain, account, zone));
    private string PathFor(string domain, string account, string? zone) => Path.Combine(root, "drafts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { account, zone, domain.ToLowerInvariant() })))) + ".bin");
}
