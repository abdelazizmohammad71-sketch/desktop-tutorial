using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Security;

public enum CommandPolicyDecision
{
    Allow,
    Prompt,
    Block,
}

public sealed record CommandPolicyResult(CommandPolicyDecision Decision, string Reason);

public sealed record ActionApprovalRequest(
    string Title,
    string Detail,
    string AgentId,
    string SessionId);

public interface IActionApprovalService
{
    Task<bool> RequestAsync(
        ActionApprovalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed approval service used when no interactive host is attached.</summary>
public sealed class DenyActionApprovalService : IActionApprovalService
{
    public Task<bool> RequestAsync(
        ActionApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = cancellationToken;
        return Task.FromResult(false);
    }
}

public interface ICommandPolicy
{
    CommandPolicyResult Evaluate(string command, WorkspaceContext workspace);
}

/// <summary>
/// Exec-policy style allow/prompt/block layer. Workspace-write allows a deliberately
/// narrow local set and rejects path escapes/network. Dangerous commands always need
/// an explicit per-command confirmation, even under Full Access.
/// </summary>
public sealed class CommandPolicy : ICommandPolicy
{
    private static readonly string[] AlwaysBlocked =
    [
        "format ", "shutdown ", "bcdedit", "diskpart", "cipher /w",
        "reg delete", "remove-item -recurse", "rm -rf /",
    ];

    private static readonly string[] AlwaysPrompt =
    [
        " delete ", " del ", " remove ", "git clean", "git reset --hard",
        " install", " uninstall", " publish", " deploy", " push", "credential",
    ];

    public CommandPolicyResult Evaluate(string command, WorkspaceContext workspace)
    {
        var normalized = " " + command.Trim().ToLowerInvariant() + " ";

        if (!workspace.HasProject || !workspace.IsAvailable)
        {
            return Block("No available project is bound to this session.");
        }

        if (!workspace.Policy.CanRunCommands)
        {
            return Block("Command execution is disabled by the active sandbox.");
        }

        if (AlwaysBlocked.Any(normalized.Contains))
        {
            return Block("This command is blocked as a destructive system operation.");
        }

        if (AlwaysPrompt.Any(normalized.Contains))
        {
            return Prompt("This destructive or external action requires confirmation.");
        }

        if (workspace.Policy.Sandbox == SandboxMode.WorkspaceWrite)
        {
            if (ContainsPathEscape(command))
            {
                return Block("Workspace commands cannot use absolute or parent paths.");
            }

            if (!workspace.Policy.NetworkEnabled && LooksNetworked(normalized))
            {
                return Block("Network access is disabled in workspace-write mode.");
            }

            if (!IsRoutineWorkspaceCommand(normalized))
            {
                return Prompt(
                    "This is outside the routine workspace command set and requires Full Access.");
            }
        }

        if (workspace.Policy.Approval == ApprovalPolicy.Untrusted)
        {
            return Prompt("Untrusted mode requires confirmation for command execution.");
        }

        return new CommandPolicyResult(CommandPolicyDecision.Allow, "Allowed by policy.");
    }

    private static bool IsRoutineWorkspaceCommand(string command) =>
        command.StartsWith(" git status ", StringComparison.Ordinal) ||
        command.StartsWith(" git diff ", StringComparison.Ordinal) ||
        command.StartsWith(" git log ", StringComparison.Ordinal) ||
        command.StartsWith(" git show ", StringComparison.Ordinal) ||
        command.StartsWith(" rg ", StringComparison.Ordinal) ||
        command.StartsWith(" dotnet build ", StringComparison.Ordinal) && command.Contains(" --no-restore ") ||
        command.StartsWith(" dotnet test ", StringComparison.Ordinal) && command.Contains(" --no-restore ");

    private static bool LooksNetworked(string command) =>
        command.StartsWith(" curl ", StringComparison.Ordinal) ||
        command.StartsWith(" npm ", StringComparison.Ordinal) ||
        command.StartsWith(" npx ", StringComparison.Ordinal) ||
        command.Contains("http://", StringComparison.Ordinal) ||
        command.Contains("https://", StringComparison.Ordinal);

    private static bool ContainsPathEscape(string command) =>
        command.Contains("..", StringComparison.Ordinal) ||
        command.Contains(":\\", StringComparison.Ordinal) ||
        command.Contains(":/", StringComparison.Ordinal) ||
        command.TrimStart().StartsWith("/", StringComparison.Ordinal);

    private static CommandPolicyResult Block(string reason) =>
        new(CommandPolicyDecision.Block, reason);

    private static CommandPolicyResult Prompt(string reason) =>
        new(CommandPolicyDecision.Prompt, reason);
}
