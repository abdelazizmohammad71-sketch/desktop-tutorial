namespace ZX0ai.Core.Models;

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZX0ai.Core.Skills;

/// <summary>Author of a transcript entry.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>
/// One entry in a conversation transcript. Provider-neutral: the OpenRouter
/// adapter maps this onto the OpenAI-compatible wire schema.
/// </summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private bool _isStreaming;

    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    // Not `required`: the XAML type-info generator emits a parameterless activator
    // for any type used as a DependencyProperty type, which required members forbid.
    public ChatRole Role { get; init; } = ChatRole.User;

    public string Content
    {
        get => _content;
        set => SetProperty(ref _content, value);
    }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Set when the message was produced by a team member rather than the tier itself.</summary>
    public string? AgentId { get; init; }

    /// <summary>Model slug that produced this message. Always rendered LTR.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Configured tier label safe for the primary transcript UI. The raw model remains
    /// available for diagnostics and team-member cards only.
    /// </summary>
    public string? TierDisplayName { get; init; }

    /// <summary>True while tokens are still streaming into <see cref="Content"/>.</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set => SetProperty(ref _isStreaming, value);
    }

    /// <summary>
    /// For <see cref="ChatRole.Tool"/> messages, the id of the call being answered.
    /// Providers reject a tool result that does not correlate to a request.
    /// </summary>
    public string? ToolCallId { get; init; }

    /// <summary>
    /// For assistant messages that requested tools. These are replayed on the next
    /// provider round before correlated <see cref="ChatRole.Tool"/> results.
    /// </summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
