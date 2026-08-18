using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NSDeck.Providers.Windows;

public sealed class PowerShellJeaCommandRunner : IWindowsDnsCommandRunner, IDisposable
{
    private const string WorkerResourceName = "NSDeck.WindowsDnsWorker.ps1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _worker;
    private Task<string>? _standardErrorTask;
    private bool _disposed;

    public async Task<string> InvokeAsync(
        string server,
        string endpointName,
        WindowsDnsOperation operation,
        string? zoneName = null,
        string? recordsJson = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var request = new WorkerRequest(
            Guid.NewGuid().ToString("N"),
            server,
            endpointName,
            operation.ToString(),
            zoneName,
            recordsJson is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(recordsJson)));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureWorkerStarted();
            var process = _worker!;
            var requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await process.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);

            var responseJson = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                throw new InvalidOperationException(GetWorkerExitMessage());
            }

            var response = JsonSerializer.Deserialize<WorkerResponse>(responseJson, JsonOptions)
                ?? throw new InvalidOperationException("The Windows DNS worker returned an empty response.");
            if (!string.Equals(response.Id, request.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The Windows DNS worker returned an unexpected response identifier.");
            }
            if (!response.Ok)
            {
                throw new InvalidOperationException(FormatFailure(server, endpointName, response.Error));
            }

            return response.Payload.Trim();
        }
        catch (OperationCanceledException)
        {
            ResetWorker();
            throw;
        }
        catch (IOException exception)
        {
            ResetWorker();
            throw new InvalidOperationException("The persistent Windows PowerShell DNS worker stopped unexpectedly. Retry the request to reconnect.", exception);
        }
        catch (JsonException exception)
        {
            ResetWorker();
            throw new InvalidOperationException("The persistent Windows PowerShell DNS worker returned an invalid response. Retry the request to reconnect.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureWorkerStarted()
    {
        if (_worker is { HasExited: false }) return;
        ResetWorker();

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(executable))
        {
            throw new PlatformNotSupportedException("Windows PowerShell 5.1 is required for Windows DNS JEA connections.");
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(LoadWorkerScript()));
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        try
        {
            process.Start();
            process.StandardInput.AutoFlush = true;
            _worker = process;
            _standardErrorTask = process.StandardError.ReadToEndAsync();
        }
        catch (Exception exception)
        {
            process.Dispose();
            throw new InvalidOperationException("Windows PowerShell could not be started for the Windows DNS connection.", exception);
        }
    }

    private static string LoadWorkerScript()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(WorkerResourceName)
            ?? throw new InvalidOperationException("The embedded Windows DNS worker script was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private string GetWorkerExitMessage()
    {
        var detail = _standardErrorTask is { IsCompletedSuccessfully: true }
            ? NormalizePowerShellError(_standardErrorTask.Result)
            : string.Empty;
        return string.IsNullOrWhiteSpace(detail)
            ? "The persistent Windows PowerShell DNS worker stopped unexpectedly. Retry the request to reconnect."
            : $"The persistent Windows PowerShell DNS worker stopped unexpectedly. {detail}";
    }

    internal static string FormatFailure(string server, string endpointName, string rawError)
    {
        var detail = NormalizePowerShellError(rawError);
        if (detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
            return $"Windows denied access to JEA endpoint {endpointName} on {server}. " +
                   $"NSDeck is running as {identity}. Verify that this account belongs to the endpoint's operator group. " +
                   "If the account was added recently, fully sign out of Windows and sign back in so NSDeck receives a new security token.";
        }

        if (detail.Length > 1200) detail = detail[..1200] + "…";
        return $"Could not complete the Windows DNS request on {server} through JEA endpoint {endpointName}. {detail}".Trim();
    }

    internal static string NormalizePowerShellError(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError)) return "Windows PowerShell returned no error details.";

        var detail = rawError;
        var xmlStart = rawError.IndexOf("<Objs", StringComparison.Ordinal);
        if (xmlStart >= 0)
        {
            try
            {
                var document = XDocument.Parse(rawError[xmlStart..]);
                var messages = document.Descendants()
                    .Where(element => element.Name.LocalName == "S" &&
                                      string.Equals((string?)element.Attribute("S"), "Error", StringComparison.OrdinalIgnoreCase))
                    .Select(element => element.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value));
                detail = string.Join(' ', messages);
            }
            catch
            {
                // Fall back to the original text when PowerShell returns incomplete CLIXML.
            }
        }

        detail = Regex.Replace(detail, "_x([0-9A-Fa-f]{4})_", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
        detail = detail.Replace("#< CLIXML", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(detail, @"\s+", " ").Trim();
    }

    private void ResetWorker()
    {
        var process = _worker;
        _worker = null;
        _standardErrorTask = null;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(1500)) process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The worker is already stopping or inaccessible.
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Wait();
        try
        {
            ResetWorker();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed record WorkerRequest(
        string Id,
        string Server,
        string EndpointName,
        string Operation,
        string? ZoneName,
        string? RecordsJsonBase64);

    private sealed record WorkerResponse(
        string Id,
        bool Ok,
        string Payload,
        string Error);
}
