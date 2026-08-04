namespace ZX0ai.Core.Models;

/// <summary>
/// Customer-facing naming. DXM is presented as one single AI model.
/// </summary>
/// <remarks>
/// <para>
/// The product runs a multi-agent team internally, and the domain objects carry
/// provider slugs, roles and per-agent state because orchestration, logging and the
/// capability adapter genuinely need them. None of that is part of the product: the
/// customer sees <b>DXM</b> answering, not a directory of vendors or a roster of
/// internal agents.
/// </para>
/// <para>
/// Two distinct kinds of leakage are guarded here, because they fail differently:
/// a <i>vendor</i> identifier reveals which third party is behind the product, while
/// an <i>internal</i> identifier reveals that there is a team at all. Both are
/// checked, so a refactor cannot reintroduce either by accident.
/// </para>
/// </remarks>
public static class ZxaBranding
{
    /// <summary>The only brand the customer ever sees.</summary>
    public const string ProductName = "DXM";

    /// <summary>
    /// Attribution for anything the product produced. Deliberately constant: every
    /// internal agent answers as DXM, so no call site can accidentally distinguish
    /// them.
    /// </summary>
    public static string AttributionFor(AgentRole role)
    {
        _ = role;
        return ProductName;
    }

    /// <summary>Legacy name kept so existing call sites keep compiling.</summary>
    public static string CallsignFor(AgentRole role) => AttributionFor(role);

    /// <summary>
    /// The customer-facing name for a tier: DXM, plus a capability mode.
    /// </summary>
    /// <remarks>
    /// A tier is an internal routing concept — which models, how many, at what effort —
    /// and its key says so out loud. What the customer is choosing is a capability
    /// level of the one product, so that is what gets rendered. The key never appears.
    /// </remarks>
    public static string TierTag(string? tierKey)
    {
        var mode = ModeFor(tierKey);
        return mode.Length == 0 ? ProductName : $"{ProductName} {mode}";
    }

    /// <summary>Maps a tier key onto a capability mode, or empty when unrecognised.</summary>
    /// <remarks>
    /// Order matters: keys overlap, and the heaviest match must win. Falling through to
    /// an empty mode is deliberate — an unknown tier renders as plain "DXM" rather than
    /// risking an echo of its key.
    /// </remarks>
    private static string ModeFor(string? tierKey)
    {
        if (string.IsNullOrWhiteSpace(tierKey))
        {
            return string.Empty;
        }

        bool Has(string marker) => tierKey.Contains(marker, StringComparison.OrdinalIgnoreCase);

        return Has("ultra") ? "Ultra"
            : Has("pro") ? "Pro"
            : Has("medium") || Has("medim") ? "Standard"
            : Has("lite") || Has("light") ? "Fast"
            : Has("low") || Has("mini") || Has("free") ? "Mini"
            : string.Empty;
    }

    /// <summary>True when a tier key denotes the heaviest, customer-visible step.</summary>
    public static bool IsUltraTier(string? tierKey) =>
        tierKey is not null &&
        tierKey.Contains("ultra", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="text"/> would reveal a third-party provider.
    /// </summary>
    public static bool LooksLikeVendorIdentifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (ContainsProviderSlug(text))
        {
            return true;
        }

        string[] vendors =
        [
            "anthropic", "claude", "openai", "gpt", "gemini", "google", "deepseek",
            "moonshot", "kimi", "qwen", "grok", "x-ai", "nvidia", "nemotron",
            "llama", "meta", "mistral", "cohere", "gemma", "openrouter",
        ];

        return vendors.Any(vendor => text.Contains(vendor, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when any whitespace-delimited token has the shape of a provider slug.
    /// </summary>
    /// <remarks>
    /// A slug is one token of the form <c>vendor/model</c>, and both halves are real
    /// names. Requiring three characters on each side is what separates
    /// <c>moonshotai/kimi-k3</c> from ordinary text that happens to contain a slash —
    /// a unit such as <c>tok/s</c> reveals nothing and must not be suppressed.
    /// </remarks>
    private static bool ContainsProviderSlug(string text)
    {
        const int MinimumSegment = 3;

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = token.IndexOf('/', StringComparison.Ordinal);

            if (slash >= MinimumSegment && token.Length - slash - 1 >= MinimumSegment)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> would reveal the internal team: a role name,
    /// a protocol, a tier key, or the old ZXA branding.
    /// </summary>
    /// <remarks>
    /// DXM must read as a single model. Showing "Leader", "debate-then-synthesize" or
    /// "zxa-Ultra-full-max" tells the customer there is machinery behind the curtain,
    /// which is exactly what the product is meant to hide.
    /// </remarks>
    public static bool LooksLikeInternalIdentifier(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] internals =
        [
            "zxa", "leader", "planner", "coder", "reviewer", "researcher", "critic",
            "principal-engineer", "autonomous-builder", "problemsolver", "designer",
            "leader-delegate", "debate-then-synthesize", "pipeline",
            "agent", "roster", "synthesis", "protocol", "tier",
        ];

        return internals.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when a label is safe to render to a customer.</summary>
    public static bool IsSafeForCustomer(string? text) =>
        !LooksLikeVendorIdentifier(text) && !LooksLikeInternalIdentifier(text);
}
