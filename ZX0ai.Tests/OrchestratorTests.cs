using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Skills;

namespace ZX0ai.Tests;

/// <summary>
/// A provider that echoes a scripted reply per model, recording what it was asked.
/// Lets the protocols be exercised with no network and no key.
/// </summary>
internal sealed class FakeChatProvider : IChatProvider
{
    private readonly Func<string, IReadOnlyList<ChatMessage>, string> _reply;

    public FakeChatProvider(Func<string, IReadOnlyList<ChatMessage>, string>? reply = null) =>
        _reply = reply ?? ((model, _) => $"reply from {model}");

    public string Name => "fake";

    public bool IsConfigured => true;

    /// <summary>Every call made, in order, as (model, systemPrompt, lastUserPrompt).</summary>
    public List<(string Model, string System, string Prompt)> Calls { get; } = [];

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add((
            model,
            messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty,
            messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty));

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return ChatDelta.Content(_reply(model, messages));
        yield return ChatDelta.Done(model);
    }
}

/// <summary>Fails for the models matching a predicate, succeeds for the rest.</summary>
internal sealed class SelectiveFailingProvider(Func<string, bool> shouldFail) : IChatProvider
{
    public string Name => "selective";

    public bool IsConfigured => true;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = messages;

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (shouldFail(model))
        {
            throw new ChatProviderException(ChatFailureReason.RateLimited, "Error_RateLimited");
        }

        yield return ChatDelta.Content($"reply from {model}");
        yield return ChatDelta.Done(model);
    }
}

internal sealed class ToolLoopProvider : IChatProvider
{
    private int _coderCalls;

    public string Name => "tool-loop";

    public bool IsConfigured => true;

    public bool SawCorrelatedToolResult { get; private set; }

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (model == "coder/model" && _coderCalls++ == 0)
        {
            yield return ChatDelta.Tool(new ToolCall("call_real", "read_file", "{}"));
            yield return ChatDelta.Done(model);
            yield break;
        }

        if (model == "coder/model")
        {
            SawCorrelatedToolResult = messages.Any(message =>
                message.Role == ChatRole.Tool &&
                message.ToolCallId == "call_real" &&
                message.Content == "done");
        }

        yield return ChatDelta.Content($"reply from {model}");
        yield return ChatDelta.Done(model);
    }
}

internal sealed class ConcurrencyProvider : IChatProvider
{
    private int _active;
    private int _maximum;

    public string Name => "concurrency";

    public bool IsConfigured => true;

    public int Maximum => Volatile.Read(ref _maximum);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = messages;
        var active = Interlocked.Increment(ref _active);
        while (true)
        {
            var observed = Volatile.Read(ref _maximum);
            if (active <= observed || Interlocked.CompareExchange(ref _maximum, active, observed) == observed)
            {
                break;
            }
        }

        try
        {
            await Task.Delay(35, cancellationToken);
            yield return ChatDelta.Content($"reply from {model}");
            yield return ChatDelta.Done(model);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

public sealed class OrchestratorTests
{
    private static ModelTier Tier(TeamProtocol protocol, params AgentRole[] roles) => new()
    {
        Key = "test-tier",
        DisplayName = "Test Tier",
        Mode = TeamMode.Team,
        Protocol = protocol,
        Leader = "leader/model",
        Members = [.. roles.Select(r => new TeamMember { Role = r, Model = $"{r}/model".ToLowerInvariant() })],
    };

    private static List<ChatMessage> Ask(string text) =>
        [new ChatMessage { Role = ChatRole.User, Content = text }];

    private static AgentOrchestrator Build(IChatProvider provider, out SkillRegistry registry)
    {
        var constitution = Constitution.Default();
        registry = new SkillRegistry([], constitution, NullLogger<SkillRegistry>.Instance);

        return new AgentOrchestrator(
            provider,
            registry,
            constitution,
            NullLogger<AgentOrchestrator>.Instance);
    }

    private static async Task<List<OrchestrationUpdate>> RunAsync(
        AgentOrchestrator orchestrator,
        ModelTier tier,
        string prompt = "Explain dependency injection.",
        AgentRunOptions? options = null)
    {
        var updates = new List<OrchestrationUpdate>();

        await foreach (var update in orchestrator.RunAsync(tier, Ask(prompt), options: options))
        {
            updates.Add(update);
        }

        return updates;
    }

    // ------------------------------------------------------------------ //
    // Plan mode
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Plan mode is enforced here, not asked for in a prompt. Whatever the leader
    /// decides, no member is woken and the run ends at the plan.
    /// </summary>
    [Fact]
    public async Task PlanMode_StopsAtTheLeader()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Planner, AgentRole.Coder),
            options: AgentRunOptions.Plan);

