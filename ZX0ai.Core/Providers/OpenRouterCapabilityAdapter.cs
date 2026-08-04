using ZX0ai.Core.Services;

namespace ZX0ai.Core.Providers;

/// <summary>
/// The only place where ZX0ai effort profiles become provider request parameters.
/// Unsupported reasoning fields are omitted rather than guessed.
/// </summary>
public sealed class OpenRouterCapabilityAdapter(
    IConfigService config,
    IOpenRouterCatalogService catalog) : IOpenRouterCapabilityAdapter
{
    private static readonly IReadOnlyDictionary<string, string[]> Defaults =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["low"] = ["low", "minimal", "none"],
            ["medium"] = ["medium", "low", "minimal"],
            ["high"] = ["high", "medium", "low"],
            ["extra-high"] = ["xhigh", "max", "high"],
            ["max"] = ["max", "xhigh", "high"],
            ["ultra"] = ["max", "xhigh", "high", "medium"],
        };

    public OpenRouterAdaptedOptions Adapt(ModelInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.ResolvedSlug))
        {
            throw new InvalidOperationException(
                $"Model '{invocation.RequestedSlug}' has no validated OpenRouter resolution.");
        }

        var capability = catalog.Find(invocation.ResolvedSlug) ?? throw new InvalidOperationException(
            $"Model '{invocation.ResolvedSlug}' is not present in the validated OpenRouter catalog.");
        var routing = string.Equals(invocation.Speed, "Fast", StringComparison.OrdinalIgnoreCase)
            ? new Dictionary<string, object?> { ["sort"] = "throughput" }
            : null;
        var outputLimit = OutputLimitFor(capability);

        if (string.Equals(invocation.EffortProfile, "provider-default", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(invocation.EffortProfile))
        {
            return new OpenRouterAdaptedOptions(
                null, null, capability.SupportsTools, routing, outputLimit, capability.SupportsToolChoice);
        }

        if (!capability.SupportsReasoning)
        {
            return new OpenRouterAdaptedOptions(
                null, null, capability.SupportsTools, routing, outputLimit, capability.SupportsToolChoice);
        }

        IReadOnlyList<string> candidates;
        if (config.Options.ReasoningProfiles.TryGetValue(invocation.EffortProfile, out var configured))
        {
            candidates = configured;
        }
        else if (Defaults.TryGetValue(invocation.EffortProfile, out var defaults))
        {
            candidates = defaults;
        }
        else
        {
            candidates = [];
        }

        var effort = candidates.FirstOrDefault(candidate =>
            (!string.Equals(candidate, "none", StringComparison.OrdinalIgnoreCase) ||
             !capability.ReasoningMandatory) &&
            capability.SupportedEfforts.Contains(candidate, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(effort))
        {
            return new OpenRouterAdaptedOptions(
                new Dictionary<string, object?> { ["effort"] = effort },
                effort,
                capability.SupportsTools,
                routing,
                outputLimit,
                capability.SupportsToolChoice);
        }

        if (capability.SupportsReasoningMaxTokens)
        {
            return new OpenRouterAdaptedOptions(
                new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["max_tokens"] = BudgetFor(invocation.EffortProfile),
                },
                "token-budget",
                capability.SupportsTools,
                routing,
                outputLimit,
                capability.SupportsToolChoice);
        }

        // Models such as DeepSeek V3.1 expose only reasoning.enabled. Never send an
        // effort value they did not advertise.
        return new OpenRouterAdaptedOptions(
            new Dictionary<string, object?> { ["enabled"] = true },
            "enabled",
            capability.SupportsTools,
            routing,
            outputLimit,
            capability.SupportsToolChoice);
    }

    private static int BudgetFor(string profile) => profile.ToLowerInvariant() switch
    {
        "low" => 1_024,
        "medium" => 4_096,
        "high" => 8_192,
        "extra-high" => 16_384,
        "max" => 32_768,
        "ultra" => 65_536,
        _ => 4_096,
    };

    private IReadOnlyDictionary<string, object?>? OutputLimitFor(
        OpenRouterModelCapability capability)
    {
        var limit = Math.Clamp(config.Options.OpenRouter.MaxOutputTokens, 256, 131_072);

        if (capability.SupportedParameters.Contains(
            "max_completion_tokens",
            StringComparer.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?> { ["max_completion_tokens"] = limit };
        }

        return capability.SupportedParameters.Contains("max_tokens", StringComparer.OrdinalIgnoreCase)
            ? new Dictionary<string, object?> { ["max_tokens"] = limit }
            : null;
    }
}
