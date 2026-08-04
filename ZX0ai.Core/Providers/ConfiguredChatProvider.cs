using ZX0ai.Core.Models;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

public sealed class ConfiguredChatProvider(
    IConfigService config,
    OpenRouterProvider openRouterProvider,
    QwenProvider qwenProvider) : IChatProvider
{
    private IChatProvider SelectedProvider => config.Options.Provider?.Trim().ToLowerInvariant() switch
    {
        "qwen" => qwenProvider,
        _ => openRouterProvider,
    };

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
