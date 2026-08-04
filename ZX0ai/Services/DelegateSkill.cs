using System.Text.Json;
using ZX0ai.Core.Skills;

namespace ZX0ai.Services;

/// <summary>
/// Lets the orchestrator hand a self-contained piece of work to a specialist.
/// </summary>
/// <remarks>
/// <para>
/// Delegation is a tool rather than a separate execution mode, and that choice carries
/// the whole architecture. Because a specialist is reached only through a tool call, its
/// output can only ever come back as a tool result: it cannot reach the transcript, it
/// cannot address the user, and it cannot see another specialist. "Agents talk only to
/// the orchestrator" stops being a rule anyone has to enforce and becomes the only thing
/// the plumbing permits.
/// </para>
/// <para>
/// It also means the orchestrator decides, turn by turn, with the request in front of
/// it — which is what the routing is supposed to do. <see cref="TaskClassifier"/> only
/// sets the ceiling, so a misread request wastes one call instead of six.
/// </para>
/// </remarks>
public sealed class DelegateSkill(AgentTeam team) : ISkill
{
    public string Name => "delegate_task";

    public string Description =>
        "Hand one self-contained piece of work to a specialist and get their result back. " +
        "Roles: planner, coder, designer, reviewer, security, performance. " +
        "Use only for large multi-part work — never for a question, a small edit or a chat turn. " +
        "The specialist cannot see the conversation or the files, so put everything it needs in " +
        "the task and context. You remain responsible for the final answer and for every file written.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("role", "string", "One of: planner, coder, designer, reviewer, security, performance.", true),
        ("task", "string", "The complete instruction for the specialist. Self-contained.", true),
        ("context", "string", "Code, decisions or constraints the specialist needs. Optional.", false));

    public async Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        _ = context;

        var role = arguments.GetString("role")?.Trim().ToLowerInvariant();
        var task = arguments.GetString("task");

        if (string.IsNullOrWhiteSpace(role) || !SpecialistRoles.All.Contains(role))
        {
            return SkillResult.Fail(
                $"Unknown role. Use one of: {string.Join(", ", SpecialistRoles.All)}.");
        }

        if (string.IsNullOrWhiteSpace(task))
        {
            return SkillResult.Fail("Provide the task for the specialist.");
        }

        var result = await team
            .RunAsync(role, task, arguments.GetString("context"), cancellationToken)
            .ConfigureAwait(true);

        return SkillResult.Ok(result, $"Consulted {role}");
    }
}
