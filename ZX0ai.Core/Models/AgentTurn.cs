namespace ZX0ai.Core.Models;

/// <summary>Live status of an agent, surfaced in the team roster and the orb tint.</summary>
public enum AgentStatus
{
    Idle,
    Thinking,
    Speaking,
    Done,
    Failed,
}

/// <summary>
/// A single contribution to the team transcript. Rendered as an AgentTurnCard:
/// who spoke, under which model and role, a short reasoning summary, then content.
/// </summary>
public sealed class AgentTurn
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    // Not `required`: the XAML type-info generator emits a parameterless activator
    // for any type used as a DependencyProperty type, which required members forbid.
    public string AgentId { get; init; } = string.Empty;

    public string AgentName { get; init; } = string.Empty;

    public AgentRole Role { get; init; } = AgentRole.Leader;

    /// <summary>Model slug — rendered as an LTR island inside the RTL layout.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>ARGB accent assigned to this agent; the orb tints to it while speaking.</summary>
    public uint AccentArgb { get; init; }

    /// <summary>Brief, user-facing rationale. Never the full chain of thought.</summary>
    public string ReasoningSummary { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public AgentStatus Status { get; set; } = AgentStatus.Idle;

    /// <summary>True for the leader's arbitrated final answer, which is styled distinctly.</summary>
    public bool IsFinalAnswer { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public TimeSpan Latency { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int TotalTokens { get; set; }

    public decimal? EstimatedCost { get; set; }

    public int RetryCount { get; set; }

    public int ToolCallCount { get; set; }

    public bool IsFallbackActive { get; init; }
}
