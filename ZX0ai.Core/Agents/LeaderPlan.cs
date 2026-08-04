using System.Text;
using ZX0ai.Core.Governance;
using ZX0ai.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZX0ai.Core.Agents;

/// <summary>What the leader decided to do with a request.</summary>
public enum LeaderIntent
{
    /// <summary>The leader answers alone. No member is consulted.</summary>
    Direct,

    /// <summary>The leader assigns work to named roles and then synthesises.</summary>
    Delegate,
}

/// <summary>One unit of work the leader handed to a role.</summary>
/// <param name="Role">The role the leader chose.</param>
/// <param name="Task">What that role must produce.</param>
public sealed record LeaderAssignment(AgentRole Role, string Task);

/// <summary>
/// The leader's decision for a turn, parsed out of its first response.
/// </summary>
/// <remarks>
/// <para>
/// Every request reaches the leader alone. It decides whether the work needs anyone
/// else and, if so, exactly who and for what — nothing runs speculatively, and a
/// one-line question does not wake a whole team.
/// </para>
/// <para>
/// The decision travels in a fenced <c>dxm-plan</c> block so the leader can also write
/// ordinary prose around it. Parsing is deliberately forgiving: a missing or malformed
/// block means the leader gets treated as having planned nothing special, and the run
/// falls back to consulting the whole team rather than failing the turn.
/// </para>
/// </remarks>
public sealed record LeaderPlan(
    LeaderIntent Intent,
    IReadOnlyList<LeaderAssignment> Assignments,
    string? Summary,
    string? BrainNote,
    RiskAssessment Risk,
    string? RollbackPlan)
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Used when the leader produced no usable plan.</summary>
    public static LeaderPlan Unparsed { get; } =
        new(LeaderIntent.Delegate, [], null, null, RiskAssessment.Unclassified, null);

    /// <summary>True when at least one role was given real work.</summary>
    public bool HasAssignments => Assignments.Count > 0;

    /// <summary>
    /// Extracts the plan from a leader response, or <see cref="Unparsed"/> if there
    /// is nothing valid to extract.
    /// </summary>
    public static LeaderPlan Parse(string? response)
    {
        var json = ExtractBlock(response);
        if (json is null)
        {
            return Unparsed;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, ParseOptions);
        }
        catch (JsonException)
        {
            // A model wrote something that only looked like the block. Not an error
            // worth failing a turn over.
            return Unparsed;
        }

        if (payload is null)
        {
            return Unparsed;
        }

        var intent = string.Equals(payload.Mode, "direct", StringComparison.OrdinalIgnoreCase)
            ? LeaderIntent.Direct
            : LeaderIntent.Delegate;

        var assignments = new List<LeaderAssignment>();
        foreach (var item in payload.Assignments ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Task) ||
                !Enum.TryParse<AgentRole>(item.Role, ignoreCase: true, out var role))
            {
                continue;
            }

            assignments.Add(new LeaderAssignment(role, item.Task.Trim()));
        }

        // "Delegate, but to nobody" is a contradiction; read it as answering alone.
        if (intent == LeaderIntent.Delegate && assignments.Count == 0)
        {
            intent = LeaderIntent.Direct;
        }

        // The declared tier is a starting point, never the last word: the plan text is
        // scanned independently and the higher reading wins. See RiskClassifier.
        var scanned = string.Join(
            "\n",
            new[] { payload.Summary }
                .Concat(assignments.Select(a => a.Task))
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        var risk = RiskClassifier.Classify(scanned, RiskClassifier.ParseTier(payload.Risk));

        return new LeaderPlan(
            intent,
            assignments,
            Trimmed(payload.Summary),
            Trimmed(payload.Brain),
            risk,
            Trimmed(payload.Rollback));
    }

    /// <summary>
    /// Removes the machine-readable block from text that is about to be shown to a
    /// customer. The plan is internal routing; the prose around it is not.
    /// </summary>
    public static string StripBlock(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return string.Empty;
        }

        var (start, end) = FindBlock(response);
        if (start < 0)
        {
            return response.Trim();
        }

        var builder = new StringBuilder(response.Length);
        builder.Append(response, 0, start);
        builder.Append(response, end, response.Length - end);
        return builder.ToString().Trim();
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ExtractBlock(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return null;
        }

        var (start, end) = FindBlock(response);
        if (start < 0)
        {
            return null;
        }

        var body = response[start..end];
        var open = body.IndexOf('\n');
        var close = body.LastIndexOf("```", StringComparison.Ordinal);

        return open < 0 || close <= open ? null : body[(open + 1)..close].Trim();
    }

    /// <summary>Locates the fenced block, returning its outer bounds.</summary>
    private static (int Start, int End) FindBlock(string response)
    {
        const string Fence = "```dxm-plan";

        var start = response.IndexOf(Fence, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return (-1, -1);
        }

        var close = response.IndexOf("```", start + Fence.Length, StringComparison.Ordinal);
        if (close < 0)
        {
            // An unterminated fence swallows the rest of the response, which is the
            // safe reading: better to drop trailing text than to leak the block.
            return (start, response.Length);
        }

        return (start, Math.Min(response.Length, close + 3));
    }

    private sealed record Payload
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("brain")]
        public string? Brain { get; init; }

        [JsonPropertyName("risk")]
        public string? Risk { get; init; }

        [JsonPropertyName("rollback")]
        public string? Rollback { get; init; }

        [JsonPropertyName("assignments")]
        public IReadOnlyList<PayloadAssignment>? Assignments { get; init; }
    }

    private sealed record PayloadAssignment
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("task")]
        public string? Task { get; init; }
    }
}
