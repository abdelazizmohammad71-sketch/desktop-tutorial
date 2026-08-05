using ZX0ai.Core.Models;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

/// <summary>
/// Routes each request to the provider declared by the active tier, falling back to the
/// global provider when a tier does not override it.
/// </summary>
/// <remarks>
/// <para>
/// Provider selection is per-tier, not global. A single-tier capability such as
/// <c>zax-v2</c> declares <c>provider: qwen</c> and every request for it goes straight to
/// the Qwen backend — never to OpenRouter, never to a fallback. Team tiers declare
/// <c>openrouter</c> (or inherit the global default) and stay on that path.
/// </para>
/// <para>
/// The selection is read live from <see cref="IConfigService.ActiveTier"/> on every call
/// rather than cached, so switching tiers mid-session takes effect on the next request
/// without re-reading configuration.
/// </para>
/// </remarks>
public sealed class ConfiguredChatProvider(
    IConfigService config,
    OpenRouterProvider openRouterProvider,
    QwenProvider qwenProvider) : IChatProvider
{
    private IChatProvider SelectedProvider
    {
        get
        {
            var tierProvider = config.ActiveTier.Provider;
            return tierProvider switch
            {
                "qwen" => qwenProvider,
                _ => openRouterProvider,
            };
        }
    }

    public string Name => SelectedProvider.Name;

    public bool IsConfigured => SelectedProvider.IsConfigured;

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        SelectedProvider.StreamAsync(model, messages, cancellationToken);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        SelectedProvider.StreamAsync(invocation, messages, cancellationToken);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken = default) =>
        SelectedProvider.StreamAsync(invocation, messages, tools, cancellationToken);
}
