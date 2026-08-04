using Xunit;
using ZX0ai.Core.Governance;

namespace ZX0ai.Tests;

/// <summary>The four memory files: where they live, and which ones may be rewritten.</summary>
public sealed class ProjectMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(ProjectMemoryTests),
        Guid.NewGuid().ToString("n"));

    private readonly string _appData;

    public ProjectMemoryTests()
    {
        _appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_appData);
    }

    [Theory]
    [InlineData(MemoryFile.Planning, "planning.md")]
    [InlineData(MemoryFile.Progress, "progress.md")]
    [InlineData(MemoryFile.Reports, "reports.md")]
    [InlineData(MemoryFile.Governance, "governance.md")]
    public void Files_LiveBesideBrainInTheProject(MemoryFile file, string expected)
    {
        var project = Path.Combine(_root, "project");
        var memory = new ProjectMemory(_appData);

        Assert.Equal(
            Path.Combine(project, ".zx0ai", expected),
            memory.PathFor(project, file));
    }

    [Fact]
    public void ProjectlessSession_FallsBackToAppData()
    {
        var memory = new ProjectMemory(_appData);

        Assert.Equal(
            Path.Combine(_appData, "governance.md"),
            memory.PathFor(null, MemoryFile.Governance));
    }

    [Fact]
    public async Task PlanningAndProgress_AreRewritten()
    {
        var project = Path.Combine(_root, "rewrite");
        var memory = new ProjectMemory(_appData);

        await memory.WriteAsync(project, MemoryFile.Planning, "First plan.");
        await memory.WriteAsync(project, MemoryFile.Planning, "Second plan.");

        var content = await memory.ReadAsync(project, MemoryFile.Planning);

        Assert.Contains("Second plan.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("First plan.", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The audit trail must accumulate. A log that can be overwritten is not a log, so
    /// the type refuses the wrong call rather than silently doing the wrong thing.
    /// </summary>
    [Fact]
    public async Task ReportsAndGovernance_AccumulateAndRefuseRewrites()
    {
        var project = Path.Combine(_root, "audit");
        var memory = new ProjectMemory(_appData);

        await memory.AppendAsync(project, MemoryFile.Governance, "Gate raised for T1.");
        await memory.AppendAsync(project, MemoryFile.Governance, "Approved by the user.");

        var content = await memory.ReadAsync(project, MemoryFile.Governance);

        Assert.Contains("Gate raised for T1.", content, StringComparison.Ordinal);
        Assert.Contains("Approved by the user.", content, StringComparison.Ordinal);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memory.WriteAsync(project, MemoryFile.Governance, "erase the history"));
    }

    [Fact]
    public async Task RewrittenFiles_RefuseAppends()
    {
        var project = Path.Combine(_root, "refuse");
        var memory = new ProjectMemory(_appData);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memory.AppendAsync(project, MemoryFile.Planning, "sneak an entry in"));
    }

    [Fact]
    public async Task MissingFile_ReadsAsEmpty()
    {
        var memory = new ProjectMemory(_appData);

        Assert.Equal(
            string.Empty,
            await memory.ReadAsync(Path.Combine(_root, "nowhere"), MemoryFile.Reports));
    }

    [Fact]
    public async Task BlankContent_WritesNothing()
    {
        var project = Path.Combine(_root, "blank");
        var memory = new ProjectMemory(_appData);

        await memory.WriteAsync(project, MemoryFile.Planning, "   ");

        Assert.False(File.Exists(memory.PathFor(project, MemoryFile.Planning)));
    }

    /// <summary>A note is never worth failing a turn over.</summary>
    [Fact]
    public async Task UnwritablePath_DoesNotThrow()
    {
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var memory = new ProjectMemory(_appData);
        var project = Path.Combine(blocker, "project");

        await memory.AppendAsync(project, MemoryFile.Reports, "dropped silently");

        Assert.Equal(string.Empty, await memory.ReadAsync(project, MemoryFile.Reports));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }
}
