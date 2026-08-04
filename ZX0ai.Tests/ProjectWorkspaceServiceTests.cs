using Xunit;
using ZX0ai.Core.Projects;

namespace ZX0ai.Tests;

/// <summary>
/// The project list itself: add, pin, rename, archive, remove.
/// </summary>
/// <remarks>
/// Chat persistence for a project is covered by <see cref="WorkspaceTests"/> and the
/// path-guard suite; this file is about the record-keeping around a project, which had
/// no test coverage at all before the Projects panel gave it a caller.
/// </remarks>
public sealed class ProjectWorkspaceServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(ProjectWorkspaceServiceTests),
        Guid.NewGuid().ToString("n"));

    public ProjectWorkspaceServiceTests() => Directory.CreateDirectory(_temp);

    private ProjectWorkspaceService CreateService() =>
        new(new WorkspaceStatePaths(Path.Combine(_temp, "state")));

    private string CreateFolder(string name)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task AddingTheSameFolderTwice_ActivatesTheExistingProjectInstead()
    {
        var service = CreateService();
        await service.InitializeAsync();

        var folder = CreateFolder("alpha");
        var first = await service.AddOrActivateProjectAsync(folder);
        var second = await service.AddOrActivateProjectAsync(folder);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.Projects);
    }

    [Fact]
    public async Task PinnedProjects_SortBeforeUnpinnedRegardlessOfRecency()
    {
        var service = CreateService();
        await service.InitializeAsync();

        var older = await service.AddOrActivateProjectAsync(CreateFolder("older"));
        var newer = await service.AddOrActivateProjectAsync(CreateFolder("newer"));
        await service.SetPinnedAsync(older.Id, pinned: true);

        Assert.Equal(older.Id, service.Projects[0].Id);
        Assert.Equal(newer.Id, service.Projects[1].Id);
    }

    /// <summary>
    /// A rename must survive the next launch even though the folder's own name did not
    /// change — this pins the regression where startup re-derived the name from disk
    /// and silently reverted every rename.
    /// </summary>
    [Fact]
    public async Task RenameSurvivesReload()
    {
        var paths = new WorkspaceStatePaths(Path.Combine(_temp, "state"));
        var folder = CreateFolder("original-name");

        var first = new ProjectWorkspaceService(paths);
        await first.InitializeAsync();
        var project = await first.AddOrActivateProjectAsync(folder);
        await first.RenameProjectAsync(project.Id, "My Renamed Project");

        var second = new ProjectWorkspaceService(paths);
        await second.InitializeAsync();

        Assert.Equal("My Renamed Project", second.Projects.Single().Name);
    }

    [Fact]
    public async Task RenameToEmpty_IsRejected()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var project = await service.AddOrActivateProjectAsync(CreateFolder("beta"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RenameProjectAsync(project.Id, "   "));
    }

    [Fact]
    public async Task ArchivingAProject_RemovesItFromTheWorkingListButKeepsIt()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var project = await service.AddOrActivateProjectAsync(CreateFolder("gamma"));

        await service.SetArchivedAsync(project.Id, archived: true);

        Assert.Empty(service.Projects);
        Assert.Single(service.ArchivedProjects);
        Assert.Equal(project.Id, service.ArchivedProjects[0].Id);

        await service.SetArchivedAsync(project.Id, archived: false);

        Assert.Single(service.Projects);
        Assert.Empty(service.ArchivedProjects);
    }

    [Fact]
    public async Task ArchivingTheActiveProject_ClearsTheActiveSession()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var project = await service.AddOrActivateProjectAsync(CreateFolder("delta"));
        await service.StartChatAsync(project.Id, readOnlyWithoutProject: false);

        Assert.NotNull(service.ActiveSession);

        await service.SetArchivedAsync(project.Id, archived: true);

        Assert.Null(service.ActiveSession);
    }

    [Fact]
    public async Task RemovingAProject_NeverDeletesItsFolder()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var folder = CreateFolder("epsilon");
        var project = await service.AddOrActivateProjectAsync(folder);

        await service.RemoveProjectAsync(project.Id);

        Assert.True(Directory.Exists(folder));
        Assert.Empty(service.Projects);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
