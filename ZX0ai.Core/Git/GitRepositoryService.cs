using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Git;

/// <summary>Reduces a porcelain branch summary to the name a breadcrumb would show.</summary>
/// <remarks>
/// <c>git status --branch</c> reports a line built for a status report, not a label:
/// tracking info, ahead/behind counts and a couple of special-cased sentences for states
/// that have no branch yet. A breadcrumb needs one short word, so this exists to isolate
/// the parsing from anything that renders it.
/// </remarks>
public static class GitBranchName
{
    private const string NoCommitsPrefix = "No commits yet on ";
    private const string DetachedPrefix = "HEAD (no branch)";

    /// <summary>The current branch name, or null when there is none to show.</summary>
    public static string? Parse(string? branchSummary)
    {
        if (string.IsNullOrWhiteSpace(branchSummary))
        {
            return null;
        }

        var text = branchSummary.Trim();

        if (text.StartsWith(NoCommitsPrefix, StringComparison.Ordinal))
        {
            text = text[NoCommitsPrefix.Length..];
        }
        else if (text.StartsWith(DetachedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Tracking info reads "main...origin/main", optionally followed by
        // " [ahead 1, behind 2]". Only the local name in front of "..." is a branch.
        var trackingIndex = text.IndexOf("...", StringComparison.Ordinal);
        if (trackingIndex >= 0)
        {
            text = text[..trackingIndex];
        }

        var bracketIndex = text.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex >= 0)
        {
            text = text[..bracketIndex];
        }

        text = text.Trim();
        return text.Length == 0 ? null : text;
    }
}

public enum GitRepositoryState
{
    Ready,
    NotRepository,
    RepositoryOutsideWorkspace,
    GitUnavailable,
    Error,
}

public enum GitDiffKind
{
    Unstaged,
    Staged,
    Both,
}

public sealed record GitFileChange(
    string Path,
    string? SecondaryPath,
    char IndexStatus,
    char WorkTreeStatus)
{
    public bool IsUntracked => IndexStatus == '?' && WorkTreeStatus == '?';

    public bool IsConflicted => IndexStatus == 'U' || WorkTreeStatus == 'U' ||
                                IndexStatus == 'A' && WorkTreeStatus == 'A' ||
                                IndexStatus == 'D' && WorkTreeStatus == 'D';
}

public sealed record GitRepositorySnapshot(
    GitRepositoryState State,
    string? RepositoryRoot,
    string? BranchSummary,
    IReadOnlyList<GitFileChange> Changes,
    bool IsTruncated,
    string? Error)
{
    public static GitRepositorySnapshot NotRepository() => new(
        GitRepositoryState.NotRepository,
        null,
        null,
        [],
        false,
        null);
}

public sealed record GitDiffResult(
    GitRepositoryState State,
    string Diff,
    bool IsTruncated,
    string? Error);

public interface IGitRepositoryService
{
    Task<GitRepositorySnapshot> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    Task<GitDiffResult> GetDiffAsync(
        string workspaceRoot,
        GitDiffKind kind = GitDiffKind.Both,
        string? relativePath = null,
        CancellationToken cancellationToken = default);
}

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool IsTruncated,
    bool TimedOut,
    bool CouldNotStart = false);

public interface IGitCommandExecutor
{
    Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes git directly with ProcessStartInfo.ArgumentList. It never invokes a
/// shell and bounds output, duration, and process lifetime.
/// </summary>
public sealed class DirectGitCommandExecutor : IGitCommandExecutor
{
    private readonly string _gitExecutable;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputCharacters;

    public DirectGitCommandExecutor(
        string gitExecutable = "git",
        TimeSpan? timeout = null,
        int maxOutputCharacters = 4 * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(gitExecutable))
        {
            throw new ArgumentException("Git executable is required.", nameof(gitExecutable));
        }

        if (maxOutputCharacters is < 4096 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputCharacters));
        }

        _gitExecutable = gitExecutable;
        _timeout = timeout ?? TimeSpan.FromSeconds(20);
        _maxOutputCharacters = maxOutputCharacters;
    }

    public async Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new GitCommandResult(-1, string.Empty, "Git could not start.", false, false, true);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message, false, false, true);
        }

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput,
            _maxOutputCharacters,
            cancellationToken);
        var stderrTask = ReadBoundedAsync(
            process.StandardError,
            Math.Min(_maxOutputCharacters, 256 * 1024),
            cancellationToken);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new GitCommandResult(
            timedOut ? -1 : process.ExitCode,
            stdout.Text,
            stderr.Text,
            stdout.IsTruncated || stderr.IsTruncated,
            timedOut);
    }

    private static async Task<BoundedOutput> ReadBoundedAsync(
        StreamReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(maxCharacters, 64 * 1024));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maxCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                truncated = true;
            }
        }

        return new BoundedOutput(result.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process may have exited between the check and Kill.
        }
    }

    private sealed record BoundedOutput(string Text, bool IsTruncated);
}

