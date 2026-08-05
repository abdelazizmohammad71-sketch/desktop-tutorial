using System.Text.Json;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Routing;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Skills;

/// <summary>Ambient state a skill may need while executing.</summary>
/// <param name="Agent">The agent that requested the call.</param>
/// <param name="Workspace">Immutable project and execution-policy binding.</param>
public sealed record AgentContext(Agent Agent, WorkspaceContext Workspace)
{
    public string WorkingDirectory => Workspace.WorkingDirectory ?? string.Empty;

    /// <summary>
    /// Per-turn delegation budget, threaded through the context so each conversation
    /// owns its own scope. Mutable because <c>delegate_task</c> spends from it.
    /// </summary>
    public TurnBudget DelegationBudget { get; set; }
}

/// <summary>
/// A capability an agent can invoke, exposed to models as a callable tool.
/// </summary>
/// <remarks>
/// Implement and register; discovery is automatic. Nothing else needs editing to add
/// a skill, which is the point — the set is meant to grow.
/// </remarks>
public interface ISkill
{
    /// <summary>Stable snake_case name the model calls, e.g. <c>fetch_url</c>.</summary>
    string Name { get; }

    /// <summary>What it does, phrased for the model.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the arguments object.</summary>
    JsonElement InputSchema { get; }

    /// <summary>
    /// True when this skill writes, deletes or executes. Destructive skills require
    /// leader approval under the constitution.
    /// </summary>
    bool IsDestructive => false;

    Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Holds every registered skill and enforces per-agent grants.</summary>
public interface ISkillRegistry
{
    IReadOnlyList<ISkill> All { get; }

    /// <summary>Tool definitions for the skills <paramref name="agent"/> may call.</summary>
    IReadOnlyList<ToolDefinition> ToolsFor(Agent agent);

    /// <summary>Clears run-scoped approvals before a new orchestration starts.</summary>
    void RevokeApprovals();

    /// <summary>
    /// Runs a call on behalf of an agent, enforcing grants and destructive-action
    /// gating. Returns a failed result rather than throwing.
    /// </summary>
    Task<SkillResult> ExecuteAsync(
        Agent agent,
        ToolCall call,
        CancellationToken cancellationToken = default);

    /// <summary>Raised for every attempted call, granted or refused. Feeds the command card.</summary>
    event EventHandler<SkillInvocation>? SkillInvoked;
}

/// <summary>An audit record of one skill call.</summary>
/// <param name="AgentId">Who called it.</param>
/// <param name="SkillName">What was called.</param>
/// <param name="ArgumentsJson">Arguments as the model wrote them.</param>
/// <param name="Result">Outcome, including refusals.</param>
/// <param name="Timestamp">When.</param>
public sealed record SkillInvocation(
    string AgentId,
    string SkillName,
    string ArgumentsJson,
    SkillResult Result,
    DateTimeOffset Timestamp);

/// <summary>Helper for building the small JSON Schemas skills declare.</summary>
public static class SchemaBuilder
{
    /// <summary>Object schema with the given properties, all of type string unless stated.</summary>
    public static JsonElement Object(params (string Name, string Type, string Description, bool Required)[] properties)
    {
        var props = string.Join(
            ",",
            properties.Select(p =>
                $"\"{p.Name}\":{{\"type\":\"{p.Type}\",\"description\":{JsonSerializer.Serialize(p.Description)}}}"));

        var required = string.Join(
            ",",
            properties.Where(p => p.Required).Select(p => $"\"{p.Name}\""));

        var json = $$"""
            {"type":"object","properties":{{{props}}},"required":[{{required}}]}
            """;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Reads a string property, or null when absent or of the wrong kind.</summary>
    public static string? GetString(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? GetInt(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
