namespace ZX0ai.Core.Models;

/// <summary>How a tier resolves a user turn.</summary>
public enum TeamMode
{
    /// <summary>Exactly one model answers directly.</summary>
    Single,

    /// <summary>A leader plus members collaborate under a <see cref="TeamProtocol"/>.</summary>
    Team,
}

/// <summary>
/// Collaboration protocol used when <see cref="TeamMode.Team"/> is active (Section 9).
/// </summary>
public enum TeamProtocol
{
    /// <summary>No orchestration; passthrough to a single model.</summary>
    Single,

    /// <summary>Leader plans, assigns subtasks by role, collects, synthesises.</summary>
    LeaderDelegate,

    /// <summary>Members answer in parallel, critique on the bus, leader arbitrates.</summary>
    DebateThenSynthesize,

    /// <summary>Planner to Coder to Reviewer, with the leader gating each stage.</summary>
    Pipeline,
}
