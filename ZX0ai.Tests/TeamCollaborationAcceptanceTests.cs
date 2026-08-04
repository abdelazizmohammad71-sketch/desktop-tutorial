using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Skills;

namespace ZX0ai.Tests;

/// <summary>
/// PART C acceptance: a team tier must actually run a team.
/// </summary>
/// <remarks>
/// The unit tests around <see cref="AgentOrchestrator"/> cover each protocol in
/// isolation with hand-built tiers. This suite runs the orchestrator against the
/// tier shape the product actually ships — three-plus members under one leader — and
/// asserts the observable contract the user was promised: several distinct agents
/// speak, they can see each other's work, and the leader closes with a synthesis.
/// </remarks>
public sealed class TeamCollaborationAcceptanceTests
{
    /// <summary>Mirrors the shipped Ultra tier: a leader plus three specialists.</summary>
    private static ModelTier UltraLikeTier(TeamProtocol protocol) => new()
    {
        Key = "zxa-Ultra-full-max",
        DisplayName = "zxa-Ultra-full-max",
        Mode = TeamMode.Team,
        Protocol = protocol,
        Leader = "vendor/leader-model",
        Members =
        [
            new TeamMember { Role = AgentRole.Planner, Model = "vendor/planner-model" },
            new TeamMember { Role = AgentRole.Coder, Model = "vendor/coder-model" },
            new TeamMember { Role = AgentRole.Reviewer, Model = "vendor/reviewer-model" },
        ],
    };

    private static AgentOrchestrator Build(IChatProvider provider) => new(
        provider,
        new SkillRegistry([], Constitution.Default(), NullLogger<SkillRegistry>.Instance),
        Constitution.Default(),
        NullLogger<AgentOrchestrator>.Instance);

    private static async Task<List<OrchestrationUpdate>> RunAsync(
        AgentOrchestrator orchestrator,
        ModelTier tier,
        string prompt = "Design and review a caching layer.")
    {
        var updates = new List<OrchestrationUpdate>();

        await foreach (var update in orchestrator.RunAsync(
            tier,
            [new ChatMessage { Role = ChatRole.User, Content = prompt }]))
        {
            updates.Add(update);
        }

        return updates;
    }

    [Theory]
    [InlineData(TeamProtocol.LeaderDelegate)]
    [InlineData(TeamProtocol.DebateThenSynthesize)]
    [InlineData(TeamProtocol.Pipeline)]
    public async Task EveryProtocol_RunsAtLeastThreeDistinctAgents(TeamProtocol protocol)
    {
        var updates = await RunAsync(Build(new FakeChatProvider()), UltraLikeTier(protocol));

        var speakers = updates
            .Where(update => update.Kind == OrchestrationUpdateKind.TurnStarted)
            .Select(update => update.Turn!.AgentId)
            .Distinct()
            .ToList();

        Assert.True(
            speakers.Count >= 3,
            $"{protocol} produced only {speakers.Count} distinct agent(s): {string.Join(", ", speakers)}");
    }

    [Theory]
    [InlineData(TeamProtocol.LeaderDelegate)]
    [InlineData(TeamProtocol.DebateThenSynthesize)]
    [InlineData(TeamProtocol.Pipeline)]
    public async Task EveryProtocol_EndsWithALeaderSynthesis(TeamProtocol protocol)
    {
        var updates = await RunAsync(Build(new FakeChatProvider()), UltraLikeTier(protocol));

        Assert.Contains(updates, update => update.Kind == OrchestrationUpdateKind.FinalAnswer);

        // The synthesis is the last thing the user sees, after every member turn.
        var lastTurn = updates.FindLastIndex(u => u.Kind == OrchestrationUpdateKind.TurnStarted);
        var firstFinal = updates.FindIndex(u => u.Kind == OrchestrationUpdateKind.FinalAnswer);

        Assert.True(
            firstFinal > lastTurn,
            "The leader's synthesis must come after every member has contributed.");
    }

    [Fact]
    public async Task EveryConfiguredMember_ActuallyRuns()
    {
        var provider = new FakeChatProvider();
        var tier = UltraLikeTier(TeamProtocol.LeaderDelegate);

        await RunAsync(Build(provider), tier);

        // Each member's own model must have been called: a team that silently drops
        // a member is the exact failure this suite exists to catch.
        foreach (var member in tier.Members)
        {
            Assert.Contains(provider.Calls, call => call.Model == member.Model);
        }

        Assert.Contains(provider.Calls, call => call.Model == tier.Leader);
    }

    [Fact]
    public async Task MembersCanSeeEachOthersContributions()
    {
        var provider = new FakeChatProvider((model, _) => $"finding from {model}");
        var tier = UltraLikeTier(TeamProtocol.Pipeline);

        await RunAsync(Build(provider), tier);

        // Pipeline runs Planner then Coder then Reviewer, so the last stage's prompt
        // must contain what the earlier stages wrote.
        var reviewerCall = provider.Calls.First(call => call.Model == "vendor/reviewer-model");

        Assert.Contains("finding from vendor/planner-model", reviewerCall.Prompt, StringComparison.Ordinal);
        Assert.Contains("finding from vendor/coder-model", reviewerCall.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLeaderSeesTheWholeTranscriptBeforeSynthesising()
    {
        var provider = new FakeChatProvider((model, _) => $"said by {model}");
        var tier = UltraLikeTier(TeamProtocol.LeaderDelegate);

        await RunAsync(Build(provider), tier);

        var synthesis = provider.Calls.Last();

        foreach (var member in tier.Members)
        {
            Assert.Contains($"said by {member.Model}", synthesis.Prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TeamTranscript_IsRenderableAsZxaWithoutLeakingVendorNames()
    {
        var updates = await RunAsync(
            Build(new FakeChatProvider()),
            UltraLikeTier(TeamProtocol.DebateThenSynthesize));

        var turns = updates
            .Where(update => update.Turn is not null)
            .Select(update => update.Turn!)
            .ToList();

        Assert.NotEmpty(turns);

        foreach (var turn in turns)
        {
            // The slug is carried for diagnostics...
            Assert.False(string.IsNullOrWhiteSpace(turn.Model));

            // ...but every label the transcript renders is ZXA-branded.
            Assert.False(
                ZxaBranding.LooksLikeVendorIdentifier(ZxaBranding.CallsignFor(turn.Role)),
                $"Turn for {turn.Role} would render a vendor identifier.");
        }
    }

    [Fact]
    public async Task AMemberFailing_DoesNotCostTheUserTheAnswer()
    {
        var provider = new SelectiveFailingProvider(model => model.Contains("coder", StringComparison.Ordinal));

        var updates = await RunAsync(Build(provider), UltraLikeTier(TeamProtocol.LeaderDelegate));

        Assert.Contains(updates, update => update.Kind == OrchestrationUpdateKind.Warning);
        Assert.Contains(updates, update => update.Kind == OrchestrationUpdateKind.FinalAnswer);
    }
}
