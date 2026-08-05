using Microsoft.Extensions.Logging;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;
using ZX0ai.Core.Routing;
using ZX0ai.Core.Skills;

namespace ZX0ai.Services;

/// <summary>One tool call and what came of it, for the execution panel.</summary>
public sealed class ToolRun(string name, string arguments)
{
    public string Name { get; } = name;

    /// <summary>Arguments as the model wrote them, trimmed for display.</summary>
    public string Arguments { get; } = arguments;

    public bool? Success { get; set; }

    public string? Summary { get; set; }
}

/// <summary>
/// Exposes the skill set to the model and runs what it asks for.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not <c>SkillRegistry</c>. That type exists to enforce per-agent skill
/// grants and leader sign-off across a team of agents; this product runs one Leader that
/// holds every skill, so all of that machinery would evaluate to "yes" on every call
/// while still requiring the whole agent-team graph to be constructed. What is worth
/// keeping from it — the workspace availability check and the audit trail — is here.
/// </para>
/// <para>
/// The safety boundary is not in this class. Paths are confined by
/// <c>WorkspacePathGuard</c> inside the file skills, and commands are screened by
/// <c>CommandPolicy</c> inside <c>RunCommandSkill</c>. This type decides only which
/// skills are offered, based on what the current policy permits.
/// </para>
/// </remarks>
public sealed class ToolRunner(
    IEnumerable<ISkill> skills,
    AgentWorkspace workspace,
    ILogger<ToolRunner> logger)
{
    private readonly Dictionary<string, ISkill> _skills =
        skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);

    /// <summary>
    /// The Leader, as the skills need to see it.
    /// </summary>
    /// <remarks>
    /// A single agent holding every skill. The destructive-action gate in the skill
    /// layer waives leader approval for the leader itself, which is correct here: there
    /// is no one above it to ask. The user-facing gate is the command confirmation.
    /// </remarks>
    private static readonly Agent Leader = new()
    {
        Id = "leader",
        Name = "Leader",
        Role = AgentRole.Leader,
        Model = string.Empty,
        SystemPrompt = string.Empty,
        HasAllSkills = true,
    };

    /// <summary>Raised when a call starts, and again when it finishes.</summary>
    public event EventHandler<ToolRun>? ToolStarted;

    public event EventHandler<ToolRun>? ToolFinished;

    /// <summary>True when the workspace permits any tool at all.</summary>
    public bool HasTools => Tools.Count > 0;

    /// <summary>
    /// Specialist calls allowed this turn. Set once per user message.
    /// </summary>
    /// <remarks>
    /// Held here rather than inside the skill because it decides whether the tool is
    /// advertised at all. A model shown a tool it is not permitted to use will try it
    /// and then have to be refused, and a refusal it has to interpret is worse than an
    /// option it never saw.
    /// </remarks>
    public int HelperBudget { get; set; }

    /// <summary>
    /// Per-turn delegation budget, threaded into each <see cref="AgentContext"/> so
    /// <c>delegate_task</c> can spend from it without shared mutable state on the team.
    /// </summary>
    public TurnBudget DelegationBudget { get; set; }

    /// <summary>
    /// The tools to advertise for the current workspace.
    /// </summary>
    /// <remarks>
    /// Filtered by policy rather than offered and refused later. A model told it can
    /// write files will keep trying to write files; withholding the tool is a clearer
    /// signal than a refusal it has to interpret, and it keeps the refusal path for
    /// genuine boundary violations.
    /// </remarks>
    public IReadOnlyList<ToolDefinition> Tools
    {
        get
        {
            var policy = workspace.Current.Policy;
            var bound = workspace.Current.HasProject;

            return
            [
                .. _skills.Values
                    .Where(skill => skill.Name switch
                    {
                        "read_file" or "list_files" => bound && policy.CanReadFiles,
                        "write_file" => bound && policy.CanWriteFiles,
                        "run_command" => bound && policy.CanRunCommands,
                        "web_search" or "fetch_url" => policy.CanUseNetwork,

                        // Withheld entirely when the turn has no budget, so a small
                        // request is never even offered the option of a team.
                        "delegate_task" => HelperBudget > 0,
                        _ => true,
                    })
                    .Select(skill => new ToolDefinition(skill.Name, skill.Description, skill.InputSchema)),
            ];
        }
    }

    /// <summary>Runs one call. Returns a failed result rather than throwing.</summary>
    public async Task<SkillResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var run = new ToolRun(call.Name, Describe(call.ArgumentsJson));
        ToolStarted?.Invoke(this, run);

        var result = await RunGuardedAsync(call, cancellationToken).ConfigureAwait(true);

        run.Success = result.Success;
        run.Summary = result.Summary ?? (result.Success ? "done" : result.Content);
        ToolFinished?.Invoke(this, run);

        logger.Log(
            result.Success ? LogLevel.Information : LogLevel.Warning,
            "Tool {Tool}: {Outcome}",
            call.Name,
            result.Success ? "ok" : result.Content);

        return result;
    }

    private async Task<SkillResult> RunGuardedAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!_skills.TryGetValue(call.Name, out var skill))
        {
            return SkillResult.Fail($"No tool named '{call.Name}' exists.");
        }

        if (!Tools.Any(tool => tool.Name == call.Name))
        {
            return SkillResult.Fail(
                $"'{call.Name}' is not available: no folder is bound, or the current mode does not permit it.");
        }

        try
        {
            var context = new AgentContext(Leader, workspace.Current)
            {
                DelegationBudget = DelegationBudget,
            };
            var result = await skill
                .ExecuteAsync(call.ParseArguments(), context, cancellationToken)
                .ConfigureAwait(true);

            // Read back the spent budget so the next call in the same turn sees the update.
            DelegationBudget = context.DelegationBudget;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A misbehaving tool must not take down the turn.
            logger.LogError(ex, "Tool {Tool} threw.", call.Name);
            return SkillResult.Fail($"'{call.Name}' failed: {ex.Message}");
        }
    }

    /// <summary>A one-line form of the arguments, for the panel.</summary>
    private static string Describe(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        var flat = argumentsJson
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        return flat.Length <= 120 ? flat : flat[..117] + "…";
    }
}
