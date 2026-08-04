using System.Text;
using System.Text.Json;
using ZX0ai.Core.Skills;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Services;

/// <summary>
/// Lists the files and folders under a path inside the workspace.
/// </summary>
/// <remarks>
/// <para>
/// The built-in skill set can read, write and run, but has no way to discover what is
/// there. Without this the model has to guess filenames before it can read them, which
/// in practice means it invents a plausible project layout and then reports that every
/// file is missing.
/// </para>
/// <para>
/// Noise directories are skipped by name. <c>node_modules</c> or <c>bin</c> can be tens
/// of thousands of entries, and spending the context window on them buys nothing — they
/// are build output, not the project.
/// </para>
/// </remarks>
public sealed class ListFilesSkill : ISkill
{
    /// <summary>Enough to understand a project; small enough not to flood the window.</summary>
    private const int MaxEntries = 400;

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", ".idea", "dist", "build",
        "__pycache__", ".venv", "venv", "target", "packages", ".next",
    };

    public string Name => "list_files";

    public string Description =>
        "List files and directories inside the project. Use an empty path for the root. " +
        "Recurses by default and skips build output such as node_modules, bin and obj.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("path", "string", "Directory relative to the project root. Empty means the root.", false),
        ("recursive", "boolean", "Walk subdirectories. Defaults to true.", false));

    public Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var root = context.Workspace.RootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult(SkillResult.Fail("No project folder is bound."));
        }

        var requested = arguments.GetString("path");
        var start = root;

        if (!string.IsNullOrWhiteSpace(requested) && requested is not "." and not "/" and not "\\")
        {
            // The same guard the read and write skills use: a model-authored path is
            // untrusted input, and "../.." is a perfectly ordinary thing for one to emit.
            if (!WorkspacePathGuard.TryResolveRelative(root, requested, out var resolved, out var error))
            {
                return Task.FromResult(SkillResult.Fail(error));
            }

            start = resolved;
        }

        if (!Directory.Exists(start))
        {
            return Task.FromResult(SkillResult.Fail($"No such directory: {requested}"));
        }

        var recursive = !arguments.TryGetProperty("recursive", out var flag) ||
                        flag.ValueKind != JsonValueKind.False;

        var builder = new StringBuilder();
        var count = 0;
        var truncated = Walk(start, root, recursive, builder, ref count);

        if (count == 0)
        {
            return Task.FromResult(SkillResult.Ok("(empty)", "Listed 0 entries"));
        }

        if (truncated)
        {
            builder.AppendLine($"... truncated at {MaxEntries} entries");
        }

        return Task.FromResult(SkillResult.Ok(
            builder.ToString(),
            $"Listed {count} entr{(count == 1 ? "y" : "ies")}"));
    }

    /// <summary>Depth-first walk, directories before files, paths relative to the root.</summary>
    private static bool Walk(
        string directory,
        string root,
        bool recursive,
        StringBuilder builder,
        ref int count)
    {
        IEnumerable<string> directories;
        IEnumerable<string> files;

        try
        {
            directories = Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase);
            files = Directory.EnumerateFiles(directory).Order(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable subdirectory is not a reason to fail the whole listing.
            return false;
        }

        foreach (var file in files)
        {
            if (count >= MaxEntries)
            {
                return true;
            }

            builder.AppendLine(Path.GetRelativePath(root, file).Replace('\\', '/'));
            count++;
        }

        foreach (var child in directories)
        {
            if (Skip.Contains(Path.GetFileName(child)))
            {
                continue;
            }

            if (count >= MaxEntries)
            {
                return true;
            }

            builder.AppendLine(Path.GetRelativePath(root, child).Replace('\\', '/') + "/");
            count++;

            if (recursive && Walk(child, root, recursive: true, builder, ref count))
            {
                return true;
            }
        }

        return false;
    }
}
