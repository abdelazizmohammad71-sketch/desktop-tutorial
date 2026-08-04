using Xunit;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Instructions;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Tests;

public sealed class AgentsInstructionDiscoveryTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(AgentsInstructionDiscoveryTests),
        Guid.NewGuid().ToString("n"));

    public AgentsInstructionDiscoveryTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task LoadsRootFirstAndStopsAtTheWorkingDirectory()
    {
        var root = Path.Combine(_temp, "repo");
        var child = Path.Combine(root, "src");
        var cwd = Path.Combine(child, "feature");
        Directory.CreateDirectory(cwd);
        await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "root guidance");
        await File.WriteAllTextAsync(Path.Combine(child, "ZX0AI.md"), "child guidance");
        await File.WriteAllTextAsync(Path.Combine(cwd, ".agents.md"), "task guidance");

        var workspace = WorkspaceContext.ForProject("s", "p", root) with
        {
            WorkingDirectory = cwd,
        };
        var result = await new AgentsInstructionDiscovery().DiscoverAsync(workspace);

        Assert.Equal(
            ["AGENTS.md", "src/ZX0AI.md", "src/feature/.agents.md"],
            result.Files.Select(file => file.RelativePath));
        Assert.True(
            result.ToPromptText().IndexOf("root guidance", StringComparison.Ordinal) <
            result.ToPromptText().IndexOf("task guidance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsesOnlyTheFirstFallbackNamePerDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_temp, "repo"));
        var root = Path.Combine(_temp, "repo");
        await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "preferred");
        await File.WriteAllTextAsync(Path.Combine(root, "ZX0AI.md"), "fallback");

        var result = await new AgentsInstructionDiscovery().DiscoverAsync(
            WorkspaceContext.ForProject("s", "p", root));

        var file = Assert.Single(result.Files);
        Assert.Equal("AGENTS.md", file.RelativePath);
        Assert.Equal("preferred", file.Content);
    }

    [Fact]
    public async Task EnforcesByteCaps()
    {
        var root = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), new string('x', 100));
        var discovery = new AgentsInstructionDiscovery(new AgentsInstructionDiscoveryOptions
        {
            MaxFileBytes = 12,
            MaxTotalBytes = 12,
            MaxFiles = 2,
        });

        var result = await discovery.DiscoverAsync(WorkspaceContext.ForProject("s", "p", root));

        var file = Assert.Single(result.Files);
        Assert.Equal(12, file.BytesRead);
        Assert.True(file.IsTruncated);
        Assert.Contains(result.Diagnostics, note => note.Contains("truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsAWorkingDirectoryOutsideTheProject()
    {
        var root = Path.Combine(_temp, "repo");
        var outside = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var workspace = WorkspaceContext.ForProject("s", "p", root) with
        {
            WorkingDirectory = outside,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentsInstructionDiscovery().DiscoverAsync(workspace));
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

public sealed class LayeredProjectConfigurationResolverTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(LayeredProjectConfigurationResolverTests),
        Guid.NewGuid().ToString("n"));

    public LayeredProjectConfigurationResolverTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task ResolvesRootToCwdWithOnlyAllowlistedFields()
    {
        var root = Path.Combine(_temp, "repo");
        var cwd = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(root, ".zx0ai"));
        Directory.CreateDirectory(Path.Combine(cwd, ".zx0ai"));
        var shipped = Path.Combine(_temp, "shipped.json");
        await File.WriteAllTextAsync(shipped, """
            {
              "sandbox_mode": "workspace-write",
              "approval_policy": "on-request",
              "network_access": false,
              "default_tier": "base",
              "enabled_skills": ["review", "tests"]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, ".zx0ai", "config.json"), """
            {
              "defaultTier": "root-tier",
              "enabledSkills": ["review"],
              "arbitraryCommand": "format c:"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(cwd, ".zx0ai", "config.json"), """
            { "default_tier": "nested-tier" }
            """);

        var result = await new LayeredProjectConfigurationResolver().ResolveAsync(new()
        {
            ProjectRoot = root,
            WorkingDirectory = cwd,
            ShippedConfigPath = shipped,
        });

        Assert.Equal(SandboxMode.WorkspaceWrite, result.SandboxMode);
        Assert.Equal(ApprovalPolicy.OnRequest, result.ApprovalPolicy);
        Assert.Equal("nested-tier", result.DefaultTier);
        Assert.Equal(["review"], result.EnabledSkills);
        Assert.Contains(
            result.Layers.SelectMany(layer => layer.Notes),
            note => note.Contains("non-allowlisted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectNestedAndTaskLayersCannotEscalateAuthority()
    {
        var root = Path.Combine(_temp, "repo");
        var cwd = Path.Combine(root, "deep");
        Directory.CreateDirectory(Path.Combine(root, ".zx0ai"));
        Directory.CreateDirectory(Path.Combine(cwd, ".zx0ai"));
        var shipped = Path.Combine(_temp, "shipped.json");
        await File.WriteAllTextAsync(shipped, """
            {
              "sandbox_mode": "workspace-write",
              "approval_policy": "on-request",
              "network_access": false,
              "enabled_skills": ["review"]
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, ".zx0ai", "config.json"), """
            {
              "sandbox_mode": "read-only",
              "approval_policy": "untrusted"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(cwd, ".zx0ai", "config.json"), """
            {
              "sandbox_mode": "full-access",
              "approval_policy": "never",
              "network_access": true,
              "enabled_skills": ["review", "unknown"]
            }
            """);

        var result = await new LayeredProjectConfigurationResolver().ResolveAsync(new()
        {
            ProjectRoot = root,
            WorkingDirectory = cwd,
            ShippedConfigPath = shipped,
            TaskOverridesJson = """
                { "sandbox_mode": "full-access", "network_access": true }
                """,
        });

        Assert.Equal(SandboxMode.ReadOnly, result.SandboxMode);
        Assert.Equal(ApprovalPolicy.Untrusted, result.ApprovalPolicy);
        Assert.False(result.NetworkAccess);
        Assert.Equal(["review"], result.EnabledSkills);
        Assert.Contains(
            result.Layers.SelectMany(layer => layer.Notes),
            note => note.Contains("escalation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TrustedAutoProfileStillRequiresSeparateFullAccessConfirmation()
    {
        var root = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(root);
        var user = Path.Combine(_temp, "user.json");
        await File.WriteAllTextAsync(user, """
            {
              "profiles": {
                "auto": {
                  "sandbox_mode": "full-access",
                  "approval_policy": "never",
                  "network_access": true
                }
              }
            }
            """);

        var result = await new LayeredProjectConfigurationResolver().ResolveAsync(new()
        {
            ProjectRoot = root,
            UserConfigPath = user,
            ActiveProfile = "auto",
        });

        Assert.Equal(SandboxMode.FullAccess, result.SandboxMode);
        Assert.False(result.ToExecutionPolicy().FullAccessConfirmed);
        Assert.False(result.ToExecutionPolicy().CanRunCommands);
        Assert.True(result.ToExecutionPolicy(fullAccessConfirmed: true).CanRunCommands);
    }

    [Fact]
    public async Task MalformedLayerFailsClosed()
    {
        var root = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(root);
        var shipped = Path.Combine(_temp, "broken.json");
        await File.WriteAllTextAsync(shipped, "{ broken");

        var result = await new LayeredProjectConfigurationResolver().ResolveAsync(new()
        {
            ProjectRoot = root,
            ShippedConfigPath = shipped,
        });

        Assert.Equal(SandboxMode.ReadOnly, result.SandboxMode);
        Assert.Equal(ApprovalPolicy.Untrusted, result.ApprovalPolicy);
        Assert.Contains(result.Diagnostics, note => note.Contains("valid JSON", StringComparison.Ordinal));
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
