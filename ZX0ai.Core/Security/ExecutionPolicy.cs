namespace ZX0ai.Core.Security;

/// <summary>Filesystem/process boundary applied to one chat session.</summary>
public enum SandboxMode
{
    ReadOnly,
    WorkspaceWrite,
    FullAccess,
}

/// <summary>When user approval may be requested for an action.</summary>
public enum ApprovalPolicy
{
    Untrusted,
    OnRequest,
    Never,
}

/// <summary>
/// Immutable execution authority. Network is deliberately off in workspace-write;
/// full access is effective only after an explicit user confirmation.
/// </summary>
public sealed record ExecutionPolicy(
    SandboxMode Sandbox,
    ApprovalPolicy Approval,
    bool NetworkEnabled,
    bool FullAccessConfirmed = false)
{
    /// <summary>
    /// The fail-closed baseline, used for sessions with no bound project.
    /// </summary>
    /// <remarks>
    /// Approval is <see cref="ApprovalPolicy.Untrusted"/>, not
    /// <see cref="ApprovalPolicy.OnRequest"/>. "On request" means the agent may act
    /// unprompted inside its sandbox and only asks when it steps outside — but a
    /// session with no project has no sandbox to be inside, so there is no boundary
    /// that would trigger the prompt. Granting it here would silently make the
    /// least-privileged session the least-supervised one.
    /// </remarks>
    public static ExecutionPolicy ReadOnly { get; } = new(
        SandboxMode.ReadOnly,
        ApprovalPolicy.Untrusted,
        NetworkEnabled: false);

    public static ExecutionPolicy WorkspaceDefault { get; } = new(
        SandboxMode.WorkspaceWrite,
        ApprovalPolicy.OnRequest,
        NetworkEnabled: false);

    public bool CanReadFiles => true;

    public bool CanWriteFiles =>
        Sandbox is SandboxMode.WorkspaceWrite ||
        Sandbox is SandboxMode.FullAccess && FullAccessConfirmed;

    /// <summary>
    /// Workspace-write permits only the command broker's narrow routine-command set;
    /// unrestricted commands still require explicitly confirmed full access.
    /// </summary>
    public bool CanRunCommands => Sandbox == SandboxMode.WorkspaceWrite ||
                                  Sandbox == SandboxMode.FullAccess && FullAccessConfirmed;

    public bool CanUseNetwork => NetworkEnabled &&
        (Sandbox != SandboxMode.FullAccess || FullAccessConfirmed);
}
