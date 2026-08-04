using System.Text;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Routing;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Services;

namespace ZX0ai.Services;

/// <summary>The specialists the orchestrator can hand work to.</summary>
/// <remarks>
/// Named by what they do, never by the model behind them. Which member answers a role is
/// a routing decision that changes with the selected capability and with what is
/// available upstream.
/// </remarks>
public static class SpecialistRoles
{
    public const string Planner = "planner";
    public const string Coder = "coder";
    public const string Designer = "designer";
    public const string Reviewer = "reviewer";
    public const string Security = "security";
    public const string Performance = "performance";

    public static readonly string[] All =
        [Planner, Coder, Designer, Reviewer, Security, Performance];
}

/// <summary>
/// Runs one specialist and hands its result back to the orchestrator.
/// </summary>
/// <remarks>
/// <para>
/// A specialist has no voice. It receives a task, produces text, and that text becomes a
/// tool result the orchestrator reads — it never reaches the transcript, and no
/// specialist ever sees another's output except as context the orchestrator chose to
/// pass on. That is what keeps the product a single assistant: the topology makes any
/// other arrangement impossible rather than merely discouraged.
/// </para>
/// <para>
/// Specialists do not get tools. They think and write; the orchestrator owns every
/// change to the disk. Two agents writing files in one turn is how a project ends up
/// half-built in two incompatible directions, and it also means every write stays
/// attributable to the one agent the user is talking to.
/// </para>
/// </remarks>
public sealed class AgentTeam(
    IChatProvider provider,
    IConfigService config,
    ILogger<AgentTeam> logger)
{
    /// <summary>A specialist's reply is read by a model, so it can be long. Not unbounded.</summary>
    private const int MaxReplyCharacters = 24000;

    private int _spent;
    private int _budget;

    /// <summary>Specialist calls still permitted this turn.</summary>
    public int Remaining => Math.Max(0, _budget - _spent);

    /// <summary>Opens a turn with a fresh budget. Called once per user message.</summary>
    public void BeginTurn(TaskSize size)
    {
        _budget = TaskClassifier.HelperBudget(size);
        _spent = 0;

        logger.LogInformation(
            "Turn classified {Size}; {Budget} specialist call(s) permitted.",
            size,
            _budget);
    }

    /// <summary>Runs a specialist. Returns its text, or an explanation of why it did not run.</summary>
    public async Task<string> RunAsync(
        string role,
        string task,
        string? context,
        CancellationToken cancellationToken)
    {
        if (Remaining == 0)
        {
            return _budget == 0
                ? "Delegation is not available for a request this size. Do it yourself."
                : "The specialist budget for this turn is spent. Finish the work yourself.";
        }

        var tier = config.ActiveTier;
        var member = SelectMember(tier, role);
        if (member is null || string.IsNullOrWhiteSpace(member.EffectiveModel))
        {
            return $"No specialist is configured for '{role}'. Do this part yourself.";
        }

        _spent++;

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.System, Content = SystemPromptFor(role) },
            new()
            {
                Role = ChatRole.User,
                Content = string.IsNullOrWhiteSpace(context)
                    ? task
                    : $"{task}\n\n--- context from the orchestrator ---\n{context}",
            },
        };

        var invocation = new ModelInvocation(
            member.RequestedSlug,
            member.EffectiveModel,
            member.EffortProfile);

        var reply = new StringBuilder();

        try
        {
            await foreach (var delta in provider
                .StreamAsync(invocation, messages, cancellationToken)
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
            {
                if (delta.Kind == ChatDeltaKind.Content)
                {
                    reply.Append(delta.Text);

                    if (reply.Length > MaxReplyCharacters)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ChatProviderException or HttpRequestException or IOException)
        {
            // A specialist failing is a setback, not the end of the turn. The
            // orchestrator is told plainly and can do the work itself.
            logger.LogWarning(ex, "Specialist {Role} failed.", role);
            return $"The '{role}' specialist could not complete: {ex.Message}. Do this part yourself.";
        }

        return reply.Length == 0
            ? $"The '{role}' specialist returned nothing. Do this part yourself."
            : reply.ToString();
    }

    /// <summary>
    /// Picks the configured member best suited to a role.
    /// </summary>
    /// <remarks>
    /// Falls through to the leader rather than failing. A capability with a small team
    /// should still be able to delegate — running the leader twice with a specialist's
    /// instructions is worth more than refusing, because the second pass is focused on
    /// one concern where the first was spread across all of them.
    /// </remarks>
    private static TeamMember? SelectMember(ModelTier tier, string role)
    {
        var members = tier.AllMembers;

        TeamMember? ByRole(params string[] roleIds) => members.FirstOrDefault(member =>
            roleIds.Any(id => member.RoleId.Contains(id, StringComparison.OrdinalIgnoreCase)));

        var match = role switch
        {
            SpecialistRoles.Coder => ByRole("principal-engineer", "engineer", "coder"),
            SpecialistRoles.Designer => ByRole("designer", "ui-ux"),
            SpecialistRoles.Security => ByRole("security"),
            SpecialistRoles.Planner => ByRole("problem-solver", "principal-engineer"),
            SpecialistRoles.Reviewer => ByRole("security", "problem-solver", "principal-engineer"),
            SpecialistRoles.Performance => ByRole("problem-solver", "principal-engineer"),
            _ => null,
        };

        return match ?? tier.LeaderMember ?? members.FirstOrDefault();
    }

    /// <summary>
    /// What a specialist is told about itself.
    /// </summary>
    /// <remarks>
    /// Every prompt ends by forbidding conversation. A specialist that opens with "Sure,
    /// I'd be happy to help" is writing to a user it cannot see, and that text would be
    /// pasted into the orchestrator's context as though it were work.
    /// </remarks>
    private static string SystemPromptFor(string role)
    {
        var duty = role switch
        {
            SpecialistRoles.Planner =>
                "You are a planner. Produce the architecture and an ordered, concrete plan: " +
                "the files to create, what each contains, and the sequence to build them in.",

            SpecialistRoles.Coder =>
                "You are a senior engineer. Write complete, working code. " +
                "No placeholders, no TODOs, no elisions. Give each file's full contents with its path.",

            SpecialistRoles.Designer =>
                "You are a product designer. Decide layout, hierarchy, spacing, states and " +
                "typography. Be specific enough to implement from: real values, not adjectives.",

            SpecialistRoles.Reviewer =>
                "You are a reviewer. Find defects, and say exactly where each one is and how " +
                "to fix it. Report only real problems — an empty review is a valid review.",

            SpecialistRoles.Security =>
                "You are a security reviewer. Find vulnerabilities: injection, path traversal, " +
                "unsafe deserialisation, leaked secrets, missing authorisation. Rank by severity " +
                "and give the fix for each.",

            SpecialistRoles.Performance =>
                "You are a performance engineer. Find the costs that matter: hot paths, N+1 " +
                "queries, needless allocation, blocking calls, layout thrash. Quantify where you can.",

            _ => "You are a specialist assistant. Complete the task you are given.",
        };

        return duty +
            "\n\nYou are one part of a system and you are not talking to a person. " +
            "Return only the work itself — no greeting, no preamble, no offer to help further, " +
            "no questions. Your output is read by another engineer and used directly.";
    }
}
