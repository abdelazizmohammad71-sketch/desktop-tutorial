namespace ZX0ai.Core.Services;

/// <summary>
/// Reads secrets from the process environment only.
/// </summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    public string? GetSecret(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } value
            ? value
            : null;

    public void SetSecret(string key, string secret) =>
        throw new NotSupportedException("Environment secret storage is read-only at runtime.");

    public void DeleteSecret(string key) =>
        throw new NotSupportedException("Environment secret storage is read-only at runtime.");
}
