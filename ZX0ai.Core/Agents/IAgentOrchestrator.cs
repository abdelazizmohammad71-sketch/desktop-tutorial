using ZX0ai.Core.Composition;
using ZX0ai.Core.Models;

namespace ZX0ai.Core.Agents;

/// <summary>What an orchestration update carries.</summary>
public enum OrchestrationUpdateKind
{
    /// <summary>An agent has taken the floor. <see cref="OrchestrationUpdate.Turn"/> is set.</summary>
    TurnStarted,

    /// <summary>Tokens for the agent currently speaking.</summary>
    TurnDelta,

    /// <summary>That agent has finished.</summary>
    TurnCompleted,

    /// <summary>Tokens of the leader's synthesised answer to the user.</summary>
    FinalAnswer,

    /// <summary>A recoverable problem worth surfacing without aborting the run.</summary>
    Warning,
}

/// <param name="Kind">What this update means.</param>
/// <param name="Turn">The turn it concerns, for turn-scoped kinds.</param>
/// <param name="Text">Token text, for delta kinds.</param>
public readonly record struct OrchestrationUpdate(
    OrchestrationUpdateKind Kind,
    AgentTurn? Turn = null,
    string Text = "")
{
    public static OrchestrationUpdate Started(AgentTurn turn) =>
        new(OrchestrationUpdateKind.TurnStarted, turn);

    public static OrchestrationUpdate Delta(AgentTurn turn, string text) =>
        new(OrchestrationUpdateKind.TurnDelta, turn, text);

    public static OrchestrationUpdate Completed(AgentTurn turn) =>
        new(OrchestrationUpdateKind.TurnCompleted, turn);

    public static OrchestrationUpdate Final(string text) =>
        new(OrchestrationUpdateKind.FinalAnswer, null, text);

    public static OrchestrationUpdate Warning(string text) =>
        new(OrchestrationUpdateKind.Warning, null, text);
}

/// <summary>
/// Runs a team tier to a final answer, streaming the live transcript.
/// </summary>
/// <remarks>
/// The UI depends only on this interface, so orchestration can move into
/// ZX0ai.Backend later without a view change (Section 15).
/// </remarks>
public interface IAgentOrchestrator
{
    /// <summary>The team currently assembled, for the roster panel.</summary>
    IReadOnlyList<Agent> CurrentTeam { get; }

    /// <summary>Streams the run. Yields turns, deltas and finally the leader's answer.</summary>
    /// <param name="context">
    /// Project-scoped inputs for this run: AGENTS.md instructions, resolved
    /// configuration layers and any SKILL.md package the task matched. Optional so a
    /// project-less session still runs; when supplied, every agent is seeded with it.
    /// </param>
    /// <param name="options">
    /// Per-turn switches, most importantly plan mode. Optional; omitting it runs
    /// normally.
    /// </param>
    IAsyncEnumerable<OrchestrationUpdate> RunAsync(
        ModelTier tier,
        IReadOnlyList<ChatMessage> history,
        ProjectTaskContext? context = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default);
}
