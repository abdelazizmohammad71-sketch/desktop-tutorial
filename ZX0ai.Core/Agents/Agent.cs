using ZX0ai.Core.Models;

namespace ZX0ai.Core.Agents;

/// <summary>
/// A model bound to a role, a system prompt and a set of granted skills.
/// </summary>
public sealed class Agent
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AgentRole Role { get; init; }

    /// <summary>Model slug. Always rendered LTR.</summary>
    public required string Model { get; init; }

    public string RequestedModel { get; init; } = string.Empty;

    public string EffortProfile { get; init; } = "provider-default";

    public string Speed { get; init; } = "Standard";

    public string Responsibility { get; init; } = string.Empty;

    public bool IsFallbackActive => !string.Equals(
        RequestedModel,
        Model,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>Role instructions, already merged with the constitution.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>Skill names this agent may call.</summary>
    public IReadOnlyList<string> GrantedSkills { get; init; } = [];

    /// <summary>Explicit unrestricted grant; an empty list now means no skills.</summary>
    public bool HasAllSkills { get; init; }

    /// <summary>Per-agent accent; the orb tints to it while this agent speaks.</summary>
    public uint AccentArgb { get; init; }

    public bool IsLeader => Role == AgentRole.Leader;
}

/// <summary>
/// Builds agents from a tier, seeding role prompts, skill grants and accents.
/// </summary>
public static class AgentFactory
{
    // Matches the per-agent accents in Themes/Tokens.xaml.
    private static readonly Dictionary<AgentRole, uint> Accents = new()
    {
        [AgentRole.User] = 0xFF8F94A8,
        [AgentRole.Leader] = 0xFF7B5CFF,
        [AgentRole.Planner] = 0xFF54E8FF,
        [AgentRole.Coder] = 0xFF3DDC84,
        [AgentRole.Reviewer] = 0xFFFFC46B,
        [AgentRole.Researcher] = 0xFFD46BFF,
        [AgentRole.Critic] = 0xFFFF6B6B,
        [AgentRole.ProblemSolver] = 0xFFFF8A5B,
        [AgentRole.Designer] = 0xFFF06CFF,
        [AgentRole.Builder] = 0xFF47D7B0,
    };

    /// <summary>Skill grants per role, per Section 10.</summary>
    private static readonly Dictionary<AgentRole, string[]> Grants = new()
    {
        [AgentRole.User] = [],
        // Leader gets everything; an empty grant list is treated as "all".
        [AgentRole.Leader] = [],
        [AgentRole.Planner] = ["web_search", "fetch_url", "read_file"],
        [AgentRole.Coder] = ["read_file", "write_file", "run_command", "render_preview"],
        [AgentRole.Reviewer] = ["read_file"],
        [AgentRole.Researcher] = ["web_search", "fetch_url"],
        [AgentRole.Critic] = ["read_file"],
        [AgentRole.ProblemSolver] = ["read_file", "run_command"],
        [AgentRole.Designer] = ["read_file", "render_preview"],
        [AgentRole.Builder] = ["read_file", "write_file", "run_command", "render_preview"],
    };

    public static uint AccentFor(AgentRole role) =>
        Accents.TryGetValue(role, out var accent) ? accent : 0xFF7B5CFF;

    public static IReadOnlyList<string> GrantsFor(AgentRole role) =>
        Grants.TryGetValue(role, out var grants) ? grants : [];

    /// <summary>Creates the leader plus every member of <paramref name="tier"/>.</summary>
    public static IReadOnlyList<Agent> BuildTeam(ModelTier tier, Constitution constitution)
    {
        var agents = new List<Agent>();

        agents.Add(tier.LeaderMember is { } configuredLeader
            ? Create(configuredLeader, constitution, tier)
            : Create(AgentRole.Leader, tier.Leader ?? tier.Model ?? "unknown", null, constitution, tier));

        agents.AddRange(tier.Members.Select(member =>
            Create(member, constitution, tier)));

        return agents;
    }

    public static Agent Create(
        AgentRole role,
        string model,
        string? systemPromptOverride,
        Constitution constitution,
        ModelTier tier)
    {
        var instructions = systemPromptOverride ?? DefaultPromptFor(role, tier);

        return new Agent
        {
            Id = $"{role}".ToLowerInvariant(),
            Name = DisplayNameFor(role),
            Role = role,
            Model = model,
            RequestedModel = model,
            Speed = tier.Speed,
            AccentArgb = AccentFor(role),
            GrantedSkills = GrantsFor(role),
            HasAllSkills = role == AgentRole.Leader,
            SystemPrompt =
                $"""
                {constitution.Text}

                ---

                {instructions}
                """,
        };
    }

