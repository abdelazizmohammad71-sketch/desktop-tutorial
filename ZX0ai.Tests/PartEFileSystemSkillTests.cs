using Xunit;
using ZX0ai.Core.Security;
using ZX0ai.Core.Skills;

namespace ZX0ai.Tests;

public sealed class FileSystemSkillCatalogTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(FileSystemSkillCatalogTests),
        Guid.NewGuid().ToString("n"));

    public FileSystemSkillCatalogTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task DiscoversUserAndProjectSkillMetadataWithoutRunningScripts()
    {
        var userRoot = Path.Combine(_temp, "user-skills");
        var userSkill = Path.Combine(userRoot, "review");
        var project = Path.Combine(_temp, "repo");
        var projectSkill = Path.Combine(project, ".zx0ai", "skills", "docs");
        Directory.CreateDirectory(Path.Combine(userSkill, "scripts"));
        Directory.CreateDirectory(Path.Combine(projectSkill, "assets"));
        await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), """
            ---
            name: code-review
            description: Review source code for correctness and security.
            ---
            Inspect the supplied source and report actionable findings.
            """);
        var marker = Path.Combine(_temp, "must-not-exist.txt");
        await File.WriteAllTextAsync(
            Path.Combine(userSkill, "scripts", "run.cmd"),
            $"echo unsafe > \"{marker}\"");
        await File.WriteAllTextAsync(Path.Combine(projectSkill, "SKILL.md"), """
            ---
            name: docs
            description: Write concise project documentation and guides.
            ---
            Preserve the project's terminology.
            """);

        var catalog = await new FileSystemSkillCatalog().DiscoverAsync(project, userRoot);

        Assert.Equal(2, catalog.Skills.Count);
        Assert.True(catalog.Skills.Single(skill => skill.Name == "code-review").HasScripts);
        Assert.True(catalog.Skills.Single(skill => skill.Name == "docs").HasAssets);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task SkillsAreOffByDefaultEvenForExplicitInvocation()
    {
        var userRoot = await CreateReviewSkillAsync();
        var service = new FileSystemSkillCatalog();
        var catalog = await service.DiscoverAsync(projectRoot: null, userRoot);

        var match = service.Match(
            catalog,
            "Please use $code-review now.",
            enabledSkillNames: [],
            ExecutionPolicy.WorkspaceDefault);

        Assert.Null(match);
    }

    [Fact]
    public async Task ExplicitEnabledSkillWinsAndKeepsTheSamePolicy()
    {
        var userRoot = await CreateReviewSkillAsync();
        var service = new FileSystemSkillCatalog();
        var catalog = await service.DiscoverAsync(projectRoot: null, userRoot);
        var policy = new ExecutionPolicy(
            SandboxMode.ReadOnly,
            ApprovalPolicy.Untrusted,
            NetworkEnabled: false);

        var match = service.Match(
            catalog,
            "Please use $code-review now.",
            ["code-review"],
            policy);

        Assert.NotNull(match);
        Assert.Equal(SkillMatchKind.Explicit, match.Kind);
        Assert.Equal(policy, match.EffectivePolicy);
        Assert.False(match.EffectivePolicy.CanWriteFiles);
    }

    [Fact]
    public async Task DescriptionMatchingTriggersOnlyAnEnabledRelevantSkill()
    {
        var userRoot = await CreateReviewSkillAsync();
        var service = new FileSystemSkillCatalog();
        var catalog = await service.DiscoverAsync(projectRoot: null, userRoot);

        var match = service.Match(
            catalog,
            "Review this source code for security vulnerabilities and correctness.",
            ["code-review"],
            ExecutionPolicy.ReadOnly);

        Assert.NotNull(match);
        Assert.Equal("code-review", match.Skill.Name);
        Assert.Equal(SkillMatchKind.Description, match.Kind);
    }

    [Fact]
    public async Task MalformedFrontmatterIsIgnored()
    {
        var root = Path.Combine(_temp, "skills");
        var invalid = Path.Combine(root, "invalid");
        Directory.CreateDirectory(invalid);
        await File.WriteAllTextAsync(
            Path.Combine(invalid, "SKILL.md"),
            "name: no-frontmatter\nDo something");

        var catalog = await new FileSystemSkillCatalog().DiscoverAsync(null, root);

        Assert.Empty(catalog.Skills);
        Assert.Contains(catalog.Diagnostics, note => note.Contains("frontmatter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SkillInstructionReadIsByteBounded()
    {
        var root = Path.Combine(_temp, "skills");
        var folder = Path.Combine(root, "large");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), $$"""
            ---
            name: large-skill
            description: Analyze unusually large payloads safely.
            ---
            {{new string('x', 4096)}}
            """);
        var service = new FileSystemSkillCatalog(new FileSystemSkillCatalogOptions
        {
            MaxSkillFileBytes = 1024,
        });

        var catalog = await service.DiscoverAsync(null, root);

        Assert.True(Assert.Single(catalog.Skills).IsTruncated);
    }

    private async Task<string> CreateReviewSkillAsync()
    {
        var root = Path.Combine(_temp, "skills");
        var folder = Path.Combine(root, "review");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), """
            ---
            name: code-review
            description: Review source code for correctness and security vulnerabilities.
            ---
            Inspect code carefully and return concise findings.
            """);
        return root;
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
