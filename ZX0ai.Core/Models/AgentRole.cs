namespace ZX0ai.Core.Models;

/// <summary>
/// Role an agent plays inside a team tier. Determines the system prompt it is
/// seeded with and the subset of skills it is granted (Section 10).
/// </summary>
public enum AgentRole
{
    /// <summary>External user message on the team bus; never an executable agent.</summary>
    User,

    /// <summary>Plans, delegates, arbitrates and synthesises. Final authority.</summary>
    Leader,

    /// <summary>Decomposes the task into ordered, verifiable subtasks.</summary>
    Planner,

    /// <summary>Writes and edits code. Granted read/write/run skills.</summary>
    Coder,

    /// <summary>Audits output for correctness. Read-only skills.</summary>
    Reviewer,

    /// <summary>Gathers external facts. Granted web/fetch skills.</summary>
    Researcher,

    /// <summary>Argues the opposing case during debate protocols.</summary>
    Critic,

    /// <summary>Diagnoses failures and validates proposed fixes.</summary>
    ProblemSolver,

    /// <summary>Owns interface structure, interaction states, and visual quality.</summary>
    Designer,

    /// <summary>Implements delegated work and verifies it against runtime evidence.</summary>
    Builder,
}
