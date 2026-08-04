namespace ZX0ai.Core.Governance;

/// <summary>How much damage a task can do if it goes wrong.</summary>
/// <remarks>
/// Exactly one tier per task, assigned during planning. When two tiers could apply, the
/// higher one wins — the cost of over-classifying is one approval prompt, and the cost
/// of under-classifying is an unreviewed change to something that mattered.
/// </remarks>
public enum RiskTier
{
    /// <summary>Fully reversible, no sensitive data, no external effect.</summary>
    Low,

    /// <summary>Reversible, but touches shared code others depend on.</summary>
    Medium,

    /// <summary>Production, auth, external integrations, or data that matters if corrupted.</summary>
    High,

    /// <summary>Irreversible, or affects money, compliance standing, or a broad blast radius.</summary>
    Critical,
}

/// <summary>A tier, and the reason it was assigned.</summary>
/// <param name="Tier">The assigned tier.</param>
/// <param name="Reason">One line on what makes this risky. Shown at the approval gate.</param>
public sealed record RiskAssessment(RiskTier Tier, string Reason)
{
    /// <summary>True when the work cannot be dispatched without explicit human approval.</summary>
    public bool RequiresApproval => Tier is RiskTier.High or RiskTier.Critical;

    /// <summary>True when a documented rollback plan is mandatory before dispatch.</summary>
    public bool RequiresRollbackPlan => Tier is RiskTier.Critical;

    /// <summary>The default when nothing has been classified yet.</summary>
    public static RiskAssessment Unclassified { get; } =
        new(RiskTier.Low, "No risk factors identified.");
}

/// <summary>
/// Assigns a risk tier to a task.
/// </summary>
/// <remarks>
/// <para>
/// The leader declares a tier in its plan, but that declaration is not trusted on its
/// own: a model that wants to get on with the work has every incentive to call a
/// production change "Low". So the text is independently scanned for the markers that
/// define the higher tiers, and the <b>higher</b> of the two readings is used.
/// </para>
/// <para>
/// The scan is deliberately broad and will over-classify. That is the correct direction
/// to be wrong in — a needless approval prompt costs the user a click, while a missed
/// one costs them the thing the gate existed to protect.
/// </para>
/// </remarks>
public static class RiskClassifier
{
    private static readonly (string Marker, string Reason)[] CriticalMarkers =
    [
        ("drop table", "Drops a database table."),
        ("drop database", "Drops a database."),
        ("truncate", "Truncates stored data."),
        ("rm -rf", "Recursively deletes files."),
        ("delete from", "Deletes stored rows."),
        ("force push", "Rewrites published history."),
        ("push --force", "Rewrites published history."),
        ("deploy to production", "Deploys to production."),
        ("production deploy", "Deploys to production."),
        ("rotate key", "Rotates a credential."),
        ("rotate secret", "Rotates a credential."),
        ("payment", "Touches payment logic."),
        ("billing", "Touches billing logic."),
        ("gdpr", "Affects regulatory compliance."),
        ("hipaa", "Affects regulatory compliance."),
        ("pci-dss", "Affects regulatory compliance."),
        ("soc 2", "Affects regulatory compliance."),
    ];

    private static readonly (string Marker, string Reason)[] HighMarkers =
    [
        ("production", "Touches production."),
        ("prod ", "Touches production."),
        ("auth", "Touches authentication or authorization."),
        ("login", "Touches authentication."),
        ("password", "Handles credentials."),
        ("credential", "Handles credentials."),
        ("api key", "Handles credentials."),
        ("secret", "Handles secrets."),
        ("token", "Handles tokens."),
        ("permission", "Changes access control."),
        ("access control", "Changes access control."),
        ("customer data", "Touches customer data."),
        ("personal data", "Touches personal data."),
        ("pii", "Touches personal data."),
        ("ci/cd", "Alters the delivery pipeline."),
        ("pipeline", "Alters the delivery pipeline."),
        ("migration", "Changes stored data."),
        ("schema", "Changes the data schema."),
        ("third-party", "Adds an external integration."),
        ("integration", "Adds an external integration."),
        ("webhook", "Adds an external callback."),
    ];

    private static readonly (string Marker, string Reason)[] MediumMarkers =
    [
        ("endpoint", "Adds or changes a shared endpoint."),
        ("api", "Changes a shared interface."),
        ("dependency", "Adds a dependency."),
        ("package", "Adds a dependency."),
        ("config", "Changes configuration."),
        ("refactor", "Changes shared code."),
    ];

    /// <summary>
    /// Classifies a task, taking the higher of the declared tier and the scanned tier.
    /// </summary>
    /// <param name="text">The task description to scan.</param>
    /// <param name="declared">The tier the leader claimed, if it declared one.</param>
    public static RiskAssessment Classify(string? text, RiskTier? declared = null)
    {
        var scanned = Scan(text);
        var tier = declared is { } claimed && claimed > scanned.Tier
            ? new RiskAssessment(claimed, "Classified by the plan.")
            : scanned;

        return tier;
    }

    /// <summary>Parses a tier name, or null when it is missing or unrecognised.</summary>
    public static RiskTier? ParseTier(string? value) =>
        Enum.TryParse<RiskTier>(value, ignoreCase: true, out var tier) ? tier : null;

    private static RiskAssessment Scan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RiskAssessment.Unclassified;
        }

        foreach (var (marker, reason) in CriticalMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new RiskAssessment(RiskTier.Critical, reason);
            }
        }

        foreach (var (marker, reason) in HighMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new RiskAssessment(RiskTier.High, reason);
            }
        }

        foreach (var (marker, reason) in MediumMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return new RiskAssessment(RiskTier.Medium, reason);
            }
        }

        return RiskAssessment.Unclassified;
    }
}
