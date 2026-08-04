using ZX0ai.Core.Models;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

/// <summary>One streamed delta from a provider.</summary>
/// <param name="Kind">What the delta carries.</param>
/// <param name="Text">Token text for <see cref="ChatDeltaKind.Content"/>.</param>
/// <param name="Model">Model slug reported by the provider, when known.</param>
/// <param name="ToolCall">Set for <see cref="ChatDeltaKind.ToolCall"/>.</param>
public readonly record struct ChatDelta(
    ChatDeltaKind Kind,
    string Text,
    string? Model = null,
    ToolCall? ToolCall = null,
    ProviderUsage? Usage = null)
{
    public static ChatDelta Content(string text) => new(ChatDeltaKind.Content, text);

    public static ChatDelta Reasoning(string text) => new(ChatDeltaKind.Reasoning, text);

    public static ChatDelta Tool(ToolCall call) =>
        new(ChatDeltaKind.ToolCall, string.Empty, null, call);

    public static ChatDelta Done(string? model = null) => new(ChatDeltaKind.Done, string.Empty, model);

    public static ChatDelta UsageUpdate(ProviderUsage usage) =>
        new(ChatDeltaKind.Usage, string.Empty, Usage: usage);
}

public sealed record ProviderUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    decimal? Cost = null);

public enum ChatDeltaKind
{
    /// <summary>Assistant tokens to append to the current bubble.</summary>
    Content,

    /// <summary>Provider-side reasoning summary, when the model exposes one.</summary>
    Reasoning,

    /// <summary>A tool/skill invocation the caller must execute and feed back.</summary>
    ToolCall,

    /// <summary>Provider-reported token/cost totals, usually on the final SSE frame.</summary>
    Usage,

    /// <summary>Terminal marker for the stream.</summary>
    Done,
}

/// <summary>
/// A validated model request. Requested and resolved slugs stay distinct so fallback
/// use is explicit and auditable, while the app-level effort remains provider-neutral.
/// </summary>
public sealed record ModelInvocation(
    string RequestedSlug,
    string ResolvedSlug,
    string EffortProfile = "provider-default",
    string Speed = "Standard")
{
    public static ModelInvocation Direct(string slug) => new(slug, slug);
}

/// <summary>
/// Transport abstraction over a chat completion backend. OpenRouter is the shipped
/// implementation; a direct OpenAI or Anthropic adapter can replace it without any
/// UI change (Section 2).
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable id, e.g. <c>openrouter</c>.</summary>
    string Name { get; }

    /// <summary>False when no API key is configured; the UI degrades gracefully.</summary>
    bool IsConfigured { get; }

    /// <summary>Streams a completion for <paramref name="messages"/> using <paramref name="model"/>.</summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a validated invocation. Non-OpenRouter test/provider adapters can rely
    /// on this safe default; OpenRouter overrides it to adapt capabilities and effort.
    /// </summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(invocation.ResolvedSlug, messages, cancellationToken);

    /// <summary>Typed invocation with optional tools; unsupported providers omit tools.</summary>
    IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken = default) =>
        StreamAsync(invocation, messages, cancellationToken);
}
