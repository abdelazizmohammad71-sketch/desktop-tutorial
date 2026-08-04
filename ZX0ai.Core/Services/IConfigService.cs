using ZX0ai.Core.Configuration;
using ZX0ai.Core.Models;

namespace ZX0ai.Core.Services;

/// <summary>
/// Reads <c>appsettings.json</c>, overlays non-secret user-local preferences, and
/// projects the result onto the domain model. Credentials never enter this graph.
/// </summary>
public interface IConfigService
{
    ZX0aiOptions Options { get; }

    /// <summary>Tiers in configuration order, ready for the tier selector.</summary>
    IReadOnlyList<ModelTier> Tiers { get; }

    /// <summary>The configured default tier, falling back to the first available one.</summary>
    ModelTier DefaultTier { get; }

    /// <summary>
    /// Tier selected for the current app session. This is independent of the persisted
    /// default so choosing a tier does not silently rewrite user settings.
    /// </summary>
    ModelTier ActiveTier { get; }

    /// <summary>Credential presence only; the value is never exposed.</summary>
    bool HasCredential { get; }

    /// <summary>Raised after <see cref="ReloadAsync"/> replaces the current snapshot.</summary>
    event EventHandler? Changed;

    /// <summary>Raised when <see cref="ActiveTier"/> changes.</summary>
    event EventHandler? ActiveTierChanged;

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists user-editable settings to the local override file.</summary>
    Task SaveUserOverridesAsync(CancellationToken cancellationToken = default);

    ModelTier? FindTier(string key);

    /// <summary>
    /// Selects a configured tier for this session. Unknown keys are rejected so a raw
    /// provider slug can never become the primary tier selection.
    /// </summary>
    bool SelectActiveTier(string key);

    /// <summary>
    /// Reads <c>OPENROUTER_API_KEY</c> directly from the environment, or null.
    /// </summary>
    string? ResolveCredential(ModelTier? tier);
}
