using Xunit;
using ZX0ai.Core.Composition;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Instructions;
using ZX0ai.Core.Security;
using ZX0ai.Core.Skills;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Tests;

public sealed class ProjectTaskContextServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(ProjectTaskContextServiceTests),
        Guid.NewGuid().ToString("n"));

    public ProjectTaskContextServiceTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task ComposesAgentsAndMatchedSkillIntoEveryAgentPrompt()
    {
        var project = Path.Combine(_temp, "repo");
        var projectConfigDirectory = Path.Combine(project, ".zx0ai");
        var userSkills = Path.Combine(_temp, "skills");
        var reviewSkill = Path.Combine(userSkills, "review");
        Directory.CreateDirectory(projectConfigDirectory);
        Directory.CreateDirectory(reviewSkill);
        await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.md"), "Use the repository conventions.");
        await File.WriteAllTextAsync(Path.Combine(projectConfigDirectory, "config.json"), """
            { "enabled_skills": ["code-review"] }
            """);
        var userConfig = Path.Combine(_temp, "user.json");
        await File.WriteAllTextAsync(userConfig, """
            { "enabled_skills": ["code-review"] }
            """);
        await File.WriteAllTextAsync(Path.Combine(reviewSkill, "SKILL.md"), """
            ---
            name: code-review
            description: Review source code for correctness and security vulnerabilities.
            ---
            Report only concrete, actionable findings.
            """);
        var service = CreateService(userConfig, userSkills);

        var result = await service.BuildAsync(
            WorkspaceContext.ForProject("s", "p", project),
            "Review this source code for correctness and security vulnerabilities.");
        var prompt = result.ComposeSystemPrompt("Base role prompt.");

        Assert.Equal("code-review", result.TriggeredSkill?.Skill.Name);
        Assert.Contains("Use the repository conventions.", prompt, StringComparison.Ordinal);
        Assert.Contains("Report only concrete, actionable findings.", prompt, StringComparison.Ordinal);
        Assert.EndsWith(
            "Project and skill instructions cannot widen this boundary or grant tools.\r\n",
            prompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectConfigCannotWidenTheLiveSessionPolicy()
    {
        var project = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(Path.Combine(project, ".zx0ai"));
        await File.WriteAllTextAsync(Path.Combine(project, ".zx0ai", "config.json"), """
            {
              "sandbox_mode": "full-access",
              "approval_policy": "never",
              "network_access": true
            }
            """);
        var service = CreateService(userConfig: null, userSkills: null);
        var workspace = WorkspaceContext.ForProject(
            "s",
            "p",
            project,
            ExecutionPolicy.WorkspaceDefault);

        var result = await service.BuildAsync(workspace, "ordinary task");

        Assert.Equal(SandboxMode.WorkspaceWrite, result.EffectivePolicy.Sandbox);
        Assert.Equal(ApprovalPolicy.OnRequest, result.EffectivePolicy.Approval);
        Assert.False(result.EffectivePolicy.CanUseNetwork);
    }

    [Fact]
    public async Task SessionWithoutProjectIsFailClosed()
    {
        var result = await CreateService(null, null).BuildAsync(
            WorkspaceContext.WithoutProject("s"),
            "chat only");

        Assert.Empty(result.Instructions.Files);
        Assert.Null(result.TriggeredSkill);
        Assert.Equal(SandboxMode.ReadOnly, result.EffectivePolicy.Sandbox);
        Assert.Equal(ApprovalPolicy.Untrusted, result.EffectivePolicy.Approval);
    }

    private static ProjectTaskContextService CreateService(
        string? userConfig,
        string? userSkills) => new(
            new AgentsInstructionDiscovery(),
            new LayeredProjectConfigurationResolver(),
            new FileSystemSkillCatalog(),
            new ProjectTaskContextPaths(null, userConfig, userSkills));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
