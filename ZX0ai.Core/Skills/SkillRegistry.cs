using Microsoft.Extensions.Logging;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Skills;

/// <inheritdoc cref="ISkillRegistry" />
public sealed class SkillRegistry(
    IEnumerable<ISkill> skills,
    Constitution constitution,
    ILogger<SkillRegistry> logger,
    IProjectWorkspaceService? workspaceService = null) : ISkillRegistry
{
    private readonly Dictionary<string, ISkill> _skills =
        skills.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>Agents whose destructive calls the leader has signed off.</summary>
    private readonly HashSet<string> _destructiveApprovals = [];

    public IReadOnlyList<ISkill> All => [.. _skills.Values];

    public event EventHandler<SkillInvocation>? SkillInvoked;

    /// <summary>
    /// Grants the leader's approval for <paramref name="agentId"/> to run destructive
    /// skills for the remainder of the run.
    /// </summary>
    public void ApproveDestructive(string agentId) => _destructiveApprovals.Add(agentId);

    public void RevokeApprovals() => _destructiveApprovals.Clear();

    public IReadOnlyList<ToolDefinition> ToolsFor(Agent agent) =>
    [
        .. _skills.Values
            .Where(skill => IsGranted(agent, skill.Name) && IsAvailableInWorkspace(skill.Name))
            .Select(skill => new ToolDefinition(skill.Name, skill.Description, skill.InputSchema)),
    ];

    public async Task<SkillResult> ExecuteAsync(
        Agent agent,
        ToolCall call,
        CancellationToken cancellationToken = default)
    {
        var result = await RunGuardedAsync(agent, call, cancellationToken).ConfigureAwait(false);

        // Every attempt is audited, including refusals — constitution rule 5.
        var invocation = new SkillInvocation(
            agent.Id,
            call.Name,
            call.ArgumentsJson,
            result,
            DateTimeOffset.Now);

        logger.Log(
            result.Success ? LogLevel.Information : LogLevel.Warning,
            "Skill {Skill} by {Agent}: {Outcome}",
            call.Name,
            agent.Id,
            result.Success ? "ok" : result.Content);

        SkillInvoked?.Invoke(this, invocation);
        return result;
    }

    private async Task<SkillResult> RunGuardedAsync(
        Agent agent,
        ToolCall call,
        CancellationToken cancellationToken)
    {
        if (!_skills.TryGetValue(call.Name, out var skill))
        {
            return SkillResult.Fail($"No skill named '{call.Name}' is registered.");
        }

        if (!IsGranted(agent, skill.Name))
        {
            return SkillResult.Fail(
                $"The {agent.Role} role is not granted '{skill.Name}'.");
        }

        if (skill.IsDestructive &&
            constitution.RequireLeaderApprovalForDestructiveSkills &&
            !agent.IsLeader &&
            !_destructiveApprovals.Contains(agent.Id))
        {
            return SkillResult.Fail(
                $"'{skill.Name}' is destructive and needs Leader approval before it can run.");
        }

        try
        {
            var workspace = workspaceService?.CurrentWorkspace ??
                WorkspaceContext.WithoutProject("unbound-run");

            return await skill
                .ExecuteAsync(call.ParseArguments(), new AgentContext(agent, workspace), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A misbehaving skill must not take down the run.
            logger.LogError(ex, "Skill {Skill} threw.", skill.Name);
            return SkillResult.Fail($"'{skill.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>Unrestricted access is explicit; an empty list is safely no access.</summary>
    private static bool IsGranted(Agent agent, string skillName) =>
        agent.HasAllSkills || agent.GrantedSkills.Contains(skillName);

    private bool IsAvailableInWorkspace(string skillName)
    {
        if (workspaceService is null)
        {
            // Unit tests and transport-only hosts may provide their own execution
            // context; role-grant tests should remain independent of app state.
            return true;
        }

        var workspace = workspaceService.CurrentWorkspace;
        return skillName switch
        {
            "read_file" => workspace.HasProject && workspace.Policy.CanReadFiles,
            "write_file" => workspace.HasProject && workspace.Policy.CanWriteFiles,
            "run_command" => workspace.HasProject && workspace.Policy.CanRunCommands,
            "web_search" or "fetch_url" => workspace.Policy.CanUseNetwork,
            _ => true,
        };
    }
}
