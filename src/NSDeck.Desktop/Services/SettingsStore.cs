using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NSDeck.Desktop.Services;

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        _settingsPath = Path.Combine(rootPath, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("protectedPayload", out var protectedPayload) && !string.IsNullOrWhiteSpace(protectedPayload.GetString()))
        {
            var bytes = Dpapi.Unprotect(Convert.FromBase64String(protectedPayload.GetString()!));
            return ApplyProductRenameDefaults(JsonSerializer.Deserialize<AppSettings>(bytes, JsonOptions) ?? new AppSettings());
        }

        // One-time migration from the original Namecheap-only settings format.
        var legacy = JsonSerializer.Deserialize<LegacyPersistedSettings>(json, JsonOptions);
        if (legacy is null) return new AppSettings();
        var apiKey = string.IsNullOrWhiteSpace(legacy.ProtectedApiKey)
            ? string.Empty
            : Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(legacy.ProtectedApiKey)));
        return new AppSettings
        {
            Namecheap = new NamecheapConnectionSettings
            {
                Enabled = !string.IsNullOrWhiteSpace(apiKey), ApiUser = legacy.ApiUser, UserName = legacy.UserName,
                ApiKey = apiKey, ClientIp = legacy.ClientIp, UseSandbox = legacy.UseSandbox
            }
        };
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var serialized = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        var persisted = new PersistedEnvelope(2, Convert.ToBase64String(Dpapi.Protect(serialized)));
        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, persisted, JsonOptions, cancellationToken);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static AppSettings ApplyProductRenameDefaults(AppSettings settings)
    {
        if (settings.WindowsDns.EndpointName.Equals("DomainDnsManager.Dns", StringComparison.OrdinalIgnoreCase))
            settings.WindowsDns.EndpointName = "NSDeck.Dns";
        return settings;
    }

    private sealed record PersistedEnvelope(int Version, string ProtectedPayload);
    private sealed record LegacyPersistedSettings(
        string ApiUser,
        string UserName,
        string ProtectedApiKey,
        string ClientIp,
        bool UseSandbox);

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] data) => Transform(data, protect: true);
        public static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

        private static byte[] Transform(byte[] data, bool protect)
        {
            var input = new DataBlob();
            var output = new DataBlob();
            try
            {
                input.Data = Marshal.AllocHGlobal(data.Length);
                input.Length = data.Length;
                Marshal.Copy(data, 0, input.Data, data.Length);

                var success = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);

                if (!success)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var transformed = new byte[output.Length];
                Marshal.Copy(output.Data, transformed, 0, output.Length);
                return transformed;
            }
            finally
            {
                if (input.Data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(input.Data);
                }

                if (output.Data != IntPtr.Zero)
                {
                    LocalFree(output.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
