using Xunit;
using ZX0ai.Core.Git;
using ZX0ai.Core.Mcp;
using ZX0ai.Core.Profiles;
using ZX0ai.Core.Security;

namespace ZX0ai.Tests;

public sealed class McpConfigurationTests
{
    [Fact]
    public void HttpServerNeedsExactApprovalAndNetworkAuthority()
    {
        var server = new McpServerConfiguration
        {
            Name = "docs",
            Enabled = true,
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = new Uri("https://mcp.example.test/api"),
            HeaderEnvironmentVariables = new Dictionary<string, string>
            {
                ["Authorization"] = "DOCS_MCP_AUTH",
            },
        };
        var fingerprint = McpActivationPolicy.ComputeFingerprint(server);

        var notApproved = McpActivationPolicy.Evaluate(
            server,
            new ExecutionPolicy(
                SandboxMode.WorkspaceWrite,
                ApprovalPolicy.OnRequest,
                NetworkEnabled: true),
            []);
        var noNetwork = McpActivationPolicy.Evaluate(
            server,
            ExecutionPolicy.WorkspaceDefault,
            [fingerprint]);
        var allowed = McpActivationPolicy.Evaluate(
            server,
            new ExecutionPolicy(
                SandboxMode.WorkspaceWrite,
                ApprovalPolicy.OnRequest,
                NetworkEnabled: true),
            [fingerprint]);

        Assert.False(notApproved.CanActivate);
        Assert.Contains("explicit approval", notApproved.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(noNetwork.CanActivate);
        Assert.True(allowed.CanActivate);
    }

    [Fact]
    public void StdioServerNeedsConfirmedFullAccess()
    {
        var server = new McpServerConfiguration
        {
            Name = "local-tools",
            Enabled = true,
            Transport = McpTransportKind.Stdio,
            Command = "mcp-server.exe",
            Arguments = ["--stdio"],
        };
        var fingerprint = McpActivationPolicy.ComputeFingerprint(server);

        var denied = McpActivationPolicy.Evaluate(
            server,
            ExecutionPolicy.WorkspaceDefault,
            [fingerprint]);
        var allowed = McpActivationPolicy.Evaluate(
            server,
            new ExecutionPolicy(
                SandboxMode.FullAccess,
                ApprovalPolicy.OnRequest,
                NetworkEnabled: false,
                FullAccessConfirmed: true),
            [fingerprint]);

        Assert.False(denied.CanActivate);
        Assert.True(allowed.CanActivate);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell")]
    [InlineData("bash")]
    public void ShellLaunchersAreRejected(string command)
    {
        var validation = McpServerValidator.Validate(new McpServerConfiguration
        {
            Name = "unsafe",
            Enabled = true,
            Transport = McpTransportKind.Stdio,
            Command = command,
            Arguments = ["-c", "anything"],
        });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "shell_forbidden");
    }

    [Fact]
    public void UrlCredentialsAndQueriesAreRejected()
    {
        var validation = McpServerValidator.Validate(new McpServerConfiguration
        {
            Name = "unsafe-http",
            Enabled = true,
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = new Uri("https://user:pass@example.test/mcp?token=secret"),
        });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "endpoint_credentials");
    }

    [Fact]
    public void EditingConfigurationInvalidatesAnApprovalFingerprint()
    {
        var original = new McpServerConfiguration
        {
            Name = "server",
            Enabled = true,
            Transport = McpTransportKind.Stdio,
            Command = "server.exe",
            Arguments = ["--one"],
        };
        var edited = original with { Arguments = ["--two"] };

        Assert.NotEqual(
            McpActivationPolicy.ComputeFingerprint(original),
            McpActivationPolicy.ComputeFingerprint(edited));
    }
}

public sealed class ExecutionProfileCatalogTests
{
    [Fact]
    public void StrictProfileActivatesReadOnlyAndUntrusted()
    {
        var activation = new ExecutionProfileCatalog().Activate("strict");

        Assert.True(activation.Activated);
        Assert.Equal(SandboxMode.ReadOnly, activation.Policy?.Sandbox);
        Assert.Equal(ApprovalPolicy.Untrusted, activation.Policy?.Approval);
        Assert.False(activation.Policy?.CanWriteFiles);
    }

    [Fact]
    public void AutoProfileCannotGrantItselfFullAccess()
    {
        var catalog = new ExecutionProfileCatalog();

        var pending = catalog.Activate("auto");
        var confirmed = catalog.Activate("auto", fullAccessConfirmed: true);

        Assert.False(pending.Activated);
        Assert.True(pending.NeedsFullAccessConfirmation);
        Assert.False(pending.Policy?.CanRunCommands);
        Assert.True(confirmed.Activated);
        Assert.True(confirmed.Policy?.CanRunCommands);
        Assert.True(confirmed.Policy?.CanUseNetwork);
        Assert.Equal(ApprovalPolicy.Never, confirmed.Policy?.Approval);
    }

