using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NSDeck.Desktop.Services;

public static class AuthenticodeVerifier
{
    public static async Task<bool> VerifyAsync(string path, string trustedThumbprint, CancellationToken cancellationToken = default)
    {
        trustedThumbprint = trustedThumbprint.Replace(" ", "");
        if (!Regex.IsMatch(trustedThumbprint, "\\A[0-9a-fA-F]{40}\\z")) return false;
        // Literal PowerShell quoting prevents path contents from becoming executable code.
        var script = "$s = Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''") + "'; if ($s.Status -eq 'Valid' -and $s.SignerCertificate.Thumbprint -eq '" + trustedThumbprint + "') { exit 0 }; exit 1";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not verify the update signature.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); await Task.WhenAll(stdout, stderr); return process.ExitCode == 0; }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }
}
