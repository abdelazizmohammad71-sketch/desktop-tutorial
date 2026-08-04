using ZX0ai.Core.Composition;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Governance;
using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Skills;
using ZX0ai.Core.Services;

namespace ZX0ai.Core.Agents;

/// <summary>
/// The Leader. Plans, delegates, arbitrates, enforces the constitution and produces
/// the final answer.
/// </summary>
/// <remarks>
/// <para>
/// Every request reaches the leader and no one else. The leader reads the brief, its
/// own memory and the project context, then decides one of two things: answer alone, or
/// name the roles it needs and what each must produce. Members are woken only for work
/// the leader actually asked for — a one-line question does not convene a committee.
/// </para>
/// <para>
/// Protocol choice comes from the tier and shapes how members work once they have been
/// summoned. Members never speak to the user directly; only the leader's synthesis is
/// surfaced, which is what gives DXM a single voice.
/// </para>
/// </remarks>
public sealed class AgentOrchestrator(
    IChatProvider provider,
    ISkillRegistry skills,
    Constitution constitution,
    ILogger<AgentOrchestrator> logger,
    IConfigService? config = null,
    IBrainFile? brain = null,
    IProjectMemory? memory = null) : IAgentOrchestrator
{
    private readonly List<Agent> _team = [];
    private ProjectTaskContext? _projectContext;

    public IReadOnlyList<Agent> CurrentTeam => _team;

    public AgentBus Bus { get; } = new();

    public async IAsyncEnumerable<OrchestrationUpdate> RunAsync(
        ModelTier tier,
        IReadOnlyList<ChatMessage> history,
        ProjectTaskContext? context = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Bus.Clear();
        skills.RevokeApprovals();
        _team.Clear();
        _team.AddRange(AgentFactory.BuildTeam(tier, constitution));

        // Applied when each agent's messages are built rather than by rebuilding the
        // Agent objects: Agent carries model, effort, speed and fallback state that a
        // hand-written copy would silently drop.
        _projectContext = context;

        var run = options ?? AgentRunOptions.Default;
        var leader = _team.First(a => a.IsLeader);
        var members = _team.Where(a => !a.IsLeader).ToList();
        var task = history.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
        var projectRoot = context?.ProjectRoot;

        Bus.Post(new BusMessage("user", "User", AgentRole.User, task));

        logger.LogInformation(
            "Leader {Leader} received the request. {Count} member(s) available, protocol {Protocol}, planOnly={PlanOnly}.",
            leader.Model,
            members.Count,
            tier.Protocol,
            run.PlanOnly);

        // 1. The leader alone reads the request and decides what this turn needs.
        var brainNotes = brain is null
            ? string.Empty
            : await brain.ReadAsync(projectRoot, cancellationToken).ConfigureAwait(false);

        var briefing = NewTurn(leader, "Reading the request and deciding the approach.");
        yield return OrchestrationUpdate.Started(briefing);

        var decision = new StringBuilder();
        await foreach (var update in RunAgentAsync(
            leader,
            briefing,
            BuildLeaderBriefPrompt(task, members, brainNotes, run.PlanOnly),
            decision,
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        var plan = LeaderPlan.Parse(decision.ToString());
        var prose = LeaderPlan.StripBlock(decision.ToString());

        Bus.Post(new BusMessage(leader.Id, leader.Name, leader.Role, prose));
        yield return OrchestrationUpdate.Completed(briefing);

        // The leader's memory is written from its own note, not inferred from the
        // transcript, so nothing is recorded that it did not choose to record.
        if (brain is not null && plan.BrainNote is { Length: > 0 } note)
        {
            await brain.AppendAsync(projectRoot, note, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Leader chose {Intent} with {Assignments} assignment(s).",
            plan.Intent,
            plan.Assignments.Count);

        // 2. The approval gate. High and Critical work is never dispatched on an
        //    assumption of consent, so the gate is enforced here rather than requested
        //    in a prompt — a model cannot talk its way past a branch.
        if (plan.Risk.RequiresApproval && !run.ApprovalGranted && !run.PlanOnly)
        {
            await RecordGateAsync(projectRoot, task, plan, cancellationToken).ConfigureAwait(false);

            await foreach (var update in EmitApprovalGateAsync(leader, plan, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        // 3. Plan mode stops here. The plan is the answer.
        if (run.PlanOnly)
        {
            await foreach (var update in EmitPlanAsync(leader, plan, prose, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        // 4. The leader answers alone unless it asked for help.
        var summoned = Summon(members, plan);

        if (plan.Intent == LeaderIntent.Direct || summoned.Count == 0)
        {
            await foreach (var update in SynthesizeAsync(leader, task, history, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var stream = tier.Protocol switch
        {
            TeamProtocol.DebateThenSynthesize =>
                RunDebateAsync(leader, summoned, task, history, cancellationToken),

            TeamProtocol.Pipeline =>
                RunPipelineAsync(leader, summoned, task, history, cancellationToken),

            // leader-delegate is the default for any team tier.
            _ => RunDelegateAsync(leader, summoned, task, history, plan, cancellationToken),
        };

        await foreach (var update in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Narrows the team to the roles the leader actually named.
    /// </summary>
    /// <remarks>
    /// An unparsed plan returns everyone. That is the conservative reading: if the
    /// leader's intent could not be recovered, the turn behaves the way it did before
    /// planning existed rather than silently doing less work than the user expects.
    /// </remarks>
    private static List<Agent> Summon(IReadOnlyList<Agent> members, LeaderPlan plan)
    {
        if (!plan.HasAssignments)
        {
            return plan.Intent == LeaderIntent.Direct ? [] : [.. members];
        }

        var wanted = plan.Assignments.Select(a => a.Role).ToHashSet();
        return [.. members.Where(m => wanted.Contains(m.Role))];
    }

    /// <summary>
    /// Presents the approval gate and stops. Nothing has run at this point.
    /// </summary>
    /// <remarks>
    /// The gate states the tier, why it was assigned, what would change, and how to undo
    /// it. A Critical task with no rollback plan says so explicitly rather than quietly
    /// omitting the line — a missing rollback is exactly the thing the approver needs to
    /// notice before saying yes.
    /// </remarks>
    private async IAsyncEnumerable<OrchestrationUpdate> EmitApprovalGateAsync(
        Agent leader,
        LeaderPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var turn = NewTurn(leader, "Requesting approval before acting.", isFinal: true);
        yield return OrchestrationUpdate.Started(turn);

        var text = new StringBuilder();
        text.Append("**Approval required — ").Append(plan.Risk.Tier).Append(" risk**\n\n");
        text.Append("_Why:_ ").Append(plan.Risk.Reason).Append("\n\n");

        if (plan.Summary is { Length: > 0 } summary)
        {
            text.Append("_What would change:_ ").Append(summary).Append("\n\n");
        }

        if (plan.HasAssignments)
        {
            text.Append("_Steps:_\n");
            for (var i = 0; i < plan.Assignments.Count; i++)
            {
                text.Append('\n').Append(i + 1).Append(". ").Append(plan.Assignments[i].Task);
            }

            text.Append("\n\n");
        }

        text.Append("_Rollback:_ ")
            .Append(plan.RollbackPlan ?? (plan.Risk.RequiresRollbackPlan
                ? "**None supplied.** Critical work must not proceed without one."
                : "Not supplied."))
            .Append("\n\n");

        text.Append("Nothing has been changed. Reply to approve, or say what to change.");

        var answer = text.ToString();
        turn.Status = AgentStatus.Speaking;
        yield return OrchestrationUpdate.Delta(turn, answer);
        yield return OrchestrationUpdate.Final(answer);

        turn.Status = AgentStatus.Done;
        turn.CompletedAt = DateTimeOffset.Now;
        Bus.Post(new BusMessage(leader.Id, leader.Name, leader.Role, answer));
        yield return OrchestrationUpdate.Completed(turn);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Logs the gate to the append-only governance trail.</summary>
    private async Task RecordGateAsync(
        string? projectRoot,
        string task,
        LeaderPlan plan,
        CancellationToken cancellationToken)
    {
        if (memory is null)
        {
            return;
        }

        var entry = new StringBuilder();
        entry.Append("**Approval gate raised** — ").Append(plan.Risk.Tier).Append(" risk\n\n");
        entry.Append("- Request: ").Append(Summarize(task)).Append('\n');
        entry.Append("- Reason: ").Append(plan.Risk.Reason).Append('\n');
        entry.Append("- Rollback: ").Append(plan.RollbackPlan ?? "not supplied").Append('\n');
        entry.Append("- Decision: pending\n");

        await memory
            .AppendAsync(projectRoot, MemoryFile.Governance, entry.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>First line of a request, clipped, for a log entry.</summary>
    private static string Summarize(string text)
    {
        var line = text.AsSpan().Trim();
        var newline = line.IndexOf('\n');
        if (newline >= 0)
        {
            line = line[..newline];
        }

        return line.Length <= 160 ? line.ToString() : string.Concat(line[..157], "...");
    }

    /// <summary>Surfaces the plan as the final answer without executing any of it.</summary>
    private async IAsyncEnumerable<OrchestrationUpdate> EmitPlanAsync(
        Agent leader,
        LeaderPlan plan,
        string prose,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var turn = NewTurn(leader, "Presenting the plan for approval.", isFinal: true);
        yield return OrchestrationUpdate.Started(turn);

        var text = new StringBuilder();
        if (prose.Length > 0)
        {
            text.Append(prose);
        }
        else if (plan.Summary is { Length: > 0 } summary)
        {
            text.Append(summary);
        }
        else
        {
            text.Append("No plan was produced for this request.");
        }

        // The steps are the leader's own assignments, restated as intent rather than as
        // a roster: what would happen, never who would do it.
        if (plan.HasAssignments)
        {
            text.Append("\n\n**Planned steps**\n");
            for (var i = 0; i < plan.Assignments.Count; i++)
            {
                text.Append('\n').Append(i + 1).Append(". ").Append(plan.Assignments[i].Task);
            }
        }

        text.Append("\n\n_Plan mode: nothing has been changed. Turn plan mode off to run this._");

        var answer = text.ToString();
        turn.Status = AgentStatus.Speaking;
        yield return OrchestrationUpdate.Delta(turn, answer);
        yield return OrchestrationUpdate.Final(answer);

        turn.Status = AgentStatus.Done;
        turn.CompletedAt = DateTimeOffset.Now;
        Bus.Post(new BusMessage(leader.Id, leader.Name, leader.Role, answer));
        yield return OrchestrationUpdate.Completed(turn);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ //
    // Protocol: leader-delegate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Members work the subtasks the leader named, then the leader synthesises.
    /// </summary>
    /// <remarks>
    /// The planning step already happened in <see cref="RunAsync"/> — the leader is the
    /// only agent that ever sees the raw request — so this stage starts from a decision
    /// that has already been made, and each member is handed the leader's own words for
    /// its assignment rather than a generic "do your part".
    /// </remarks>
    private async IAsyncEnumerable<OrchestrationUpdate> RunDelegateAsync(
        Agent leader,
        IReadOnlyList<Agent> members,
        string task,
        IReadOnlyList<ChatMessage> history,
        LeaderPlan plan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assignment = plan.Assignments.FirstOrDefault(a => a.Role == member.Role)?.Task
                ?? (string.IsNullOrWhiteSpace(member.Responsibility)
                    ? "Return a concrete deliverable and verification evidence."
                    : member.Responsibility);

            Bus.Post(new BusMessage(
                leader.Id,
                leader.Name,
                leader.Role,
                assignment,
                member.Id));

            var turn = NewTurn(member, $"Working the {member.Role} subtask.");
            yield return OrchestrationUpdate.Started(turn);

            var output = new StringBuilder();
            await foreach (var update in RunAgentAsync(
                member,
                turn,
                BuildMemberPrompt(member, task, assignment),
                output,
                cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            Bus.Post(new BusMessage(member.Id, member.Name, member.Role, output.ToString()));
            yield return OrchestrationUpdate.Completed(turn);
        }

        // The leader synthesises everything on the bus.
        await foreach (var update in SynthesizeAsync(leader, task, history, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // ------------------------------------------------------------------ //
    // Protocol: debate-then-synthesize
    // ------------------------------------------------------------------ //

    /// <summary>Members answer, critique each other on the bus, then the leader arbitrates.</summary>
    private async IAsyncEnumerable<OrchestrationUpdate> RunDebateAsync(
        Agent leader,
        IReadOnlyList<Agent> members,
        string task,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Round 1: independent answers, bounded by maxConcurrentAgents.
        var answerPrompts = members.ToDictionary(
            member => member.Id,
            _ =>
                $"""
                The user asked:

                {task}

                Answer it from your role's perspective. Be specific and concise.
                """);

        await foreach (var update in RunConcurrentRoundAsync(
            members,
            "Answering independently.",
            member => answerPrompts[member.Id],
            cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        // Round 2: critique. Skipped for a lone member, which has nobody to critique.
        if (members.Count > 1)
        {
            var critiquePrompts = members.ToDictionary(
                member => member.Id,
                member =>
                    $"""
                    The team's answers so far:

                    {Bus.Transcribe(member.Id)}

                    Identify the strongest concrete weakness in the others' answers, and
                    concede anything they got right that you missed. One short paragraph.
                    Do not repeat your own answer.
                    """);

            await foreach (var update in RunConcurrentRoundAsync(
                members,
                "Critiquing the other answers.",
                member => critiquePrompts[member.Id],
                cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        // Round 3: the leader arbitrates and answers.
        await foreach (var update in SynthesizeAsync(leader, task, history, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // ------------------------------------------------------------------ //
    // Protocol: pipeline
    // ------------------------------------------------------------------ //

    /// <summary>Planner, Coder, Reviewer in order, with the leader gating each stage.</summary>
    private async IAsyncEnumerable<OrchestrationUpdate> RunPipelineAsync(
        Agent leader,
        IReadOnlyList<Agent> members,
        string task,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var order = new[] { AgentRole.Planner, AgentRole.Coder, AgentRole.Reviewer };
        var ordered = order
            .Select(role => members.FirstOrDefault(m => m.Role == role))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        // Any member not part of the canonical pipeline still runs, at the end.
        ordered.AddRange(members.Where(m => !ordered.Contains(m)));

        foreach (var member in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var turn = NewTurn(member, $"Pipeline stage: {member.Role}.");
            yield return OrchestrationUpdate.Started(turn);

            var output = new StringBuilder();
            await foreach (var update in RunAgentAsync(
                member,
                turn,
                $"""
                The user asked:

                {task}

                Work so far:

                {Bus.Transcribe(member.Id)}

                Take the previous stage's output as your input and do your stage's job.
                """,
                output,
                cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            Bus.Post(new BusMessage(member.Id, member.Name, member.Role, output.ToString()));
            yield return OrchestrationUpdate.Completed(turn);
        }

        await foreach (var update in SynthesizeAsync(leader, task, history, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    // ------------------------------------------------------------------ //
    // Shared steps
    // ------------------------------------------------------------------ //

    private async IAsyncEnumerable<OrchestrationUpdate> RunConcurrentRoundAsync(
        IReadOnlyList<Agent> members,
        string reasoningSummary,
        Func<Agent, string> promptFor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxConcurrent = Math.Clamp(
            config?.Options.Agents.MaxConcurrentAgents ?? 3,
            1,
            8);
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var channel = Channel.CreateUnbounded<OrchestrationUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var tasks = members.Select(async member =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var turn = NewTurn(member, reasoningSummary);
                await channel.Writer
                    .WriteAsync(OrchestrationUpdate.Started(turn), cancellationToken)
                    .ConfigureAwait(false);

                var output = new StringBuilder();
                await foreach (var update in RunAgentAsync(
                    member,
                    turn,
                    promptFor(member),
                    output,
                    cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
                }

                Bus.Post(new BusMessage(member.Id, member.Name, member.Role, output.ToString()));
                await channel.Writer
                    .WriteAsync(OrchestrationUpdate.Completed(turn), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        _ = CompleteRoundAsync(tasks, channel.Writer);

        await foreach (var update in channel.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static async Task CompleteRoundAsync(
        IReadOnlyList<Task> tasks,
        ChannelWriter<OrchestrationUpdate> writer)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    /// <summary>Leader's final, arbitrated answer. Streams as <c>FinalAnswer</c>.</summary>
    private async IAsyncEnumerable<OrchestrationUpdate> SynthesizeAsync(
        Agent leader,
        string task,
        IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var transcript = Bus.Transcribe(leader.Id);
        var finalTurn = NewTurn(leader, "Arbitrating the team transcript and writing the final synthesis.", isFinal: true);
        var finalText = new StringBuilder();
        var finalClock = System.Diagnostics.Stopwatch.StartNew();

        yield return OrchestrationUpdate.Started(finalTurn);

        var messages = BuildMessages(
            leader,
            history,
            $"""
            The user asked:

            {task}

            Your team's full transcript:

            {transcript}

            Write the final answer to the user now. Arbitrate any disagreement and say
            which way you ruled if it mattered. Synthesise into one coherent response —
            do not paste member output verbatim, do not enumerate who said what, and do
            not describe your process unless the user asked about it.
            """);

        var invocation = new ModelInvocation(
            leader.RequestedModel,
            leader.Model,
            leader.EffortProfile,
            leader.Speed);

        await foreach (var delta in provider
            .StreamAsync(invocation, messages, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (delta.Kind == ChatDeltaKind.Content)
            {
                finalTurn.Status = AgentStatus.Speaking;
                finalText.Append(delta.Text);
                yield return OrchestrationUpdate.Delta(finalTurn, delta.Text);
                yield return OrchestrationUpdate.Final(delta.Text);
            }
            else if (delta.Kind == ChatDeltaKind.Usage && delta.Usage is { } usage)
            {
                ApplyUsage(finalTurn, usage);
            }
        }

        finalClock.Stop();
        finalTurn.Latency = finalClock.Elapsed;
        finalTurn.Status = AgentStatus.Done;
        finalTurn.CompletedAt = DateTimeOffset.Now;
        Bus.Post(new BusMessage(
            leader.Id,
            leader.Name,
            leader.Role,
            finalText.ToString()));
        yield return OrchestrationUpdate.Completed(finalTurn);
    }

    /// <summary>Streams one agent's contribution into <paramref name="sink"/>.</summary>
    private async IAsyncEnumerable<OrchestrationUpdate> RunAgentAsync(
        Agent agent,
        AgentTurn turn,
        string prompt,
        StringBuilder sink,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        turn.Status = AgentStatus.Thinking;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var messages = BuildMessages(agent, [], prompt);
        var tools = skills.ToolsFor(agent);
        var invocation = new ModelInvocation(
            agent.RequestedModel,
            agent.Model,
            agent.EffortProfile,
            agent.Speed);

        const int maxProviderRounds = 5;
        for (var round = 0; round < maxProviderRounds; round++)
        {
            var assistantText = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            IAsyncEnumerator<ChatDelta>? enumerator = null;
            var failed = false;

            try
            {
                enumerator = provider
                    .StreamAsync(invocation, messages, tools, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

                while (true)
                {
                    ChatDelta delta;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        delta = enumerator.Current;
                    }
                    catch (ChatProviderException ex)
                    {
                        logger.LogWarning(ex, "Agent {Agent} failed; continuing without it.", agent.Id);
                        turn.Status = AgentStatus.Failed;
                        failed = true;
                        break;
                    }

                    switch (delta.Kind)
                    {
                        case ChatDeltaKind.Content:
                            turn.Status = AgentStatus.Speaking;
                            assistantText.Append(delta.Text);
                            sink.Append(delta.Text);
                            yield return OrchestrationUpdate.Delta(turn, delta.Text);
                            break;

                        case ChatDeltaKind.ToolCall when delta.ToolCall is { } call:
                            toolCalls.Add(call);
                            break;

                        case ChatDeltaKind.Usage when delta.Usage is { } usage:
                            ApplyUsage(turn, usage);
                            break;
                    }
                }
            }
            finally
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (failed)
            {
                clock.Stop();
                turn.Latency = clock.Elapsed;
                yield return OrchestrationUpdate.Warning(agent.Name);
                yield break;
            }

            if (toolCalls.Count == 0)
            {
                break;
            }

            if (round == maxProviderRounds - 1)
            {
                clock.Stop();
                turn.Latency = clock.Elapsed;
                turn.Status = AgentStatus.Failed;
                yield return OrchestrationUpdate.Warning(
                    $"{agent.Name} exceeded the bounded tool-call loop.");
                yield break;
            }

            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = assistantText.ToString(),
                ToolCalls = toolCalls,
                AgentId = agent.Id,
                Model = agent.Model,
            });

            foreach (var call in toolCalls)
            {
                turn.ToolCallCount++;
                var result = await skills
                    .ExecuteAsync(agent, call, cancellationToken)
                    .ConfigureAwait(false);
                var visible = $"\n\n[{call.Name}] {result.Summary ?? result.Content}\n";

                sink.Append(visible);
                yield return OrchestrationUpdate.Delta(turn, visible);

                messages.Add(new ChatMessage
                {
                    Role = ChatRole.Tool,
                    ToolCallId = call.Id,
                    Content = result.Content,
                });
            }
        }

        clock.Stop();
        turn.Latency = clock.Elapsed;
        turn.Status = AgentStatus.Done;
        turn.CompletedAt = DateTimeOffset.Now;
    }

    private List<ChatMessage> BuildMessages(
        Agent agent,
        IReadOnlyList<ChatMessage> history,
        string prompt)
    {
        // AGENTS.md instructions, the resolved .zx0ai layers, any matched SKILL.md
        // package and the authoritative execution boundary are appended after the
        // constitution and role brief. Project text can add house rules; it cannot
        // restate — and thereby weaken — the safety rules it was handed.
        var systemPrompt = _projectContext is null
            ? agent.SystemPrompt
            : _projectContext.ComposeSystemPrompt(agent.SystemPrompt);

        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.System, Content = systemPrompt },
        };

        // Carry prior user/assistant turns so the team has conversational context.
        messages.AddRange(history
            .Where(m => m.Role is ChatRole.User or ChatRole.Assistant)
            .TakeLast(10));

        messages.Add(new ChatMessage { Role = ChatRole.User, Content = prompt });
        return messages;
    }

    /// <summary>
    /// The only prompt that carries the user's raw request. It goes to the leader.
    /// </summary>
    /// <remarks>
    /// The leader is asked to decide before it is allowed to delegate, and to say so in
    /// a machine-readable block. Everything outside that block is ordinary prose, which
    /// is what the customer would see in plan mode; the block itself is stripped.
    /// </remarks>
    private static string BuildLeaderBriefPrompt(
        string task,
        IReadOnlyList<Agent> members,
        string memory,
        bool planOnly)
    {
        var roles = members.Count == 0
            ? "(none — you are working alone)"
            : string.Join(", ", members.Select(m => m.Role.ToString()).Distinct());

        var memorySection = string.IsNullOrWhiteSpace(memory)
            ? "(nothing recorded yet)"
            : memory.Trim();

        var mandate = planOnly
            ? """
              PLAN MODE IS ON. Do not use any skill, do not modify anything, and do not
              produce the final deliverable. Explain what you would do and why, in prose
              the user can approve or correct.
              """
            : """
              Answer the request. Use a role only where it genuinely changes the result;
              for anything you can do well yourself, choose "direct" and do it.
              """;

        return $$"""
            The user asked:

            {{task}}

            {{mandate}}

            Roles you may call on: {{roles}}

            Your notes from earlier work on this project:

            {{memorySection}}

            End your response with this block, and put nothing after it:

            ```dxm-plan
            {
              "mode": "direct" | "delegate",
              "summary": "one sentence on the approach",
              "assignments": [ { "role": "Coder", "task": "what this role must produce" } ],
              "brain": "one durable note worth remembering next session, or omit",
              "risk": "Low" | "Medium" | "High" | "Critical",
              "rollback": "how to undo this, required for Critical"
            }
            ```

            Use "direct" and an empty assignments list when you do not need anyone. Only
            record a "brain" note for something that will still matter next time — a
            decision, a convention, a constraint. Never record the request itself.

            Classify the risk honestly. Anything touching production, authentication,
            personal or customer data, payments, external integrations, or irreversible
            operations is High at minimum. When two tiers could apply, choose the higher.
            """;
    }

    private string BuildMemberPrompt(Agent member, string task, string assignment) =>
        $"""
        The user asked:

        {task}

        The Leader assigned you:

        {assignment}

        The team transcript so far:

        {Bus.Transcribe(member.Id)}

        Do your assigned part now. Stay inside your role.
        """;

    private static AgentTurn NewTurn(Agent agent, string reasoning, bool isFinal = false) => new()
    {
        AgentId = agent.Id,
        AgentName = agent.Name,
        Role = agent.Role,
        Model = agent.Model,
        AccentArgb = agent.AccentArgb,
        ReasoningSummary = reasoning,
        Status = AgentStatus.Thinking,
        IsFinalAnswer = isFinal,
        IsFallbackActive = agent.IsFallbackActive,
    };

    private static void ApplyUsage(AgentTurn turn, ProviderUsage usage)
    {
        turn.PromptTokens += usage.PromptTokens;
        turn.CompletionTokens += usage.CompletionTokens;
        turn.TotalTokens += usage.TotalTokens;
        if (usage.Cost is { } cost)
        {
            turn.EstimatedCost = (turn.EstimatedCost ?? 0m) + cost;
        }
    }
}
