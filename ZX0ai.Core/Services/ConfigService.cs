using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Models;

namespace ZX0ai.Core.Services;

/// <summary>Filesystem locations the config layer reads and writes.</summary>
/// <param name="BaseSettingsPath">Shipped defaults, read-only at runtime.</param>
/// <param name="UserOverridePath">Per-user preferences. Secrets are never written here.</param>
/// <param name="CatalogCachePath">Optional sanitized OpenRouter capability cache.</param>
public sealed record ConfigPaths(
    string BaseSettingsPath,
    string UserOverridePath,
    string? CatalogCachePath = null);

/// <inheritdoc cref="IConfigService" />
public sealed class ConfigService : IConfigService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConfigPaths _paths;
    private readonly ILogger<ConfigService> _logger;

    private ZX0aiOptions _options = new();
    private IReadOnlyList<ModelTier> _tiers = [];
    private string? _activeTierKey;

    public ConfigService(ConfigPaths paths, ILogger<ConfigService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public ZX0aiOptions Options => _options;

    public IReadOnlyList<ModelTier> Tiers => _tiers;

    public ModelTier DefaultTier =>
        FindTier(_options.DefaultTier) ?? _tiers.FirstOrDefault() ?? FallbackTier;

    public ModelTier ActiveTier =>
        FindTier(_activeTierKey ?? string.Empty) ?? DefaultTier;

    public event EventHandler? Changed;

    public event EventHandler? ActiveTierChanged;

    public Task LoadAsync(CancellationToken cancellationToken = default) => ReloadAsync(cancellationToken);

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var previousActiveKey = _activeTierKey;
        var options = await ReadAsync(_paths.BaseSettingsPath, cancellationToken).ConfigureAwait(false)
                      ?? new ZX0aiOptions();

        var overrides = await ReadAsync(_paths.UserOverridePath, cancellationToken).ConfigureAwait(false);
        if (overrides is not null)
        {
            Merge(options, overrides);
        }

        _options = options;
        _tiers = ProjectTiers(options);
        _activeTierKey = FindTier(previousActiveKey ?? options.DefaultTier)?.Key ?? DefaultTier.Key;

        _logger.LogInformation(
            "Configuration loaded: provider={Provider}, tiers={TierCount}, apiKey={KeyState}",
            options.Provider,
            _tiers.Count,
            DescribeKeyState());

        Changed?.Invoke(this, EventArgs.Empty);
        ActiveTierChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Environment variable the credential is read from. It is deliberately the only
    /// supported source.
    /// </summary>
    public const string ApiKeyVariable = "OPENROUTER_API_KEY";

    private static readonly IReadOnlySet<string> KnownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "openrouter",
        "qwen",
    };

    /// <summary>True when a usable credential was found in the environment.</summary>
    public bool HasCredential => !string.IsNullOrWhiteSpace(ResolveCredential(ActiveTier));

    /// <summary>
    /// Reads the credential straight from the environment.
    /// </summary>
    /// <remarks>
    /// Resolved per call rather than cached so rotating the variable takes effect on
    /// the next request. The value never enters the options graph, logs, or files.
    /// </remarks>
    public string? ResolveCredential(ModelTier? tier)
    {
        // The tier's own variable wins; the shared one is the fallback. That ordering
        // is what lets a per-tier key be added without disturbing a working setup, and
        // lets a tier without its own key keep running on the shared one.
        if (tier?.ApiKeyEnvironmentVariable is { Length: > 0 } variable &&
            Environment.GetEnvironmentVariable(variable)?.Trim() is { Length: > 0 } scoped)
        {
            return scoped;
        }

        // The fallback key follows the tier's own provider, not the global one, so a
        // qwen-backed tier finds its key even when the global provider is openrouter.
        var tierProvider = !string.IsNullOrWhiteSpace(tier?.Provider)
            ? tier!.Provider
            : _options.Provider?.Trim().ToLowerInvariant();
        var providerFallback = tierProvider switch
        {
            "qwen" => _options.Qwen.ApiKeyEnvironmentVariable,
            _ => ApiKeyVariable,
        };

        return Environment.GetEnvironmentVariable(providerFallback)?.Trim() is { Length: > 0 } shared
            ? shared
            : null;
    }

    /// <summary>
    /// Log-safe description covering per-tier variables as well as the shared one.
    /// The key itself is never logged.
    /// </summary>
    private string DescribeKeyState()
    {
        var configured = _tiers
            .Where(tier => !string.IsNullOrWhiteSpace(ResolveCredential(tier)))
            .Select(tier => tier.Key)
            .ToList();

        return configured.Count == 0
            ? $"none (set {ApiKeyVariable})"
            : $"present for {configured.Count}/{_tiers.Count} tier(s): {string.Join(", ", configured)}";
    }

    public async Task SaveUserOverridesAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_paths.UserOverridePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Only user-editable surface is persisted. The OpenRouter endpoint is owned by
        // the provider registry and must not be written from user preferences.
        var payload = new UserPreferencesPayload
        {
            DefaultTier = _options.DefaultTier,
            Provider = _options.Provider,
            Ui = _options.Ui,
            OpenRouter = new OpenRouterPreferences
            {
                ApiKeyEnvironmentVariable = _options.OpenRouter.ApiKeyEnvironmentVariable,
                CatalogCacheHours = _options.OpenRouter.CatalogCacheHours,
                ValidateModelsOnStartup = _options.OpenRouter.ValidateModelsOnStartup,
                MaxOutputTokens = _options.OpenRouter.MaxOutputTokens,
                AppUrl = _options.OpenRouter.AppUrl,
                AppName = _options.OpenRouter.AppName,
            },
            Qwen = _options.Qwen,
        };

        await using var stream = File.Create(_paths.UserOverridePath);
        await JsonSerializer.SerializeAsync(stream, payload, WriteOptions, cancellationToken).ConfigureAwait(false);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ModelTier? FindTier(string key)
    {
        var direct = _tiers.FirstOrDefault(t =>
            string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

        if (direct is not null)
        {
            return direct;
        }

        return _options.TierAliases.TryGetValue(key, out var canonical)
            ? _tiers.FirstOrDefault(t => string.Equals(t.Key, canonical, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    public bool SelectActiveTier(string key)
    {
        var tier = FindTier(key);
        if (tier is null)
        {
            return false;
        }

        if (string.Equals(_activeTierKey, tier.Key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        _activeTierKey = tier.Key;
        ActiveTierChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<ZX0aiOptions?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<ZX0aiOptions>(stream, ReadOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // A malformed settings file must not take the app down; ship defaults instead.
            _logger.LogError(ex, "Malformed settings file at {Path}; ignoring it.", path);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not read settings file at {Path}.", path);
            return null;
        }
    }

    private static void Merge(ZX0aiOptions target, ZX0aiOptions source)
    {
        if (!string.IsNullOrWhiteSpace(source.DefaultTier))
        {
            target.DefaultTier = source.DefaultTier;
        }

        if (!string.IsNullOrWhiteSpace(source.Provider) &&
            KnownProviders.Contains(source.Provider.Trim()))
        {
            target.Provider = source.Provider.Trim();
        }

        target.Ui = MergeUi(target.Ui, source.Ui);
        target.OpenRouter = MergeOpenRouter(target.OpenRouter, source.OpenRouter);
        target.Qwen = MergeQwen(target.Qwen, source.Qwen);
    }

    private static UiOptions MergeUi(UiOptions target, UiOptions source)
    {
        if (!string.IsNullOrWhiteSpace(source.UserName))
        {
            target.UserName = source.UserName;
        }

        if (!string.IsNullOrWhiteSpace(source.Language))
        {
            target.Language = source.Language;
        }

        if (source.ReducedMotion.HasValue)
        {
            target.ReducedMotion = source.ReducedMotion;
        }

        target.ShowOrbDebugOverlay = source.ShowOrbDebugOverlay;
        return target;
    }

    private static OpenRouterOptions MergeOpenRouter(OpenRouterOptions target, OpenRouterOptions source)
    {
        // BaseUrl is part of the provider registry and must not be overridden by user
        // preferences — see OpenRouterEndpointPolicy which pins calls to openrouter.ai.

        if (!string.IsNullOrWhiteSpace(source.ApiKeyEnvironmentVariable))
        {
            target.ApiKeyEnvironmentVariable = source.ApiKeyEnvironmentVariable;
        }

        if (source.CatalogCacheHours > 0)
        {
            target.CatalogCacheHours = source.CatalogCacheHours;
        }

        target.ValidateModelsOnStartup = source.ValidateModelsOnStartup;
        if (source.MaxOutputTokens > 0)
        {
            target.MaxOutputTokens = source.MaxOutputTokens;
        }

        if (!string.IsNullOrWhiteSpace(source.AppUrl))
        {
            target.AppUrl = source.AppUrl;
        }

        if (!string.IsNullOrWhiteSpace(source.AppName))
        {
            target.AppName = source.AppName;
        }

        return target;
    }

    private static QwenOptions MergeQwen(QwenOptions target, QwenOptions source)
    {
        if (!string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            target.BaseUrl = source.BaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(source.ApiKeyEnvironmentVariable))
        {
            target.ApiKeyEnvironmentVariable = source.ApiKeyEnvironmentVariable;
        }

        if (!string.IsNullOrWhiteSpace(source.DefaultModel))
        {
            target.DefaultModel = source.DefaultModel;
        }

        if (source.Temperature is > 0 and <= 2)
        {
            target.Temperature = source.Temperature;
        }

        if (source.TopP is > 0 and <= 1)
        {
            target.TopP = source.TopP;
        }

        if (source.MaxTokens > 0)
        {
            target.MaxTokens = source.MaxTokens;
        }

        target.Stream = source.Stream;

        if (!string.IsNullOrWhiteSpace(source.ReasoningLevel))
        {
            target.ReasoningLevel = source.ReasoningLevel;
        }

        return target;
    }

    private static IReadOnlyList<ModelTier> ProjectTiers(ZX0aiOptions options)
    {
        var result = new List<ModelTier>(options.Tiers.Count);

        foreach (var (key, tier) in options.Tiers)
        {
            var mode = ParseMode(tier.Mode);

            var projectedMembers = tier.Members.Select(ProjectMember).ToList();
            var configuredLeader = projectedMembers.FirstOrDefault(m =>
                string.Equals(m.RoleId, "leader", StringComparison.OrdinalIgnoreCase));

            // Legacy team definitions kept the leader slug outside the member list.
            if (configuredLeader is null && !string.IsNullOrWhiteSpace(tier.Leader))
            {
                configuredLeader = new TeamMember
                {
                    Role = AgentRole.Leader,
                    RoleId = "leader",
                    DisplayName = "Leader",
                    Model = tier.Leader,
                    RequestedSlug = tier.Leader,
                    ResolvedSlug = tier.Leader,
                    Responsibility = "Owns delegation, arbitration, verification, and the final synthesis.",
                };
            }

            result.Add(new ModelTier
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(tier.DisplayName) ? key : tier.DisplayName,
                Mode = mode,
                Protocol = ParseProtocol(tier.Protocol, mode),
                RequireAllMembersInAgentMode = tier.RequireAllMembersInAgentMode,
                RelativeSpeed = Math.Clamp(tier.RelativeSpeed, 1, 4),
                RelativeCost = Math.Clamp(tier.RelativeCost, 1, 4),
                Speed = string.IsNullOrWhiteSpace(tier.Speed) ? "Standard" : tier.Speed,
                Provider = ResolveTierProvider(tier.Provider, options.Provider),
                Model = tier.Model ?? string.Empty,
                Leader = configuredLeader?.EffectiveModel ?? tier.Leader,
                LeaderMember = configuredLeader,
                Theme = string.Equals(tier.Theme, "fire", StringComparison.OrdinalIgnoreCase)
                    ? TierTheme.Fire
                    : TierTheme.Violet,
                Level = Math.Clamp(tier.Level, 1, 4),
                ApiKeyEnvironmentVariable = tier.ApiKeyEnvironmentVariable,
                Members = projectedMembers.Where(m => !ReferenceEquals(m, configuredLeader)).ToList(),
            });
        }

        return result;
    }

    private static TeamMember ProjectMember(MemberOptions member)
    {
        var requested = string.IsNullOrWhiteSpace(member.RequestedSlug)
            ? member.Model ?? string.Empty
            : member.RequestedSlug;

        var roleId = string.IsNullOrWhiteSpace(member.Role) ? "coder" : member.Role;

        return new TeamMember
        {
            Role = ParseRole(roleId),
            RoleId = roleId,
            DisplayName = string.IsNullOrWhiteSpace(member.DisplayName)
                ? DisplayNameFor(roleId)
                : member.DisplayName,
            Model = requested,
            RequestedSlug = requested,
            FallbackSlugs = member.FallbackSlugs,
            EffortProfile = string.IsNullOrWhiteSpace(member.EffortProfile)
                ? "provider-default"
                : member.EffortProfile,
            Responsibility = member.Responsibility ?? string.Empty,
            SystemPrompt = member.SystemPrompt,
        };
    }

    private static string DisplayNameFor(string roleId) => roleId.ToLowerInvariant() switch
    {
        "leader" => "Leader",
        "principal-engineer" => "Principal Engineer",
        "autonomous-builder" => "Autonomous Builder",
        "deep-engineer" => "Deep Engineer",
        "architecture-reviewer" => "Architecture Reviewer",
        "senior-coder" => "Senior Coder",
        "problem-solver" => "Problem Solver",
        "fast-coder" => "Fast Coder",
        "code-specialist" => "Code Specialist",
        "primary-coder" => "Primary Coder",
        "secondary-coder" => "Secondary Coder",
        "ui-ux-designer" => "UI/UX Designer",
        _ => roleId.Replace('-', ' '),
    };

    /// <summary>
    /// Resolves the provider for a tier, falling back to the global provider when the
    /// tier does not declare its own.
    /// </summary>
    private static string ResolveTierProvider(string? tierProvider, string? globalProvider)
    {
        var raw = !string.IsNullOrWhiteSpace(tierProvider) ? tierProvider!.Trim() : globalProvider;
        return string.IsNullOrWhiteSpace(raw) ? "openrouter" : raw.ToLowerInvariant();
    }

    private static TeamMode ParseMode(string? value) =>
        string.Equals(value, "team", StringComparison.OrdinalIgnoreCase) ? TeamMode.Team : TeamMode.Single;

    private static TeamProtocol ParseProtocol(string? value, TeamMode mode) => value?.ToLowerInvariant() switch
    {
        "leader-delegate" => TeamProtocol.LeaderDelegate,
        "debate-then-synthesize" => TeamProtocol.DebateThenSynthesize,
        "pipeline" => TeamProtocol.Pipeline,
        "single" => TeamProtocol.Single,
        // A team tier with no protocol still needs one; delegation is the safe default.
        _ => mode == TeamMode.Team ? TeamProtocol.LeaderDelegate : TeamProtocol.Single,
    };

    private static AgentRole ParseRole(string? value) => value?.ToLowerInvariant() switch
    {
        "leader" => AgentRole.Leader,
        "planner" => AgentRole.Planner,
        "reviewer" or "architecture-reviewer" => AgentRole.Reviewer,
        "researcher" => AgentRole.Researcher,
        "critic" => AgentRole.Critic,
        "problem-solver" => AgentRole.ProblemSolver,
        "ui-ux-designer" or "designer" => AgentRole.Designer,
        "autonomous-builder" => AgentRole.Builder,
        _ => AgentRole.Coder,
    };

    /// <summary>Used only when configuration is missing entirely, so the UI still renders.</summary>
    private static ModelTier FallbackTier { get; } = new()
    {
        Key = "zxa-Lite",
        DisplayName = "zxa-Lite",
        Mode = TeamMode.Single,
        Protocol = TeamProtocol.Single,
    };

    private sealed class UserPreferencesPayload
    {
        public string DefaultTier { get; init; } = string.Empty;

        public string Provider { get; init; } = "openrouter";

        public UiOptions Ui { get; init; } = new();

        public OpenRouterPreferences OpenRouter { get; init; } = new();

        public QwenOptions Qwen { get; init; } = new();
    }

    private sealed class OpenRouterPreferences
    {
        public string? ApiKeyEnvironmentVariable { get; init; }

        public int CatalogCacheHours { get; init; }

        public bool ValidateModelsOnStartup { get; init; }

        public int MaxOutputTokens { get; init; }

        public string? AppUrl { get; init; }

        public string? AppName { get; init; }
    }
}
