using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Services;

namespace ZX0ai.Backend;

internal static class TierContractMapper
{
    internal static IReadOnlyList<TierResponse> MapAll(
        IConfigService config,
        IOpenRouterCatalogService catalog) =>
        config.Tiers.Select(tier => Map(tier, config, catalog)).ToList();

    internal static TierResponse Map(
        ModelTier tier,
        IConfigService config,
        IOpenRouterCatalogService catalog)
    {
        var members = tier.AllMembers.Select(member => new TierMemberResponse(
            string.IsNullOrWhiteSpace(member.RoleId)
                ? member.Role.ToString().ToLowerInvariant()
                : member.RoleId,
            member.DisplayName,
            member.RequestedSlug,
            NullIfEmpty(member.ResolvedSlug),
            member.FallbackSlugs,
            member.EffortProfile,
            member.Responsibility,
            member.Availability,
            member.IsFallbackActive,
            member.Role == AgentRole.Leader)).ToList();

        var single = ResolveSingle(tier, catalog);
        var runnable = tier.IsTeam
            ? members.Count > 0 &&
              members.Any(member => member.Leader) &&
              members.All(member =>
                  member.Availability is ModelAvailability.Available or ModelAvailability.Fallback &&
                  !string.IsNullOrWhiteSpace(member.ResolvedSlug))
            : single.Availability == ModelAvailability.Available &&
              !string.IsNullOrWhiteSpace(single.ResolvedSlug);

        return new TierResponse(
            tier.Key,
            tier.DisplayName,
            tier.Mode.ToString().ToLowerInvariant(),
            ProtocolName(tier.Protocol),
            string.Equals(config.ActiveTier.Key, tier.Key, StringComparison.OrdinalIgnoreCase),
            runnable,
            tier.RequireAllMembersInAgentMode,
            tier.RelativeSpeed,
            tier.RelativeCost,
            tier.Speed,
            single.RequestedSlug,
            single.ResolvedSlug,
            single.Availability,
            members);
    }

    internal static (string RequestedSlug, string ResolvedSlug)? InvocationForSingle(
        ModelTier tier,
        IOpenRouterCatalogService catalog)
    {
        var resolved = ResolveSingle(tier, catalog);
        return resolved.Availability == ModelAvailability.Available &&
               !string.IsNullOrWhiteSpace(resolved.RequestedSlug) &&
               !string.IsNullOrWhiteSpace(resolved.ResolvedSlug)
            ? (resolved.RequestedSlug!, resolved.ResolvedSlug!)
            : null;
    }

    private static SingleResolution ResolveSingle(
        ModelTier tier,
        IOpenRouterCatalogService catalog)
    {
        if (tier.IsTeam)
        {
            return new SingleResolution(null, null, ModelAvailability.Unknown);
        }

        var requested = NullIfEmpty(tier.Model);
        if (requested is null)
        {
            return new SingleResolution(null, null, ModelAvailability.Unavailable);
        }

        return catalog.Find(requested) is not null
            ? new SingleResolution(requested, requested, ModelAvailability.Available)
            : new SingleResolution(requested, null, catalog.Current.Models.Count == 0
                ? ModelAvailability.Unknown
                : ModelAvailability.Unavailable);
    }

    private static string ProtocolName(TeamProtocol protocol) => protocol switch
    {
        TeamProtocol.LeaderDelegate => "leader-delegate",
        TeamProtocol.DebateThenSynthesize => "debate-then-synthesize",
        TeamProtocol.Pipeline => "pipeline",
        _ => "single",
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private readonly record struct SingleResolution(
        string? RequestedSlug,
        string? ResolvedSlug,
        ModelAvailability Availability);
}
