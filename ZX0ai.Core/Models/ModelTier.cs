namespace ZX0ai.Core.Models;

/// <summary>
/// A selectable tier (zxa-Light, zxa0-Pro, zxa0-Ultra-full-max...). Materialised
/// from configuration, never hardcoded.
/// </summary>
public sealed class ModelTier
{
    /// <summary>Config key, e.g. <c>zxa0-Pro</c>. Rendered LTR in the UI.</summary>
    public required string Key { get; init; }

    /// <summary>Label shown in the tier selector.</summary>
    public required string DisplayName { get; init; }

    public TeamMode Mode { get; init; } = TeamMode.Single;

    public TeamProtocol Protocol { get; init; } = TeamProtocol.Single;

    /// <summary>All configured members must contribute in Agent/Build mode.</summary>
    public bool RequireAllMembersInAgentMode { get; init; }

    /// <summary>Relative UI indicators, 1 (low) through 4 (high).</summary>
    public int RelativeSpeed { get; init; } = 2;

    public int RelativeCost { get; init; } = 2;

    public string Speed { get; init; } = "Standard";

    /// <summary>Model slug used when <see cref="Mode"/> is <see cref="TeamMode.Single"/>.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Model slug of the orchestrating leader when <see cref="Mode"/> is Team.</summary>
    public string? Leader { get; set; }

    /// <summary>Rich leader definition for config-driven team tiers.</summary>
    public TeamMember? LeaderMember { get; set; }

    /// <summary>Visual identity the app adopts while this tier is selected.</summary>
    public TierTheme Theme { get; init; } = TierTheme.Violet;

    /// <summary>Relative power, 1..4. Drives the tier icon.</summary>
    public int Level { get; init; } = 1;

    /// <summary>
    /// Environment variable holding this tier's credential, or null for the shared one.
    /// </summary>
    /// <remarks>
    /// The name, never the value. Keeping it a name is what lets a tier definition stay
    /// safe to commit.
    /// </remarks>
    public string? ApiKeyEnvironmentVariable { get; init; }

    public IReadOnlyList<TeamMember> Members { get; init; } = [];

    /// <summary>Leader first, followed by every non-leader member.</summary>
    public IReadOnlyList<TeamMember> AllMembers => LeaderMember is null
        ? Members
        : [LeaderMember, .. Members];

    public bool IsRunnable => !IsTeam || AllMembers.All(m =>
        m.Availability is ModelAvailability.Available or ModelAvailability.Fallback &&
        !string.IsNullOrWhiteSpace(m.ResolvedSlug));

    /// <summary>True when this tier resolves through the orchestrator rather than a single call.</summary>
    public bool IsTeam => Mode == TeamMode.Team;
}

/// <summary>
/// The app's visual identity. Tiers can re-skin the whole shell, so stepping up to
/// the most powerful tier is something the user sees, not just a dropdown value.
/// </summary>
public enum TierTheme
{
    /// <summary>The default violet identity.</summary>
    Violet,

    /// <summary>Ultra: red and ember, for the heaviest tier.</summary>
    Fire,
}

/// <summary>One member of a team tier: a role bound to a concrete model slug.</summary>
public sealed class TeamMember
{
    public required AgentRole Role { get; init; }

    /// <summary>Stable config role id, e.g. principal-engineer or ui-ux-designer.</summary>
    public string RoleId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Legacy initialiser used by tests and old configuration.</summary>
    public string Model { get; set; } = string.Empty;

    public string RequestedSlug { get; set; } = string.Empty;

    public IReadOnlyList<string> FallbackSlugs { get; set; } = [];

    /// <summary>The validated OpenRouter slug used for actual requests.</summary>
    public string ResolvedSlug { get; set; } = string.Empty;

    public string EffortProfile { get; init; } = "provider-default";

    public string Responsibility { get; init; } = string.Empty;

    public ModelAvailability Availability { get; set; } = ModelAvailability.Unknown;

    public bool IsFallbackActive => Availability == ModelAvailability.Fallback;

    public string EffectiveModel => !string.IsNullOrWhiteSpace(ResolvedSlug)
        ? ResolvedSlug
        : !string.IsNullOrWhiteSpace(RequestedSlug)
            ? RequestedSlug
            : Model;

    /// <summary>Optional override of the role's default system prompt.</summary>
    public string? SystemPrompt { get; init; }
}

public enum ModelAvailability
{
    Unknown,
    Available,
    Fallback,
    Unavailable,
}