/// <summary>Read-only git status and diff surface bound to one workspace root.</summary>
public sealed class GitRepositoryService : IGitRepositoryService
{
    private const int MaxCombinedDiffCharacters = 4 * 1024 * 1024;
    private readonly IGitCommandExecutor _executor;

    public GitRepositoryService(IGitCommandExecutor? executor = null) =>
        _executor = executor ?? new DirectGitCommandExecutor();

    public async Task<GitRepositorySnapshot> InspectAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = WorkspacePathGuard.CanonicalizeDirectory(workspaceRoot);
        var repository = await ResolveRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
        if (repository.State != GitRepositoryState.Ready || repository.RepositoryRoot is null)
        {
            return new GitRepositorySnapshot(
                repository.State,
                repository.RepositoryRoot,
                null,
                [],
                false,
                repository.Error);
        }

        var status = await _executor.ExecuteAsync(
            repository.RepositoryRoot,
            GitArguments(
                repository.RepositoryRoot,
                "status",
                "--porcelain=v1",
                "-z",
                "--untracked-files=all",
                "--branch"),
            cancellationToken).ConfigureAwait(false);
        if (status.CouldNotStart)
        {
            return new GitRepositorySnapshot(
                GitRepositoryState.GitUnavailable,
                repository.RepositoryRoot,
                null,
                [],
                false,
                "Git executable is unavailable.");
        }

        if (status.TimedOut || status.ExitCode != 0)
        {
            return new GitRepositorySnapshot(
                GitRepositoryState.Error,
                repository.RepositoryRoot,
                null,
                [],
                status.IsTruncated,
                status.TimedOut ? "Git status timed out." : SanitizeError(status.StandardError));
        }

