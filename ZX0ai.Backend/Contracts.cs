using ZX0ai.Core.Models;

namespace ZX0ai.Backend;

public sealed record ApiError(string Code, string Message);

public sealed record HealthResponse(
    string Status,
    string Service,
    string Provider,
    bool ProviderConfigured,
    int TierCount,
    int RunnableTierCount,
    DateTimeOffset? CatalogFetchedAt,
    int CatalogModelCount,
    bool WorkspaceBound,
    bool WorkspaceAvailable);

public sealed record TierMemberResponse(
    string Role,
    string DisplayName,
    string RequestedSlug,
    string? ResolvedSlug,
    IReadOnlyList<string> FallbackSlugs,
    string EffortProfile,
    string Responsibility,
    ModelAvailability Availability,
    bool FallbackActive,
    bool Leader);

public sealed record TierResponse(
    string Key,
    string DisplayName,
    string Mode,
    string Protocol,
    bool Selected,
    bool Runnable,
    bool RequireAllMembersInAgentMode,
    int RelativeSpeed,
    int RelativeCost,
    string Speed,
    string? RequestedSlug,
    string? ResolvedSlug,
    ModelAvailability Availability,
    IReadOnlyList<TierMemberResponse> Members);

public sealed record ModelRefreshResponse(
    DateTimeOffset FetchedAt,
    int ModelCount,
    int RunnableTierCount,
    IReadOnlyList<TierResponse> Tiers);

public sealed record SkillResponse(
    string Name,
    string Description,
    bool Destructive,
    bool EnabledForCurrentWorkspace);

/// <summary>
/// Deliberately small input surface. Unknown JSON members are rejected globally, so
/// fields such as apiKey, baseUrl, systemPrompt or projectPath cannot be smuggled in.
/// </summary>
public sealed record ChatStreamRequest(
    string Tier,
    IReadOnlyList<ChatInputMessage> Messages,
    /// <summary>When true DXM plans and stops instead of acting.</summary>
    bool PlanOnly = false);

public sealed record ChatInputMessage(string Role, string Content);

public enum AgentRunStatus
{
    Running,
    Completed,
    Failed,
    Canceled,
}

public sealed record AgentTurnSnapshot(
    string Id,
    string AgentId,
    string AgentName,
    string Role,
    string Model,
    uint AccentArgb,
    string ReasoningSummary,
    string Content,
    AgentStatus Status,
    bool FinalAnswer,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Truncated);

public sealed record AgentRunSnapshot(
    string RunId,
    string Tier,
    string TierDisplayName,
    AgentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AgentTurnSnapshot> Turns,
    string Answer,
    bool AnswerTruncated,
    string? FailureCode);

internal sealed record RunStartedEvent(
    string RunId,
    string Tier,
    string TierDisplayName,
    string Mode);

internal sealed record TurnStartedEvent(
    string Id,
    string AgentId,
    string AgentName,
    string Role,
    string Model,
    uint AccentArgb,
    string ReasoningSummary,
    bool FinalAnswer);

internal sealed record TurnDeltaEvent(string TurnId, string Text);

internal sealed record TurnCompletedEvent(string TurnId, AgentStatus Status);

internal sealed record AnswerDeltaEvent(string Text);

internal sealed record UsageEvent(int PromptTokens, int CompletionTokens, int TotalTokens, decimal? Cost);

internal sealed record WarningEvent(string Message);

internal sealed record RunCompletedEvent(string RunId);

internal sealed record StreamErrorEvent(string Code, string Message);
