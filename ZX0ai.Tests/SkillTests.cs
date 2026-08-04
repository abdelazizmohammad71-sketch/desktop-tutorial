using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Commands;
using ZX0ai.Core.Models;
using ZX0ai.Core.Skills;

namespace ZX0ai.Tests;

/// <summary>A skill that records whether it ran, so gating can be asserted.</summary>
internal sealed class SpySkill(string name, bool destructive = false) : ISkill
{
    public string Name => name;

    public string Description => $"Spy skill {name}.";

    public bool IsDestructive => destructive;

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("value", "string", "Anything.", false));

    public int Executions { get; private set; }

    public Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        _ = arguments;
        _ = context;
        _ = cancellationToken;

        Executions++;
        return Task.FromResult(SkillResult.Ok("done"));
    }
}

/// <summary>A skill that throws, to prove a bad skill cannot kill a run.</summary>
internal sealed class ThrowingSkill : ISkill
{
    public string Name => "explode";

    public string Description => "Always throws.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object();

    public Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("boom");
}

public sealed class SkillRegistryTests
{
    private static ModelTier Tier => new()
    {
        Key = "t",
        DisplayName = "T",
        Mode = TeamMode.Team,
        Leader = "leader/model",
    };

    private static Agent AgentFor(AgentRole role) =>
        AgentFactory.Create(role, $"{role}/model", null, Constitution.Default(), Tier);

    private static SkillRegistry Registry(params ISkill[] skills) =>
        new(skills, Constitution.Default(), NullLogger<SkillRegistry>.Instance);

    private static ToolCall Call(string name) => new("call_1", name, "{}");

    [Fact]
    public async Task AGrantedSkill_Runs()
    {
        var skill = new SpySkill("read_file");
        var registry = Registry(skill);

        var result = await registry.ExecuteAsync(AgentFor(AgentRole.Reviewer), Call("read_file"));

        Assert.True(result.Success);
        Assert.Equal(1, skill.Executions);
    }

