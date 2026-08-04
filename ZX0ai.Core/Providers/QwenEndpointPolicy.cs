using ZX0ai.Core.Configuration;

namespace ZX0ai.Core.Providers;

public static class QwenEndpointPolicy
{
    public static Uri Build(QwenOptions options, string relativePath)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var configured) ||
            configured.Scheme != Uri.UriSchemeHttps ||
            (!configured.Host.EndsWith(".qwen.ai", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(configured.Host, "qwen.ai", StringComparison.OrdinalIgnoreCase)) ||
            !configured.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Qwen BaseUrl must be https://api.qwen.ai/v1 or another https://*.qwen.ai/v1 endpoint.");
        }

        return new Uri(configured.ToString().TrimEnd('/') + "/" + relativePath.TrimStart('/'));
    }
}
