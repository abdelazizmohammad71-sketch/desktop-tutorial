namespace ZX0ai.Backend;

internal static class LocalBackendPolicy
{
    internal const int DefaultPort = 5179;

    internal static int ReadPort(string? value) =>
        int.TryParse(value, out var port) && port is >= 1024 and <= 65535
            ? port
            : DefaultPort;

    internal static bool IsAllowedHost(string? host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "127.0.0.1", StringComparison.Ordinal) ||
        string.Equals(host, "::1", StringComparison.Ordinal);

    internal static bool HasCredentialHeader(IHeaderDictionary headers) =>
        headers.ContainsKey("Authorization") ||
        headers.ContainsKey("Proxy-Authorization") ||
        headers.ContainsKey("X-Api-Key") ||
        headers.ContainsKey("Api-Key") ||
        headers.ContainsKey("X-OpenRouter-Key");
}
