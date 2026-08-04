namespace ZX0ai.Core.Models;

/// <summary>Outcome of a single executed command or skill invocation.</summary>
public enum CommandStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    /// <summary>Blocked by the allow-list or refused at the confirmation prompt.</summary>
    Denied,
}

/// <summary>
/// One monospace line inside a CommandCard: the command that ran, its live
/// status and its streamed output. Always displayed LTR.
/// </summary>
public sealed class CommandStep
{
    public string Id { get; init; } = Guid.NewGuid().ToString("n");

    /// <summary>Arabic label describing intent, e.g. "إنشاء وتفعيل الشبكة".</summary>
    public string? Label { get; set; }

    /// <summary>The literal command line. English, LTR, monospace.</summary>
    public required string CommandLine { get; init; }

    public CommandStatus Status { get; set; } = CommandStatus.Pending;

    public string Output { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Agent that requested the command; every invocation is attributed.</summary>
    public string? RequestedByAgentId { get; init; }
}

/// <summary>A bullet in a CommandCard summary block, carrying its "+N" improvement count.</summary>
public sealed class CommandOutcome
{
    public required string Text { get; init; }

    public int ImprovementCount { get; init; }
}