        var roles = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted)
            .Select(u => u.Turn!.Role)
            .ToList();

        Assert.Equal([AgentRole.Leader, AgentRole.Leader], roles);
        Assert.DoesNotContain(roles, role => role is AgentRole.Planner or AgentRole.Coder);
    }

    [Fact]
    public async Task PlanMode_StillAnswersTheUser()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder),
            options: AgentRunOptions.Plan);

        var answer = string.Concat(updates
            .Where(u => u.Kind == OrchestrationUpdateKind.FinalAnswer)
            .Select(u => u.Text));

        Assert.NotEmpty(answer);
        Assert.Contains("Plan mode", answer, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    // Approval gate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Scripts the leader to return a Critical plan, and every member to reply normally.
    /// The leader is identified by its model, which is the only agent given
    /// <c>leader/model</c> by <see cref="Tier"/>.
    /// </summary>
    private static string CriticalPlanReply(string model, IReadOnlyList<ChatMessage> messages)
    {
        _ = messages;

        return model == "leader/model"
            ? """
              I will rotate the signing key.

              ```dxm-plan
              {
                "mode": "delegate",
                "summary": "Rotate the production signing key.",
                "assignments": [ { "role": "Coder", "task": "Swap the key reference." } ]
              }
              ```
              """
            : $"reply from {model}";
    }

    /// <summary>
    /// The gate is a branch in the orchestrator, not an instruction in a prompt. When
    /// the leader's plan is High or Critical, no member is dispatched and the run ends
    /// asking for approval.
    /// </summary>
    [Fact]
    public async Task CriticalPlan_StopsBeforeDispatchingAnyone()
    {
        var orchestrator = Build(new FakeChatProvider(CriticalPlanReply), out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder));

        var roles = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted)
            .Select(u => u.Turn!.Role)
            .ToList();

        Assert.DoesNotContain(AgentRole.Coder, roles);

        var answer = string.Concat(updates
            .Where(u => u.Kind == OrchestrationUpdateKind.FinalAnswer)
            .Select(u => u.Text));

        // "production" alone is enough to reach High, which is already gated. The plan
        // never claimed a tier, so this is the scanner's own reading of the text.
        Assert.Contains("Approval required", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("High", answer, StringComparison.Ordinal);
        Assert.Contains("Nothing has been changed", answer, StringComparison.Ordinal);
    }

    /// <summary>Once the human has approved, the same plan runs.</summary>
    [Fact]
    public async Task ApprovedRun_DispatchesTheSamePlan()
    {
        var orchestrator = Build(new FakeChatProvider(CriticalPlanReply), out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder),
            options: AgentRunOptions.Approved);

        Assert.Contains(
            updates,
            u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn!.Role == AgentRole.Coder);
    }

    /// <summary>Normal runs must not pick up plan mode by accident.</summary>
    [Fact]
    public async Task WithoutPlanMode_MembersStillRun()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder));

        Assert.Contains(
            updates,
            u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn!.Role == AgentRole.Coder);
    }

    // ------------------------------------------------------------------ //
    // Team assembly
    // ------------------------------------------------------------------ //

    [Fact]
    public void BuildTeam_AlwaysProducesExactlyOneLeader()
    {
        var team = AgentFactory.BuildTeam(
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Reviewer),
            Constitution.Default());

        Assert.Single(team, a => a.IsLeader);
        Assert.Equal(3, team.Count);
    }

    [Fact]
    public void EveryAgent_IsSeededWithTheConstitution()
    {
        var constitution = Constitution.Default();
        var team = AgentFactory.BuildTeam(Tier(TeamProtocol.Pipeline, AgentRole.Planner), constitution);

        Assert.All(team, agent =>
            Assert.Contains("final authority", agent.SystemPrompt, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Roles_GetDistinctAccents()
    {
        var accents = new[] { AgentRole.Leader, AgentRole.Planner, AgentRole.Coder, AgentRole.Reviewer }
            .Select(AgentFactory.AccentFor)
            .ToList();

        Assert.Equal(accents.Count, accents.Distinct().Count());
    }

    [Fact]
    public void LeaderGrant_IsUnrestricted_AndReviewerIsReadOnly()
    {
        // An empty grant list means "all skills"; the Leader is the only role with it.
        Assert.Empty(AgentFactory.GrantsFor(AgentRole.Leader));

        var reviewer = AgentFactory.GrantsFor(AgentRole.Reviewer);
        Assert.Contains("read_file", reviewer);
        Assert.DoesNotContain("write_file", reviewer);
        Assert.DoesNotContain("run_command", reviewer);
    }

    // ------------------------------------------------------------------ //
    // Protocols
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task LeaderDelegate_Plans_ThenRunsEachMember_ThenSynthesizes()
    {
        var provider = new FakeChatProvider();
        var orchestrator = Build(provider, out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Planner, AgentRole.Coder));

        var started = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn?.IsFinalAnswer != true)
            .Select(u => u.Turn!.Role)
            .ToList();

        // Leader plans first, then each member takes a turn.
        Assert.Equal([AgentRole.Leader, AgentRole.Planner, AgentRole.Coder], started);
        Assert.Contains(updates, u => u.Kind == OrchestrationUpdateKind.FinalAnswer);
    }

    [Fact]
    public async Task DebateThenSynthesize_RunsAnswerRoundThenCritiqueRound()
    {
        var provider = new FakeChatProvider();
        var orchestrator = Build(provider, out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.DebateThenSynthesize, AgentRole.Coder, AgentRole.Critic));

        var started = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn?.IsFinalAnswer != true)
            .ToList();

        // The leader is briefed first — that holds for every protocol — then two
        // members take two rounds each.
        Assert.Equal(AgentRole.Leader, started[0].Turn!.Role);
        Assert.Equal(5, started.Count);
        Assert.Contains(started, u => u.Turn!.ReasoningSummary.Contains("independently", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(started, u => u.Turn!.ReasoningSummary.Contains("Critiquing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Debate_WithASingleMember_SkipsTheCritiqueRound()
    {
        // There is nobody to critique, so a second round would just be self-talk.
        var orchestrator = Build(new FakeChatProvider(), out _);

        var updates = await RunAsync(orchestrator, Tier(TeamProtocol.DebateThenSynthesize, AgentRole.Coder));

        var started = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn?.IsFinalAnswer != true)
            .ToList();

        // The leader's briefing, then one answer round and no critique.
        Assert.Equal([AgentRole.Leader, AgentRole.Coder], started.Select(u => u.Turn!.Role));
    }

    [Fact]
    public async Task Pipeline_RunsPlannerThenCoderThenReviewer_RegardlessOfConfigOrder()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        // Deliberately configured out of order.
        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.Pipeline, AgentRole.Reviewer, AgentRole.Planner, AgentRole.Coder));

        var order = updates
            .Where(u => u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn?.IsFinalAnswer != true)
            .Select(u => u.Turn!.Role)
            .ToList();

        // The leader is always briefed first; the pipeline order follows.
        Assert.Equal(
            [AgentRole.Leader, AgentRole.Planner, AgentRole.Coder, AgentRole.Reviewer],
            order);
    }

    [Fact]
    public async Task TeamWithNoMembers_StillProducesAFinalAnswer()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        var updates = await RunAsync(orchestrator, Tier(TeamProtocol.LeaderDelegate));

        Assert.Contains(updates, u => u.Kind == OrchestrationUpdateKind.FinalAnswer);
        Assert.Single(updates, u =>
            u.Kind == OrchestrationUpdateKind.TurnStarted && u.Turn?.IsFinalAnswer == true);
    }

    // ------------------------------------------------------------------ //
    // Leader authority and the bus
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task TheLeaderModel_ProducesTheFinalAnswer()
    {
        var provider = new FakeChatProvider((model, _) => $"answer<{model}>");
        var orchestrator = Build(provider, out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder));

        var final = string.Concat(updates
            .Where(u => u.Kind == OrchestrationUpdateKind.FinalAnswer)
            .Select(u => u.Text));

        Assert.Equal("answer<leader/model>", final);
    }

    [Fact]
    public async Task MembersSeeEachOthersWorkOnTheBus()
    {
        var provider = new FakeChatProvider((model, _) => $"contribution from {model}");
        var orchestrator = Build(provider, out _);

        await RunAsync(orchestrator, Tier(TeamProtocol.Pipeline, AgentRole.Planner, AgentRole.Coder));

        // The Coder ran after the Planner, so the Planner's output must be in its prompt.
        var coderCall = provider.Calls.First(c => c.Model == "coder/model");
        Assert.Contains("contribution from planner/model", coderCall.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLeadersSynthesisPrompt_ContainsTheWholeTranscript()
    {
        var provider = new FakeChatProvider((model, _) => $"said by {model}");
        var orchestrator = Build(provider, out _);

        await RunAsync(orchestrator, Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Reviewer));

        var synthesis = provider.Calls.Last();
        Assert.Contains("said by coder/model", synthesis.Prompt, StringComparison.Ordinal);
        Assert.Contains("said by reviewer/model", synthesis.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachAgent_IsCalledWithItsOwnModel()
    {
        var provider = new FakeChatProvider();
        var orchestrator = Build(provider, out _);

        await RunAsync(orchestrator, Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Researcher));

        Assert.Contains(provider.Calls, c => c.Model == "coder/model");
        Assert.Contains(provider.Calls, c => c.Model == "researcher/model");
        Assert.Contains(provider.Calls, c => c.Model == "leader/model");
    }

    // ------------------------------------------------------------------ //
    // Failure and cancellation
    // ------------------------------------------------------------------ //

    [Fact]
    public async Task AFailingMember_WarnsAndTheLeaderStillAnswers()
    {
        // One member dropping out must not cost the user their answer.
        var provider = new SelectiveFailingProvider(model => model.StartsWith("coder", StringComparison.Ordinal));
        var orchestrator = Build(provider, out _);

        var updates = await RunAsync(
            orchestrator,
            Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Reviewer));

        Assert.Contains(updates, u => u.Kind == OrchestrationUpdateKind.Warning);
        Assert.Contains(updates, u => u.Kind == OrchestrationUpdateKind.FinalAnswer);
    }

    [Fact]
    public async Task AFailingLeader_PropagatesATypedError()
    {
        // If the leader cannot synthesise there is no answer to give, so this must
        // reach the view model, which turns it into a localised message.
        var provider = new SelectiveFailingProvider(model => model.StartsWith("leader", StringComparison.Ordinal));
        var orchestrator = Build(provider, out _);

        var failure = await Assert.ThrowsAsync<ChatProviderException>(() =>
            RunAsync(orchestrator, Tier(TeamProtocol.LeaderDelegate)));

        Assert.Equal(ChatFailureReason.RateLimited, failure.Reason);
    }

    [Fact]
    public async Task Cancellation_StopsTheRun()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);
        using var cancellation = new CancellationTokenSource();

        var tier = Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Reviewer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in orchestrator.RunAsync(tier, Ask("hello"), cancellationToken: cancellation.Token))
            {
                // Cancel as soon as the first agent takes the floor.
                if (update.Kind == OrchestrationUpdateKind.TurnStarted)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }

    [Fact]
    public async Task CurrentTeam_IsExposedForTheRoster()
    {
        var orchestrator = Build(new FakeChatProvider(), out _);

        await RunAsync(orchestrator, Tier(TeamProtocol.LeaderDelegate, AgentRole.Coder, AgentRole.Reviewer));

        Assert.Equal(3, orchestrator.CurrentTeam.Count);
        Assert.Single(orchestrator.CurrentTeam, a => a.IsLeader);
    }

    [Fact]
    public async Task ToolResult_IsCorrelatedAndReturnedToTheModel()
    {
        var provider = new ToolLoopProvider();
        var read = new SpySkill("read_file");
        var registry = new SkillRegistry(
            [read],
            Constitution.Default(),
            NullLogger<SkillRegistry>.Instance);
        var orchestrator = new AgentOrchestrator(
            provider,
            registry,
            Constitution.Default(),
            NullLogger<AgentOrchestrator>.Instance);

        await RunAsync(orchestrator, Tier(TeamProtocol.Pipeline, AgentRole.Coder));

        Assert.True(provider.SawCorrelatedToolResult);
        Assert.Equal(1, read.Executions);
    }

    [Fact]
    public async Task DebateConcurrencyIsRealAndCappedAtConfiguredDefault()
    {
        var provider = new ConcurrencyProvider();
        var orchestrator = Build(provider, out _);

        await RunAsync(orchestrator, Tier(
            TeamProtocol.DebateThenSynthesize,
            AgentRole.Coder,
            AgentRole.Reviewer,
            AgentRole.Researcher,
            AgentRole.Critic));

        Assert.InRange(provider.Maximum, 2, 3);
    }
}
