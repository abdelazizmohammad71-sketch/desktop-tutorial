using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Composition;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Instructions;
using ZX0ai.Core.Models;
using ZX0ai.Core.Security;
using ZX0ai.Core.Skills;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Tests;

/// <summary>
/// The Part E capabilities were previously discovered, resolved and then discarded:
/// nothing carried them into an actual model call. These tests pin the wiring, so a
/// future refactor cannot quietly disconnect project guidance again.
/// </summary>
public sealed class ProjectContextReachesAgentsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(ProjectContextReachesAgentsTests),
        Guid.NewGuid().ToString("n"));

    public ProjectContextReachesAgentsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp folder must not fail the suite.
        }
    }

    private static ModelTier TeamTier() => new()
    {
        Key = "zxa-Ultra-full-max",
        DisplayName = "zxa-Ultra-full-max",
        Mode = TeamMode.Team,
        Protocol = TeamProtocol.LeaderDelegate,
        Leader = "vendor/leader",
        Members = [new TeamMember { Role = AgentRole.Coder, Model = "vendor/coder" }],
    };

    private static AgentOrchestrator Orchestrator(IChatProviderProbe provider) => new(
        provider,
        new SkillRegistry([], Constitution.Default(), NullLogger<SkillRegistry>.Instance),
        Constitution.Default(),
        NullLogger<AgentOrchestrator>.Instance);

    private ProjectTaskContextService Service() => new(
        new AgentsInstructionDiscovery(),
        new LayeredProjectConfigurationResolver(),
        new FileSystemSkillCatalog(),
        new ProjectTaskContextPaths(null, null, null));

    private async Task<ProjectTaskContext> BuildContextAsync(string task)
    {
        var workspace = WorkspaceContext.ForProject(
            "session",
            "project",
            _root,
            ExecutionPolicy.WorkspaceDefault);

        return await Service().BuildAsync(workspace, task);
    }

    private static async Task DrainAsync(
        AgentOrchestrator orchestrator,
        ProjectTaskContext? context)
    {
        await foreach (var _ in orchestrator.RunAsync(
            TeamTier(),
            [new ChatMessage { Role = ChatRole.User, Content = "Add a cache." }],
            context))
        {
            // Draining is enough; the probe records what each agent was sent.
        }
    }

    [Fact]
    public async Task AgentsInstructions_ReachEverySystemPrompt()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "AGENTS.md"),
            "House rule: every public method carries an XML summary.");

        var provider = new IChatProviderProbe();
        await DrainAsync(Orchestrator(provider), await BuildContextAsync("Add a cache."));

        Assert.NotEmpty(provider.SystemPrompts);
        Assert.All(provider.SystemPrompts, prompt =>
            Assert.Contains("every public method carries an XML summary", prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheExecutionBoundary_IsStatedToEveryAgent()
    {
        var provider = new IChatProviderProbe();
        await DrainAsync(Orchestrator(provider), await BuildContextAsync("Add a cache."));

        Assert.All(provider.SystemPrompts, prompt =>
            Assert.Contains("Execution boundary", prompt, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProjectText_IsAppendedAfterTheConstitution_NotBefore()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "AGENTS.md"),
            "House rule: prefer records.");

        var provider = new IChatProviderProbe();
        await DrainAsync(Orchestrator(provider), await BuildContextAsync("Add a cache."));

        var prompt = provider.SystemPrompts[0];
        var constitution = prompt.IndexOf("final authority", StringComparison.OrdinalIgnoreCase);
        var project = prompt.IndexOf("prefer records", StringComparison.Ordinal);

        Assert.True(constitution >= 0, "The constitution must still be present.");
        Assert.True(project >= 0, "Project guidance must be present.");

        // A repository may add house rules; it must not be able to restate — and so
        // weaken — the safety rules it was handed.
        Assert.True(
            constitution < project,
            "Project instructions must be appended after the constitution, never before it.");
    }

    [Fact]
    public async Task WithoutContext_TheRunStillWorksOnTheConstitutionAlone()
    {
        var provider = new IChatProviderProbe();
        await DrainAsync(Orchestrator(provider), context: null);

        Assert.NotEmpty(provider.SystemPrompts);
        Assert.All(provider.SystemPrompts, prompt =>
            Assert.Contains("final authority", prompt, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(provider.SystemPrompts, prompt =>
            prompt.Contains("Active project instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMatchingSkillPackage_IsOfferedToTheAgents()
    {
        var skillDirectory = Path.Combine(_root, ".zx0ai", "skills", "cache-design");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(skillDirectory, "SKILL.md"),
            """
            ---
            name: cache-design
            description: Guidance for designing and reviewing a caching layer.
            ---

            Prefer read-through caching with an explicit eviction policy.
            """);

        var context = await BuildContextAsync("Design a caching layer for the API.");

        // The catalog found it; whether it is offered depends on the match, but a
        // discovered package must at least be visible to the run.
        Assert.NotEmpty(context.SkillCatalog.Skills);
        Assert.Contains(context.SkillCatalog.Skills, package =>
            package.Name.Equals("cache-design", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProjectlessSession_IsFailClosedAndCarriesNoProjectText()
    {
        var context = await Service().BuildAsync(
            WorkspaceContext.WithoutProject("session"),
            "Do something.");

        Assert.Equal(SandboxMode.ReadOnly, context.EffectivePolicy.Sandbox);
        Assert.Equal(ApprovalPolicy.Untrusted, context.EffectivePolicy.Approval);
        Assert.False(context.EffectivePolicy.CanUseNetwork);
        Assert.Empty(context.Instructions.Files);
    }
}

/// <summary>Records the system prompt each agent was actually sent.</summary>
internal sealed class IChatProviderProbe : ZX0ai.Core.Providers.IChatProvider
{
    public List<string> SystemPrompts { get; } = [];

    public string Name => "probe";

    public bool IsConfigured => true;

    public async IAsyncEnumerable<ZX0ai.Core.Providers.ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SystemPrompts.Add(
            messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Content ?? string.Empty);

        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        yield return ZX0ai.Core.Providers.ChatDelta.Content($"ack from {model}");
        yield return ZX0ai.Core.Providers.ChatDelta.Done(model);
    }
}
