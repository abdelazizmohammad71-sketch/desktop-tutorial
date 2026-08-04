using ZX0ai.Core.Security;

namespace ZX0ai.Core.Workspaces;

/// <summary>
/// Immutable workspace binding carried by a chat session. A writable session can
/// never exist without a canonical project root.
/// </summary>
public sealed record WorkspaceContext
{
    public required string SessionId { get; init; }

    public string? ProjectId { get; init; }

    public string? RootPath { get; init; }

    public string? WorkingDirectory { get; init; }

    public required ExecutionPolicy Policy { get; init; }

    public bool HasProject => !string.IsNullOrWhiteSpace(ProjectId) &&
                              !string.IsNullOrWhiteSpace(RootPath);

    public bool IsAvailable => HasProject && Directory.Exists(RootPath);

    public static WorkspaceContext ForProject(
        string sessionId,
        string projectId,
        string rootPath,
        ExecutionPolicy? policy = null)
    {
        var canonical = WorkspacePathGuard.CanonicalizeDirectory(rootPath);
        var effectivePolicy = policy ?? ExecutionPolicy.WorkspaceDefault;

        return new WorkspaceContext
        {
            SessionId = sessionId,
            ProjectId = projectId,
            RootPath = canonical,
            WorkingDirectory = canonical,
            Policy = effectivePolicy,
        };
    }

    public static WorkspaceContext WithoutProject(string sessionId) => new()
    {
        SessionId = sessionId,
        Policy = ExecutionPolicy.ReadOnly,
    };
}

/// <summary>Canonical path boundary shared by every file-capable feature.</summary>
public static class WorkspacePathGuard
{
    public static string CanonicalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A project folder is required.", nameof(path));
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Project folder does not exist: {full}");
        }

        var directory = new DirectoryInfo(full);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 &&
            directory.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
        }

        return full;
    }

    public static bool TryResolveRelative(
        string root,
        string? relativePath,
        out string resolved,
        out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "Provide a file path.";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = "Use a path relative to the active project.";
            return false;
        }

        try
        {
            var canonicalRoot = CanonicalizeDirectory(root);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));

            if (!IsInside(canonicalRoot, candidate))
            {
                error = "Path escapes the active project.";
                return false;
            }

            if (!ExistingComponentsStayInside(canonicalRoot, candidate))
            {
                error = "Path crosses a link or junction outside the active project.";
                return false;
            }

            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or
                                         NotSupportedException or
                                         PathTooLongException or
                                         IOException or
                                         UnauthorizedAccessException)
        {
            error = "That path is not valid inside the active project.";
            return false;
        }
    }

    public static bool IsInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private static bool ExistingComponentsStayInside(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;

        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current)
                    ? new FileInfo(current)
                    : null;

            if (info is null)
            {
                // The rest may be new. Its nearest existing ancestor was checked.
                break;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !IsInside(root, Path.GetFullPath(target.FullName)))
            {
                return false;
            }
        }

        return true;
    }
}
