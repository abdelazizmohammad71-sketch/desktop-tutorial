using Xunit;
using ZX0ai.Core.Agents;

namespace ZX0ai.Tests;

/// <summary>The leader's memory: where it lives, what it keeps, and how it fails.</summary>
public sealed class BrainFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(BrainFileTests),
        Guid.NewGuid().ToString("n"));

    private readonly string _appData;

    public BrainFileTests()
    {
        _appData = Path.Combine(_root, "appdata");
        Directory.CreateDirectory(_appData);
    }

    [Fact]
    public void ProjectSession_WritesInsideTheProject()
    {
        var project = Path.Combine(_root, "project");
        var brain = new BrainFile(_appData);

        Assert.Equal(
            Path.Combine(project, ".zx0ai", "brain.md"),
            brain.PathFor(project));
    }

    /// <summary>
    /// A read-only session has no project to write into, but memory should still
    /// survive the session.
    /// </summary>
    [Fact]
    public void ProjectlessSession_FallsBackToAppData()
    {
        var brain = new BrainFile(_appData);

        Assert.Equal(Path.Combine(_appData, "brain.md"), brain.PathFor(null));
        Assert.Equal(Path.Combine(_appData, "brain.md"), brain.PathFor("   "));
    }

    [Fact]
    public async Task MissingBrain_ReadsAsEmpty()
    {
        var brain = new BrainFile(_appData);

        Assert.Equal(string.Empty, await brain.ReadAsync(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public async Task AppendedNotes_SurviveAndAccumulate()
    {
        var project = Path.Combine(_root, "accumulate");
        var brain = new BrainFile(_appData);

        await brain.AppendAsync(project, "Uses tabs, not spaces.");
        await brain.AppendAsync(project, "Tests live under /spec.");

        var content = await brain.ReadAsync(project);

        Assert.Contains("# DXM brain", content, StringComparison.Ordinal);
        Assert.Contains("Uses tabs, not spaces.", content, StringComparison.Ordinal);
        Assert.Contains("Tests live under /spec.", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankNote_WritesNothing()
    {
        var project = Path.Combine(_root, "blank");
        var brain = new BrainFile(_appData);

        await brain.AppendAsync(project, "   ");

        Assert.False(File.Exists(brain.PathFor(project)));
    }

    /// <summary>
    /// Memory is capped, and the notes that survive are the newest ones. An unbounded
    /// file would eventually cost more context than it is worth.
    /// </summary>
    [Fact]
    public async Task Growth_IsCappedAndDropsOldestFirst()
    {
        var project = Path.Combine(_root, "capped");
        var brain = new BrainFile(_appData);

        await brain.AppendAsync(project, "OLDEST-MARKER " + new string('a', 4_000));

        for (var i = 0; i < 6; i++)
        {
            await brain.AppendAsync(project, $"note {i} " + new string('b', 4_000));
        }

        await brain.AppendAsync(project, "NEWEST-MARKER");

        var content = await brain.ReadAsync(project);

        Assert.True(
            content.Length <= BrainFile.MaxCharacters,
            $"Brain grew to {content.Length} characters.");
        Assert.Contains("NEWEST-MARKER", content, StringComparison.Ordinal);
        Assert.DoesNotContain("OLDEST-MARKER", content, StringComparison.Ordinal);
        Assert.Contains("# DXM brain", content, StringComparison.Ordinal);
    }

    /// <summary>A note is never worth failing a turn over.</summary>
    [Fact]
    public async Task UnwritablePath_DoesNotThrow()
    {
        // A file where the directory should be: creating the folder must fail.
        var blocker = Path.Combine(_root, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var brain = new BrainFile(_appData);
        var project = Path.Combine(blocker, "project");

        await brain.AppendAsync(project, "should be dropped silently");

        Assert.Equal(string.Empty, await brain.ReadAsync(project));
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