    [Fact]
    public void CustomNamedProfileIsSwitchable()
    {
        var catalog = new ExecutionProfileCatalog(
        [
            new ExecutionProfile
            {
                Name = "review",
                DisplayName = "Review only",
                SandboxMode = SandboxMode.ReadOnly,
                ApprovalPolicy = ApprovalPolicy.OnRequest,
                DefaultTier = "zxa-low",
                EnabledSkills = new HashSet<string>(["code-review"]),
            },
        ]);

        var activation = catalog.Activate("review");

        Assert.True(activation.Activated);
        Assert.Equal("zxa-low", activation.Profile?.DefaultTier);
        Assert.Contains("code-review", Assert.IsType<ExecutionProfile>(activation.Profile).EnabledSkills);
    }
}

public sealed class GitRepositoryServiceTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(GitRepositoryServiceTests),
        Guid.NewGuid().ToString("n"));

    public GitRepositoryServiceTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task ReportsAnHonestNotRepositoryState()
    {
        var executor = new QueueGitExecutor(new GitCommandResult(
            128,
            string.Empty,
            "fatal: not a git repository (or any parent)",
            false,
            false));

        var result = await new GitRepositoryService(executor).InspectAsync(_temp);

        Assert.Equal(GitRepositoryState.NotRepository, result.State);
        Assert.Null(result.Error);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public async Task RepositoryAboveWorkspaceIsReportedButNotInspected()
    {
        var workspace = Path.Combine(_temp, "child");
        Directory.CreateDirectory(workspace);
        var executor = new QueueGitExecutor(new GitCommandResult(
            0,
            _temp + Environment.NewLine,
            string.Empty,
            false,
            false));

        var result = await new GitRepositoryService(executor).InspectAsync(workspace);

        Assert.Equal(GitRepositoryState.RepositoryOutsideWorkspace, result.State);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task ParsesBranchChangesAndRenameFromPorcelainZ()
    {
        var executor = new QueueGitExecutor(
            Success(_temp + Environment.NewLine),
            Success("## main...origin/main\0 M changed.txt\0?? new.txt\0R  renamed.txt\0old.txt\0"));

        var result = await new GitRepositoryService(executor).InspectAsync(_temp);

        Assert.Equal(GitRepositoryState.Ready, result.State);
        Assert.Equal("main...origin/main", result.BranchSummary);
        Assert.Equal(3, result.Changes.Count);
        Assert.Contains(result.Changes, change => change.Path == "new.txt" && change.IsUntracked);
        var renamed = Assert.Single(result.Changes, change => change.IndexStatus == 'R');
        Assert.Equal("renamed.txt", renamed.Path);
        Assert.Equal("old.txt", renamed.SecondaryPath);
    }

    [Fact]
    public async Task DiffUsesSeparateArgumentsAndBoundsThePathToTheRepository()
    {
        var executor = new QueueGitExecutor(
            Success(_temp + Environment.NewLine),
            Success("diff --git a/src/file b/src/file\n+one\n"),
            Success("diff --git a/src/file b/src/file\n+two\n"));
        var service = new GitRepositoryService(executor);

        var result = await service.GetDiffAsync(
            _temp,
            GitDiffKind.Both,
            "src/file name;echo.txt");

        Assert.Equal(GitRepositoryState.Ready, result.State);
        Assert.Contains("# Unstaged changes", result.Diff, StringComparison.Ordinal);
        Assert.Contains("# Staged changes", result.Diff, StringComparison.Ordinal);
        Assert.Equal(3, executor.Calls.Count);
        Assert.All(executor.Calls.Skip(1), call =>
        {
            Assert.Contains("--no-ext-diff", call);
            Assert.Contains("--", call);
            Assert.Contains("src/file name;echo.txt", call);
        });
    }

    [Fact]
    public async Task RealGitRepositoryReportsAStagedChangeWhenGitIsAvailable()
    {
        var repo = Path.Combine(_temp, "real-repo");
        Directory.CreateDirectory(repo);
        var executor = new DirectGitCommandExecutor(timeout: TimeSpan.FromSeconds(10));
        var init = await executor.ExecuteAsync(repo, ["init"]);
        if (init.CouldNotStart)
        {
            return;
        }

        Assert.Equal(0, init.ExitCode);
        await File.WriteAllTextAsync(Path.Combine(repo, "tracked.txt"), "real content\n");
        var add = await executor.ExecuteAsync(repo, ["-C", repo, "add", "tracked.txt"]);
        Assert.Equal(0, add.ExitCode);

        var service = new GitRepositoryService(executor);
        var status = await service.InspectAsync(repo);
        var diff = await service.GetDiffAsync(repo, GitDiffKind.Staged);

        Assert.Equal(GitRepositoryState.Ready, status.State);
        Assert.Contains(status.Changes, change =>
            change.Path == "tracked.txt" && change.IndexStatus == 'A');
        Assert.Equal(GitRepositoryState.Ready, diff.State);
        Assert.Contains("real content", diff.Diff, StringComparison.Ordinal);
    }

    private static GitCommandResult Success(string output) => new(
        0,
        output,
        string.Empty,
        false,
        false);

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(
                         _temp,
                         "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch (IOException)
                {
                    // A concurrently closing git handle will be retried by delete.
                }
            }

            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private sealed class QueueGitExecutor(params GitCommandResult[] results) : IGitCommandExecutor
    {
        private readonly Queue<GitCommandResult> _results = new(results);

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GitCommandResult> ExecuteAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            _ = workingDirectory;
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }
}
