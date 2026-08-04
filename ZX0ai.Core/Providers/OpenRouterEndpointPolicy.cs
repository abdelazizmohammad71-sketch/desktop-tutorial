using ZX0ai.Core.Configuration;

namespace ZX0ai.Core.Providers;

/// <summary>
/// Pins credential-bearing calls to OpenRouter. A preference/config override can
/// never redirect the bearer token to another host.
/// </summary>
public static class OpenRouterEndpointPolicy
{
    public static Uri Build(OpenRouterOptions options, string relativePath)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var configured) ||
            configured.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(configured.Host, "openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
            (!configured.IsDefaultPort && configured.Port != 443) ||
            !configured.AbsolutePath.TrimEnd('/').Equals("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "OpenRouter BaseUrl must be https://openrouter.ai/api/v1.");
        }

        return new Uri(configured.ToString().TrimEnd('/') + "/" + relativePath.TrimStart('/'));
    }
}
