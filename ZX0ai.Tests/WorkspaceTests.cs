using Xunit;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(WorkspaceTests),
        Guid.NewGuid().ToString("n"));

    public WorkspaceTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public void SiblingPrefixDoesNotEscapeTheWorkspace()
    {
        var root = Path.Combine(_temp, "project");
        var sibling = Path.Combine(_temp, "project-evil");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);

        Assert.False(WorkspacePathGuard.TryResolveRelative(
            root,
            Path.Combine("..", "project-evil", "payload.txt"),
            out _,
            out _));
    }

    [Fact]
    public void ReparsePointCannotEscapeTheWorkspaceWhenLinksAreSupported()
    {
        var root = Path.Combine(_temp, "project");
        var outside = Path.Combine(_temp, "outside");
        var link = Path.Combine(root, "linked-outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(WorkspacePathGuard.TryResolveRelative(
            root,
            Path.Combine("linked-outside", "payload.txt"),
            out _,
            out _));
    }

    [Fact]
    public async Task WritableSessionCannotExistWithoutAProject()
    {
        var service = new ProjectWorkspaceService(new WorkspaceStatePaths(Path.Combine(_temp, "state")));
        await service.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartChatAsync(projectId: null, readOnlyWithoutProject: false));

        var readOnly = await service.StartChatAsync(projectId: null, readOnlyWithoutProject: true);
        Assert.Equal(SandboxMode.ReadOnly, readOnly.Sandbox);
        Assert.False(service.CurrentWorkspace.HasProject);
        Assert.False(service.CurrentWorkspace.Policy.CanWriteFiles);
    }

    [Fact]
    public async Task ProjectsAreRealDeduplicatedAndPersisted()
    {
        var folder = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(folder);
        var state = new WorkspaceStatePaths(Path.Combine(_temp, "state"));
        var service = new ProjectWorkspaceService(state);
        await service.InitializeAsync();

        var first = await service.AddOrActivateProjectAsync(folder);
        var second = await service.AddOrActivateProjectAsync(folder + Path.DirectorySeparatorChar);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.Projects);

        var session = await service.StartChatAsync(first.Id, readOnlyWithoutProject: false);
        Assert.Equal(SandboxMode.WorkspaceWrite, session.Sandbox);
        Assert.Equal(WorkspacePathGuard.CanonicalizeDirectory(folder), service.CurrentWorkspace.RootPath);

        var reloaded = new ProjectWorkspaceService(state);
        await reloaded.InitializeAsync();
        Assert.Single(reloaded.Projects);
        Assert.Equal(first.Id, reloaded.ActiveProject?.Id);
        Assert.Equal(session.Id, reloaded.ActiveSession?.Id);
    }

    [Fact]
    public async Task RemovingAProjectNeverDeletesItsFolder()
    {
        var folder = Path.Combine(_temp, "keep-me");
        Directory.CreateDirectory(folder);
        var service = new ProjectWorkspaceService(new WorkspaceStatePaths(Path.Combine(_temp, "state")));
        await service.InitializeAsync();
        var project = await service.AddOrActivateProjectAsync(folder);

        await service.RemoveProjectAsync(project.Id);

        Assert.True(Directory.Exists(folder));
        Assert.Empty(service.Projects);
    }

    [Fact]
    public async Task FullAccessRequiresExplicitConfirmation()
    {
        var folder = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(folder);
        var service = new ProjectWorkspaceService(new WorkspaceStatePaths(Path.Combine(_temp, "state")));
        await service.InitializeAsync();
        var project = await service.AddOrActivateProjectAsync(folder);
        await service.StartChatAsync(project.Id, readOnlyWithoutProject: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetExecutionPolicyAsync(new ExecutionPolicy(
                SandboxMode.FullAccess,
                ApprovalPolicy.OnRequest,
                NetworkEnabled: true,
                FullAccessConfirmed: false)));
    }

    [Fact]
    public void CommandPolicyHasRealAllowPromptBlockStates()
    {
        var folder = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(folder);
        var workspace = WorkspaceContext.ForProject("session", "project", folder);
        var policy = new CommandPolicy();

        Assert.Equal(
            CommandPolicyDecision.Allow,
            policy.Evaluate("git status", workspace).Decision);
        Assert.Equal(
            CommandPolicyDecision.Block,
            policy.Evaluate("curl https://example.com", workspace).Decision);

        var full = workspace with
        {
            Policy = new ExecutionPolicy(
                SandboxMode.FullAccess,
                ApprovalPolicy.Never,
                NetworkEnabled: true,
                FullAccessConfirmed: true),
        };

        Assert.Equal(
            CommandPolicyDecision.Prompt,
            policy.Evaluate("git reset --hard", full).Decision);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
