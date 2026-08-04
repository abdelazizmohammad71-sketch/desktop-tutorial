namespace ZX0ai.Core.Services;

/// <summary>
/// Abstract source of secrets such as API keys.
/// </summary>
public interface ISecretStore
{
    string? GetSecret(string key);

    void SetSecret(string key, string secret);

    void DeleteSecret(string key);
}
