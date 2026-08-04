using System.Text.Json.Serialization;

namespace ZX0ai.Core.Configuration;

/// <summary>
/// Wire shape of <c>appsettings.json</c>. Deserialised as-is, then projected onto
/// the domain model by <see cref="Services.ConfigService"/>.
/// </summary>
public sealed class ZX0aiOptions
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "openrouter";

    [JsonPropertyName("openrouter")]
    public OpenRouterOptions OpenRouter { get; set; } = new();

    [JsonPropertyName("qwen")]
    public QwenOptions Qwen { get; set; } = new();

    [JsonPropertyName("defaultTier")]
    public string DefaultTier { get; set; } = "zxa-Lite";

    /// <summary>
    /// App-level reasoning profiles. Values are ordered provider efforts to try,
    /// strongest/preferred first. The capability adapter is the only code allowed
    /// to project these profiles onto OpenRouter request parameters.
    /// </summary>
    [JsonPropertyName("reasoningProfiles")]
    public Dictionary<string, List<string>> ReasoningProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Backwards-compatible tier ids that never appear in the picker.</summary>
    [JsonPropertyName("tierAliases")]
    public Dictionary<string, string> TierAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("tiers")]
    public Dictionary<string, TierOptions> Tiers { get; set; } = [];

    [JsonPropertyName("agents")]
    public AgentRuntimeOptions Agents { get; set; } = new();

    [JsonPropertyName("ui")]
    public UiOptions Ui { get; set; } = new();
}

public sealed class OpenRouterOptions
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>Name only. The secret is read directly from the process environment.</summary>
    [JsonPropertyName("apiKeyEnvironmentVariable")]
    public string ApiKeyEnvironmentVariable { get; set; } = "OPENROUTER_API_KEY";

    [JsonPropertyName("catalogCacheHours")]
    public int CatalogCacheHours { get; set; } = 6;

    [JsonPropertyName("validateModelsOnStartup")]
    public bool ValidateModelsOnStartup { get; set; } = true;

    /// <summary>Per-call safety/cost ceiling, adapted to the model's supported field.</summary>
    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>Sent as <c>HTTP-Referer</c>; OpenRouter uses it for app attribution.</summary>
    [JsonPropertyName("appUrl")]
    public string AppUrl { get; set; } = "https://github.com/zx0ai";

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = "ZX0ai";
}

public sealed class QwenOptions
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "https://api.qwen.ai/v1";

    [JsonPropertyName("apiKeyEnvironmentVariable")]
    public string ApiKeyEnvironmentVariable { get; set; } = "QWEN_API_KEY";

    [JsonPropertyName("defaultModel")]
    public string DefaultModel { get; set; } = "qwen-3.8-max";

    [JsonPropertyName("temperature")]
    public decimal Temperature { get; set; } = 0.7m;

    [JsonPropertyName("topP")]
    public decimal TopP { get; set; } = 0.95m;

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 2048;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("reasoningLevel")]
    public string ReasoningLevel { get; set; } = "high";
}

public sealed class TierOptions
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary><c>single</c> or <c>team</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "single";

    /// <summary><c>leader-delegate</c>, <c>debate-then-synthesize</c> or <c>pipeline</c>.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("requireAllMembersInAgentMode")]
    public bool RequireAllMembersInAgentMode { get; set; }

    [JsonPropertyName("relativeSpeed")]
    public int RelativeSpeed { get; set; } = 2;

    [JsonPropertyName("relativeCost")]
    public int RelativeCost { get; set; } = 2;

    [JsonPropertyName("speed")]
    public string Speed { get; set; } = "Standard";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Visual identity this tier switches the whole app into: <c>violet</c> (default)
    /// or <c>fire</c>. Selecting a fire tier re-skins accents, glows and the orb.
    /// </summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>Relative power, used for the tier icon: 1 = lowest, 4 = ultra.</summary>
    [JsonPropertyName("level")]
    public int Level { get; set; } = 1;

    [JsonPropertyName("leader")]
    public string? Leader { get; set; }

    /// <summary>
    /// Environment variable holding this tier's credential.
    /// </summary>
    /// <remarks>
    /// The variable name only — the secret is read from the process environment and
    /// never enters this graph, a log or a file. Naming it per tier lets each
    /// capability bill against its own key, so switching capability switches key with
    /// nothing stored in the app. Falls back to the shared variable when unset.
    /// </remarks>
    [JsonPropertyName("apiKeyEnvironmentVariable")]
    public string? ApiKeyEnvironmentVariable { get; set; }

    [JsonPropertyName("members")]
    public List<MemberOptions> Members { get; set; } = [];
}

public sealed class MemberOptions
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "coder";

    /// <summary>Legacy single-slug field retained for old local configurations.</summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("requestedSlug")]
    public string? RequestedSlug { get; set; }

    [JsonPropertyName("fallbackSlugs")]
    public List<string> FallbackSlugs { get; set; } = [];

    [JsonPropertyName("effortProfile")]
    public string EffortProfile { get; set; } = "provider-default";

    [JsonPropertyName("responsibility")]
    public string? Responsibility { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }
}

public sealed class AgentRuntimeOptions
{
    [JsonPropertyName("maxConcurrentAgents")]
    public int MaxConcurrentAgents { get; set; } = 3;
}

public sealed class UiOptions
{
    /// <summary>Name interpolated into the Arabic welcome heading.</summary>
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = "Sophia";

    /// <summary>BCP-47 tag forced onto the resource resolver. English is the default.</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en-US";

    /// <summary>Honours the OS setting when null; force with true/false.</summary>
    [JsonPropertyName("reducedMotion")]
    public bool? ReducedMotion { get; set; }

    /// <summary>Renders the orb's live RMS/FFT overlay.</summary>
    [JsonPropertyName("showOrbDebugOverlay")]
    public bool ShowOrbDebugOverlay { get; set; }
}
