using System.Text.Json.Serialization;

namespace ZX0ai.Core.Providers;

/// <summary>Sanitized, non-secret model capabilities returned by OpenRouter.</summary>
public sealed class OpenRouterModelCapability
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedParameters { get; init; } = [];

    public IReadOnlyList<string> SupportedEfforts { get; init; } = [];

    public bool SupportsReasoningMaxTokens { get; init; }

    public bool ReasoningMandatory { get; init; }

    public int? ContextLength { get; init; }

    public string? PromptPrice { get; init; }

    public string? CompletionPrice { get; init; }

    [JsonIgnore]
    public bool SupportsReasoning =>
        SupportedParameters.Contains("reasoning", StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool SupportsTools =>
        SupportedParameters.Contains("tools", StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool SupportsToolChoice =>
        SupportedParameters.Contains("tool_choice", StringComparer.OrdinalIgnoreCase);
}

public sealed class OpenRouterCatalogSnapshot
{
    public DateTimeOffset FetchedAt { get; init; }

    public IReadOnlyList<OpenRouterModelCapability> Models { get; init; } = [];

    public OpenRouterModelCapability? Find(string slug) => Models.FirstOrDefault(model =>
        string.Equals(model.Id, slug, StringComparison.OrdinalIgnoreCase));
}

public interface IOpenRouterCatalogService
{
    OpenRouterCatalogSnapshot Current { get; }

    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<OpenRouterCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);

    OpenRouterModelCapability? Find(string slug);
}

/// <summary>Request-time reasoning settings after capability normalization.</summary>
public sealed record OpenRouterAdaptedOptions(
    IReadOnlyDictionary<string, object?>? Reasoning,
    string? NormalizedEffort,
    bool SupportsTools,
    IReadOnlyDictionary<string, object?>? ProviderRouting = null,
    IReadOnlyDictionary<string, object?>? OutputLimit = null,
    bool SupportsToolChoice = false);

public interface IOpenRouterCapabilityAdapter
{
    OpenRouterAdaptedOptions Adapt(ModelInvocation invocation);
}
