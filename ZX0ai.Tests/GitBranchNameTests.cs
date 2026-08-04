using Xunit;
using ZX0ai.Core.Git;

namespace ZX0ai.Tests;

/// <summary>
/// Reduces <c>git status --branch</c>'s porcelain line to the label a breadcrumb shows.
/// </summary>
public sealed class GitBranchNameTests
{
    [Theory]
    [InlineData("main", "main")]
    [InlineData("main...origin/main", "main")]
    [InlineData("main...origin/main [ahead 1]", "main")]
    [InlineData("main...origin/main [ahead 1, behind 2]", "main")]
    [InlineData("feature/nice-thing...origin/feature/nice-thing", "feature/nice-thing")]
    public void ExtractsTheLocalBranchFromTrackingInfo(string summary, string expected) =>
        Assert.Equal(expected, GitBranchName.Parse(summary));

    [Fact]
    public void FreshRepositoryWithNoCommits_ReadsTheBranchAfterThePrefix() =>
        Assert.Equal("main", GitBranchName.Parse("No commits yet on main"));

    [Fact]
    public void DetachedHead_HasNoBranchToShow() =>
        Assert.Null(GitBranchName.Parse("HEAD (no branch)"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSummary_HasNoBranchToShow(string? summary) =>
        Assert.Null(GitBranchName.Parse(summary));
}