        var parsed = ParsePorcelain(status.StandardOutput, root, repository.RepositoryRoot);
        return new GitRepositorySnapshot(
            GitRepositoryState.Ready,
            repository.RepositoryRoot,
            parsed.BranchSummary,
            parsed.Changes,
            status.IsTruncated,
            null);
    }

    public async Task<GitDiffResult> GetDiffAsync(
        string workspaceRoot,
        GitDiffKind kind = GitDiffKind.Both,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        var root = WorkspacePathGuard.CanonicalizeDirectory(workspaceRoot);
        var repository = await ResolveRepositoryAsync(root, cancellationToken).ConfigureAwait(false);
        if (repository.State != GitRepositoryState.Ready || repository.RepositoryRoot is null)
        {
            return new GitDiffResult(repository.State, string.Empty, false, repository.Error);
        }

        var pathspec = ResolvePathspec(root, repository.RepositoryRoot, relativePath);
        if (relativePath is not null && pathspec is null)
        {
            return new GitDiffResult(
                GitRepositoryState.Error,
                string.Empty,
                false,
                "Diff path must stay inside the active repository and workspace.");
        }

        var sections = kind switch
        {
            GitDiffKind.Unstaged => new[] { (Title: string.Empty, Cached: false) },
            GitDiffKind.Staged => new[] { (Title: string.Empty, Cached: true) },
            GitDiffKind.Both => new[]
            {
                (Title: "# Unstaged changes", Cached: false),
                (Title: "# Staged changes", Cached: true),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var builder = new StringBuilder();
        var truncated = false;
        foreach (var section in sections)
        {
            var arguments = new List<string>(GitArguments(
                repository.RepositoryRoot,
                "diff",
                "--no-ext-diff",
                "--no-textconv",
                "--no-color"));
            if (section.Cached)
            {
                arguments.Add("--cached");
            }

            arguments.Add("--");
            if (pathspec is not null)
            {
                arguments.Add(pathspec);
            }

            var result = await _executor.ExecuteAsync(
                repository.RepositoryRoot,
                arguments,
                cancellationToken).ConfigureAwait(false);
            if (result.CouldNotStart)
            {
                return new GitDiffResult(
                    GitRepositoryState.GitUnavailable,
                    string.Empty,
                    false,
                    "Git executable is unavailable.");
            }

            if (result.TimedOut || result.ExitCode != 0)
            {
                return new GitDiffResult(
                    GitRepositoryState.Error,
                    builder.ToString(),
                    truncated || result.IsTruncated,
                    result.TimedOut ? "Git diff timed out." : SanitizeError(result.StandardError));
            }

            if (section.Title.Length > 0)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine().AppendLine();
                }

                builder.AppendLine(section.Title);
            }

            AppendBounded(builder, result.StandardOutput, ref truncated);
            truncated |= result.IsTruncated;
        }

        return new GitDiffResult(
            GitRepositoryState.Ready,
            builder.ToString(),
            truncated,
            null);
    }

    private async Task<RepositoryResolution> ResolveRepositoryAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var result = await _executor.ExecuteAsync(
            workspaceRoot,
            GitArguments(workspaceRoot, "rev-parse", "--show-toplevel"),
            cancellationToken).ConfigureAwait(false);
        if (result.CouldNotStart)
        {
            return new RepositoryResolution(
                GitRepositoryState.GitUnavailable,
                null,
                "Git executable is unavailable.");
        }

        if (result.TimedOut)
        {
            return new RepositoryResolution(
                GitRepositoryState.Error,
                null,
                "Git repository detection timed out.");
        }

        if (result.ExitCode != 0)
        {
            return LooksLikeNotRepository(result.StandardError)
                ? new RepositoryResolution(GitRepositoryState.NotRepository, null, null)
                : new RepositoryResolution(
                    GitRepositoryState.Error,
                    null,
                    SanitizeError(result.StandardError));
        }

        var firstLine = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return new RepositoryResolution(
                GitRepositoryState.Error,
                null,
                "Git did not return a repository root.");
        }

        string repositoryRoot;
        try
        {
            repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(firstLine.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RepositoryResolution(
                GitRepositoryState.Error,
                null,
                "Git returned an invalid repository root.");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!comparer.Equals(repositoryRoot, workspaceRoot) &&
            !WorkspacePathGuard.IsInside(workspaceRoot, repositoryRoot))
        {
            return new RepositoryResolution(
                GitRepositoryState.RepositoryOutsideWorkspace,
                repositoryRoot,
                "The git repository root is outside the active workspace.");
        }

        return new RepositoryResolution(GitRepositoryState.Ready, repositoryRoot, null);
    }

    private static IReadOnlyList<string> GitArguments(
        string repositoryRoot,
        params string[] commandArguments)
    {
        var arguments = new List<string>
        {
            "-c", "color.ui=false",
            "-c", "core.quotepath=false",
            "-c", "core.fsmonitor=false",
            "--literal-pathspecs",
            "-C", repositoryRoot,
        };
        arguments.AddRange(commandArguments);
        return arguments;
    }

    private static PorcelainResult ParsePorcelain(
        string output,
        string workspaceRoot,
        string repositoryRoot)
    {
        var entries = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var changes = new List<GitFileChange>();
        string? branch = null;

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.StartsWith("## ", StringComparison.Ordinal))
            {
                branch = entry[3..].Trim();
                continue;
            }

            if (entry.Length < 4 || entry[2] != ' ')
            {
                continue;
            }

            var indexStatus = entry[0];
            var workTreeStatus = entry[1];
            var path = entry[3..];
            string? secondary = null;
            if ((indexStatus is 'R' or 'C' || workTreeStatus is 'R' or 'C') &&
                index + 1 < entries.Length)
            {
                secondary = entries[++index];
            }

            if (!IsSafeGitPath(workspaceRoot, repositoryRoot, path) ||
                secondary is not null && !IsSafeGitPath(workspaceRoot, repositoryRoot, secondary))
            {
                continue;
            }

            changes.Add(new GitFileChange(
                NormalizePath(path),
                secondary is null ? null : NormalizePath(secondary),
                indexStatus,
                workTreeStatus));
        }

        return new PorcelainResult(branch, changes);
    }

    private static bool IsSafeGitPath(
        string workspaceRoot,
        string repositoryRoot,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(Path.Combine(repositoryRoot, path));
            return WorkspacePathGuard.IsInside(workspaceRoot, candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? ResolvePathspec(
        string workspaceRoot,
        string repositoryRoot,
        string? relativePath)
    {
        if (relativePath is null)
        {
            return null;
        }

        if (!WorkspacePathGuard.TryResolveRelative(
                workspaceRoot,
                relativePath,
                out var resolved,
                out _))
        {
            return null;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!comparer.Equals(resolved, repositoryRoot) &&
            !WorkspacePathGuard.IsInside(repositoryRoot, resolved))
        {
            return null;
        }

        return NormalizePath(Path.GetRelativePath(repositoryRoot, resolved));
    }

    private static void AppendBounded(
        StringBuilder builder,
        string value,
        ref bool truncated)
    {
        var remaining = MaxCombinedDiffCharacters - builder.Length;
        if (remaining <= 0)
        {
            truncated = true;
            return;
        }

        if (value.Length <= remaining)
        {
            builder.Append(value);
            return;
        }

        builder.Append(value, 0, remaining);
        truncated = true;
    }

    private static bool LooksLikeNotRepository(string error) =>
        error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("not a repository", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeError(string error)
    {
        var firstLine = error
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine)
            ? "Git command failed."
            : firstLine.Trim();
    }

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record RepositoryResolution(
        GitRepositoryState State,
        string? RepositoryRoot,
        string? Error);

    private sealed record PorcelainResult(
        string? BranchSummary,
        IReadOnlyList<GitFileChange> Changes);
}
