namespace ZX0ai.Core.Agents;

/// <summary>Per-turn switches for a run.</summary>
/// <param name="PlanOnly">
/// When true the leader plans and stops. Nothing is delegated, no skill runs, and the
/// plan itself becomes the answer.
/// </param>
/// <remarks>
/// Plan mode exists so a user can see the intended approach before anything touches
/// their files. It is enforced in the orchestrator rather than by prompting, because a
/// prompt asking a model not to act is a request, not a guarantee.
/// </remarks>
public sealed record AgentRunOptions(bool PlanOnly = false, bool ApprovalGranted = false)
{
    /// <summary>Normal execution.</summary>
    public static AgentRunOptions Default { get; } = new();

    /// <summary>Plan and stop.</summary>
    public static AgentRunOptions Plan { get; } = new(PlanOnly: true);

    /// <summary>Execution the human has explicitly approved at the gate.</summary>
    public static AgentRunOptions Approved { get; } = new(ApprovalGranted: true);
}