    [Fact]
    public async Task AnUngrantedSkill_IsRefusedWithoutRunning()
    {
        // The Reviewer is read-only by grant.
        var skill = new SpySkill("write_file", destructive: true);
        var registry = Registry(skill);

        var result = await registry.ExecuteAsync(AgentFor(AgentRole.Reviewer), Call("write_file"));

        Assert.False(result.Success);
        Assert.Equal(0, skill.Executions);
        Assert.Contains("not granted", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DestructiveSkills_NeedLeaderApproval()
    {
        var skill = new SpySkill("write_file", destructive: true);
        var registry = Registry(skill);
        var coder = AgentFor(AgentRole.Coder);

        var refused = await registry.ExecuteAsync(coder, Call("write_file"));

        Assert.False(refused.Success);
        Assert.Equal(0, skill.Executions);
        Assert.Contains("Leader approval", refused.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AfterLeaderApproval_TheDestructiveSkillRuns()
    {
        var skill = new SpySkill("write_file", destructive: true);
        var registry = Registry(skill);
        var coder = AgentFor(AgentRole.Coder);

        registry.ApproveDestructive(coder.Id);
        var result = await registry.ExecuteAsync(coder, Call("write_file"));

        Assert.True(result.Success);
        Assert.Equal(1, skill.Executions);
    }

    [Fact]
    public async Task TheLeader_NeedsNoApprovalOfItself()
    {
        var skill = new SpySkill("write_file", destructive: true);
        var registry = Registry(skill);

        var result = await registry.ExecuteAsync(AgentFor(AgentRole.Leader), Call("write_file"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RevokingApprovals_RestoresGating()
    {
        var skill = new SpySkill("write_file", destructive: true);
        var registry = Registry(skill);
        var coder = AgentFor(AgentRole.Coder);

        registry.ApproveDestructive(coder.Id);
        registry.RevokeApprovals();

        Assert.False((await registry.ExecuteAsync(coder, Call("write_file"))).Success);
    }

    [Fact]
    public async Task AnUnknownSkill_FailsCleanly()
    {
        var result = await Registry().ExecuteAsync(AgentFor(AgentRole.Leader), Call("nope"));

        Assert.False(result.Success);
        Assert.Contains("No skill named", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingSkill_IsContained()
    {
        var result = await Registry(new ThrowingSkill())
            .ExecuteAsync(AgentFor(AgentRole.Leader), Call("explode"));

        Assert.False(result.Success);
        Assert.Contains("boom", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EverySkillCall_IsAudited_IncludingRefusals()
    {
        var registry = Registry(new SpySkill("write_file", destructive: true));
        var audit = new List<SkillInvocation>();
        registry.SkillInvoked += (_, invocation) => audit.Add(invocation);

        await registry.ExecuteAsync(AgentFor(AgentRole.Coder), Call("write_file"));
        await registry.ExecuteAsync(AgentFor(AgentRole.Leader), Call("write_file"));

        // Constitution rule 5: every invocation is logged, granted or not.
        Assert.Equal(2, audit.Count);
        Assert.False(audit[0].Result.Success);
        Assert.True(audit[1].Result.Success);
    }

    [Fact]
    public void ToolsFor_ReflectsTheRoleGrant()
    {
        var registry = Registry(
            new SpySkill("read_file"),
            new SpySkill("write_file", destructive: true),
            new SpySkill("web_search"));

        var reviewerTools = registry.ToolsFor(AgentFor(AgentRole.Reviewer)).Select(t => t.Name).ToList();
        var leaderTools = registry.ToolsFor(AgentFor(AgentRole.Leader)).Select(t => t.Name).ToList();

        Assert.Equal(["read_file"], reviewerTools);
        Assert.Equal(3, leaderTools.Count);
    }

    [Fact]
    public void ToolDefinition_ProjectsOntoTheOpenAiWireShape()
    {
        var tool = Registry(new SpySkill("read_file")).ToolsFor(AgentFor(AgentRole.Leader))[0];
        var wire = tool.ToWire();

        Assert.Equal("function", wire["type"]);
        var function = Assert.IsType<Dictionary<string, object?>>(wire["function"]);
        Assert.Equal("read_file", function["name"]);
        Assert.NotNull(function["parameters"]);
    }
}

public sealed class CommandRunnerTests
{
    [Theory]
    [InlineData("git status")]
    [InlineData("dotnet build")]
    [InlineData("npm install")]
    [InlineData("ls -la")]
    public void AllowedVerbs_ArePermitted(string command)
    {
        Assert.True(CommandRunner.IsAllowed(command, out _));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("format c:")]
    [InlineData("shutdown /s")]
    [InlineData("reg delete HKLM\\Software")]
    public void UnknownVerbs_AreRefused(string command)
    {
        Assert.False(CommandRunner.IsAllowed(command, out var reason));
        Assert.Contains("allow-list", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("git status && rm -rf /")]
    [InlineData("git status; rm -rf /")]
    [InlineData("git status | sh")]
    [InlineData("echo `whoami`")]
    [InlineData("git status\nrm -rf /")]
    public void ChainingOntoAnAllowedVerb_IsRefused(string command)
    {
        // The whole point of the guard: an allowed first token must not smuggle a
        // second command in behind it.
        Assert.False(CommandRunner.IsAllowed(command, out var reason));
        Assert.Contains("chaining", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAbsolutePathToAnAllowedVerb_IsStillAllowed()
    {
        Assert.True(CommandRunner.IsAllowed("/usr/bin/git status", out _));
        Assert.True(CommandRunner.IsAllowed("C:\\tools\\git.exe status", out _));
    }

    [Fact]
    public void AnUnquotedPathContainingSpaces_IsRefused()
    {
        // The verb is taken as the first whitespace-delimited token, so this parses as
        // "C:\Program" and is refused. Erring toward refusal is the right failure mode
        // for a guard a model can write arbitrary strings into.
        Assert.False(CommandRunner.IsAllowed("C:\\Program Files\\Git\\git.exe status", out _));
    }

    [Fact]
    public void AnEmptyCommand_IsRefused()
    {
        Assert.False(CommandRunner.IsAllowed("   ", out _));
    }

    [Fact]
    public async Task RefusedCommands_NeverStartAProcess()
    {
        var runner = new CommandRunner(NullLogger<CommandRunner>.Instance);
        var started = false;
        runner.CommandStarted += (_, _) => started = true;

        var execution = await runner.RunAsync("rm -rf /", Environment.CurrentDirectory);

        Assert.Equal(-1, execution.ExitCode);
        Assert.False(started);
    }

    [Fact]
    public async Task AnAllowedCommand_RunsAndReportsItsOutput()
    {
        var runner = new CommandRunner(NullLogger<CommandRunner>.Instance);

        var execution = await runner.RunAsync("echo zx0ai-probe", Environment.CurrentDirectory);

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("zx0ai-probe", execution.Output, StringComparison.Ordinal);
    }
}

public sealed class HtmlTextTests
{
    [Fact]
    public void ScriptAndStyle_AreStripped()
    {
        var text = HtmlText.Extract(
            "<html><head><style>body{color:red}</style></head><body><script>alert(1)</script><p>Real text</p></body></html>");

        Assert.Contains("Real text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("color:red", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Entities_AreDecoded()
    {
        Assert.Contains("A & B", HtmlText.Extract("<p>A &amp; B</p>"), StringComparison.Ordinal);
    }

    [Fact]
    public void BlockTags_BecomeLineBreaks()
    {
        var text = HtmlText.Extract("<p>one</p><p>two</p>");

        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.Contains("two", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyInput_IsHandled()
    {
        Assert.Equal(string.Empty, HtmlText.Extract(string.Empty));
    }

    [Fact]
    public void EnsureDocument_WrapsABareFragment()
    {
        var document = HtmlText.EnsureDocument("<h1>Hi</h1>");

        Assert.StartsWith("<!doctype html>", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1>Hi</h1>", document, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureDocument_LeavesAFullDocumentAlone()
    {
        const string full = "<html><body>already</body></html>";
        Assert.Equal(full, HtmlText.EnsureDocument(full));
    }
}