    private static Agent Create(TeamMember member, Constitution constitution, ModelTier tier)
    {
        if (!string.IsNullOrWhiteSpace(member.RequestedSlug) &&
            (member.Availability is not (ModelAvailability.Available or ModelAvailability.Fallback) ||
             string.IsNullOrWhiteSpace(member.ResolvedSlug)))
        {
            throw new InvalidOperationException(
                $"Configured model '{member.RequestedSlug}' has not been validated by OpenRouter.");
        }

        var roleInstructions = member.SystemPrompt ?? DefaultPromptFor(member.Role, tier);
        var responsibility = string.IsNullOrWhiteSpace(member.Responsibility)
            ? string.Empty
            : $"\n\nYour configured responsibility:\n{member.Responsibility}";

        return new Agent
        {
            Id = string.IsNullOrWhiteSpace(member.RoleId)
                ? member.Role.ToString().ToLowerInvariant()
                : member.RoleId.ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(member.DisplayName)
                ? DisplayNameFor(member.Role)
                : member.DisplayName,
            Role = member.Role,
            Model = member.EffectiveModel,
            RequestedModel = string.IsNullOrWhiteSpace(member.RequestedSlug)
                ? member.EffectiveModel
                : member.RequestedSlug,
            EffortProfile = member.EffortProfile,
            Speed = tier.Speed,
            Responsibility = member.Responsibility,
            AccentArgb = AccentFor(member.Role),
            GrantedSkills = GrantsFor(member.Role),
            HasAllSkills = member.Role == AgentRole.Leader,
            SystemPrompt =
                $"""
                {constitution.Text}

                ---

                {roleInstructions}{responsibility}
                """,
        };
    }

    private static string DisplayNameFor(AgentRole role) => role switch
    {
        AgentRole.Leader => "Leader",
        AgentRole.User => "User",
        AgentRole.Planner => "Planner",
        AgentRole.Coder => "Coder",
        AgentRole.Reviewer => "Reviewer",
        AgentRole.Researcher => "Researcher",
        AgentRole.Critic => "Critic",
        AgentRole.ProblemSolver => "Problem Solver",
        AgentRole.Designer => "Designer",
        AgentRole.Builder => "Builder",
        _ => role.ToString(),
    };

    private static string DefaultPromptFor(AgentRole role, ModelTier tier)
    {
        var roster = tier.Members.Count == 0
            ? "none"
            : string.Join(", ", tier.Members.Select(m => $"{m.Role} ({m.Model})"));

        return role switch
        {
            AgentRole.User => "# Your role: User\n\nThis is not an executable agent role.",

            AgentRole.Leader =>
                $"""
                # Your role: Leader

                You command this team and hold final authority. Your members are: {roster}.

                Your job:
                - Decide what the task actually requires. Do not over-decompose a simple
                  question into subtasks; answer it yourself if delegation adds nothing.
                - When you delegate, give each member a specific, self-contained subtask.
                - Read the bus. Arbitrate disagreements explicitly and say why.
                - Approve or refuse destructive skill calls.
                - Produce the final answer yourself. Synthesise; do not paste member
                  output verbatim, and do not describe the process unless asked.
                """,

            AgentRole.Planner =>
                """
                # Your role: Planner

                Decompose the task into ordered, verifiable steps. Each step states what
                is done and how anyone would know it succeeded. No implementation, no
                code. Be concise; a plan longer than the work is a failed plan.
                """,

            AgentRole.Coder =>
                """
                # Your role: Coder

                Write and edit code. Produce complete, runnable code with no placeholders
                or elisions. Match the conventions of any surrounding code you are shown.
                State assumptions in one line before the code, not after it.
                """,

            AgentRole.Reviewer =>
                """
                # Your role: Reviewer

                Audit for correctness, then for clarity. Report only defects you can
                justify: quote the specific line or claim and say what breaks and when.
                Do not rewrite the work; that is the Coder's job. If it is sound, say so
                plainly rather than inventing objections.
                """,

            AgentRole.Researcher =>
                """
                # Your role: Researcher

                Gather external facts using your skills. Cite what you retrieved. Separate
                what you verified from what you inferred. If a fact could not be
                retrieved, say so rather than guessing.
                """,

            AgentRole.Critic =>
                """
                # Your role: Critic

                Argue the strongest opposing case: what would make this answer wrong, and
                under what conditions. Attack the reasoning, not the author. If you cannot
                find a real weakness, say that instead of manufacturing one.
                """,

            AgentRole.ProblemSolver =>
                """
                # Your role: Problem Solver

                Reproduce or trace the failure, identify the root cause, and propose a
                concrete fix with verification evidence. Do not guess when evidence can
                be collected, and do not claim a fix passed unless you ran its check.
                """,

            AgentRole.Designer =>
                """
                # Your role: UI/UX Designer

                Own information hierarchy, interaction states, accessibility, and visual
                consistency. Ground recommendations in the current product and return
                implementation-ready decisions rather than decorative prose.
                """,

            AgentRole.Builder =>
                """
                # Your role: Autonomous Builder

                Implement the delegated deliverable end to end. Inspect the repository,
                make scoped changes, run proportionate verification, and report concrete
                files and runtime evidence. Challenge work that would regress behavior
                or violate the project policy.
                """,

            _ => "# Your role: Assistant\n\nAnswer helpfully and concisely.",
        };
    }
}
