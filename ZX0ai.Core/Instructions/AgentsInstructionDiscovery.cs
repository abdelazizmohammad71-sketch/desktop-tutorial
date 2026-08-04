using System.Text;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Instructions;

/// <summary>Hard limits for project instruction discovery.</summary>
public sealed record AgentsInstructionDiscoveryOptions
{
    public IReadOnlyList<string> FallbackFileNames { get; init; } =
        ["AGENTS.md", "ZX0AI.md", ".agents.md"];

    public int MaxFileBytes { get; init; } = 32 * 1024;

    public int MaxTotalBytes { get; init; } = 128 * 1024;

    public int MaxFiles { get; init; } = 32;
}

/// <summary>One bounded instruction file, relative to the active project.</summary>
public sealed record ProjectInstructionFile(
    string RelativePath,
    string Content,
    int BytesRead,
    bool IsTruncated);

/// <summary>Instructions in application order: project root first, cwd last.</summary>
public sealed record ProjectInstructionSet(
    IReadOnlyList<ProjectInstructionFile> Files,
    IReadOnlyList<string> Diagnostics)
{
    public static ProjectInstructionSet Empty { get; } = new([], []);

    /// <summary>
    /// Produces an auditable prompt fragment without losing each file's scope.
    /// </summary>
    public string ToPromptText()
    {
        if (Files.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var file in Files)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append("## Project instructions: ")
                .AppendLine(file.RelativePath)
                .Append(file.Content);
        }

        return builder.ToString();
    }
}

public interface IAgentsInstructionDiscovery
{
    Task<ProjectInstructionSet> DiscoverAsync(
        WorkspaceContext workspace,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Discovers Codex-style instruction files without ever walking above the
/// session's canonical project root. At most one fallback name is selected in
/// each directory, and content is always byte bounded.
/// </summary>
public sealed class AgentsInstructionDiscovery : IAgentsInstructionDiscovery
{
    private readonly AgentsInstructionDiscoveryOptions _options;

    public AgentsInstructionDiscovery(AgentsInstructionDiscoveryOptions? options = null)
    {
        _options = options ?? new AgentsInstructionDiscoveryOptions();
        ValidateOptions(_options);
    }

    public async Task<ProjectInstructionSet> DiscoverAsync(
        WorkspaceContext workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (!workspace.HasProject || !workspace.IsAvailable ||
            string.IsNullOrWhiteSpace(workspace.RootPath))
        {
            return ProjectInstructionSet.Empty;
        }

        var root = WorkspacePathGuard.CanonicalizeDirectory(workspace.RootPath);
        var workingDirectory = string.IsNullOrWhiteSpace(workspace.WorkingDirectory)
            ? root
            : ResolveWorkingDirectory(root, workspace.WorkingDirectory);

        var directories = BuildRootFirstDirectoryChain(root, workingDirectory);
        var files = new List<ProjectInstructionFile>();
        var diagnostics = new List<string>();
        var remainingBytes = _options.MaxTotalBytes;

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count >= _options.MaxFiles || remainingBytes == 0)
            {
                diagnostics.Add("Project instruction discovery stopped at its configured limit.");
                break;
            }

            var selected = FindFirstSafeInstructionFile(root, directory);
            if (selected is null)
            {
                continue;
            }

            var readLimit = Math.Min(_options.MaxFileBytes, remainingBytes);
            try
            {
                var bounded = await ReadUtf8BoundedAsync(
                    selected,
                    readLimit,
                    cancellationToken).ConfigureAwait(false);

                var relative = NormalizeRelativePath(Path.GetRelativePath(root, selected));
                files.Add(new ProjectInstructionFile(
                    relative,
                    bounded.Content,
                    bounded.BytesRead,
                    bounded.IsTruncated));
                remainingBytes -= bounded.BytesRead;

                if (bounded.IsTruncated)
                {
                    diagnostics.Add($"{relative} was truncated at {readLimit} bytes.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(
                    $"Could not read {NormalizeRelativePath(Path.GetRelativePath(root, selected))}.");
            }
        }

        return new ProjectInstructionSet(files, diagnostics);
    }

    private string? FindFirstSafeInstructionFile(string root, string directory)
    {
        foreach (var fileName in _options.FallbackFileNames)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, candidate);
            if (WorkspacePathGuard.TryResolveRelative(root, relative, out var safe, out _) &&
                File.Exists(safe))
            {
                return safe;
            }
        }

        return null;
    }

    private static string ResolveWorkingDirectory(string root, string workingDirectory)
    {
        var candidate = Path.IsPathRooted(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.GetFullPath(Path.Combine(root, workingDirectory));

        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException(
                $"Working directory does not exist: {candidate}");
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (!WorkspacePathGuard.TryResolveRelative(root, relative, out var resolved, out var error) ||
            !Directory.Exists(resolved))
        {
            throw new InvalidOperationException(
                $"Working directory must stay inside the active project. {error}");
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static IReadOnlyList<string> BuildRootFirstDirectoryChain(
        string root,
        string workingDirectory)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var chain = new List<string>();
        var current = Path.TrimEndingDirectorySeparator(workingDirectory);

        while (true)
        {
            chain.Add(current);
            if (comparer.Equals(current, root))
            {
                break;
            }

            current = Directory.GetParent(current)?.FullName ??
                throw new InvalidOperationException(
                    "Working directory is not descended from the active project root.");

            if (!WorkspacePathGuard.IsInside(root, current) && !comparer.Equals(current, root))
            {
                throw new InvalidOperationException(
                    "Instruction discovery attempted to cross the active project root.");
            }
        }

        chain.Reverse();
        return chain;
    }

    private static async Task<BoundedText> ReadUtf8BoundedAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[maxBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var truncated = offset > maxBytes || stream.Position < stream.Length;
        var bytesRead = Math.Min(offset, maxBytes);
        var content = Encoding.UTF8.GetString(buffer, 0, bytesRead)
            .TrimStart('\uFEFF');
        return new BoundedText(content, bytesRead, truncated);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static void ValidateOptions(AgentsInstructionDiscoveryOptions options)
    {
        if (options.MaxFileBytes is < 1 or > 1024 * 1024 ||
            options.MaxTotalBytes is < 1 or > 4 * 1024 * 1024 ||
            options.MaxFiles is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Instruction discovery limits are outside the supported range.");
        }

        if (options.FallbackFileNames.Count == 0 ||
            options.FallbackFileNames.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                Path.IsPathRooted(name) ||
                name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
        {
            throw new ArgumentException(
                "Fallback instruction names must be plain file names.",
                nameof(options));
        }
    }

    private sealed record BoundedText(string Content, int BytesRead, bool IsTruncated);
}
