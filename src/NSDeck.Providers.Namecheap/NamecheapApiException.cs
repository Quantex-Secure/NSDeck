namespace NSDeck.Providers.Namecheap;

public sealed class NamecheapApiException(string message, string? errorCode = null)
    : Exception(message)
{
    public string? ErrorCode { get; } = errorCode;
}
