using System.Text;

namespace ZX0ai.Core.Governance;

/// <summary>The leader's memory files for one project.</summary>
public interface IProjectMemory
{
    /// <summary>Absolute path of one memory file for a project.</summary>
    string PathFor(string? projectRoot, MemoryFile file);

    /// <summary>Reads a memory file, or an empty string when it does not exist.</summary>
    Task<string> ReadAsync(
        string? projectRoot,
        MemoryFile file,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces a memory file. Used for the files that describe the present.</summary>
    Task WriteAsync(
        string? projectRoot,
        MemoryFile file,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>Appends a dated entry. Used for the files that are an audit trail.</summary>
    Task AppendAsync(
        string? projectRoot,
        MemoryFile file,
        string entry,
        CancellationToken cancellationToken = default);
}

/// <summary>Which memory file.</summary>
/// <remarks>
/// Two kinds, and the difference matters. <see cref="Planning"/> and
/// <see cref="Progress"/> describe the present and are rewritten. <see cref="Reports"/>
/// and <see cref="Governance"/> are the audit trail and are only ever appended to —
/// a log you can rewrite is not a log.
/// </remarks>
public enum MemoryFile
{
    /// <summary>The current plan. Rewritten per execution request.</summary>
    Planning,

    /// <summary>The live task board. Rewritten as tasks move.</summary>
    Progress,

    /// <summary>Work log and QC history. Append-only.</summary>
    Reports,

    /// <summary>Approvals, escalations and audit trail. Append-only.</summary>
    Governance,
}

/// <summary>
/// Markdown memory files kept beside <c>brain.md</c> in the project.
/// </summary>
/// <remarks>
/// <para>
/// Plain markdown inside the project, not a database and not app-private state. A user
/// must be able to read what the system decided, disagree with it, edit it, and commit
/// it alongside their code. An audit trail nobody can open is not an audit trail.
/// </para>
/// <para>
/// Every write is best-effort. Memory is a record of work, not the work itself, and a
/// locked file must never take a turn down with it.
/// </para>
/// </remarks>
public sealed class ProjectMemory(string appDataDirectory) : IProjectMemory
{
    private const string ProjectFolder = ".zx0ai";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string PathFor(string? projectRoot, MemoryFile file)
    {
        var name = FileName(file);

        return string.IsNullOrWhiteSpace(projectRoot)
            ? Path.Combine(appDataDirectory, name)
            : Path.Combine(projectRoot, ProjectFolder, name);
    }

    public async Task<string> ReadAsync(
        string? projectRoot,
        MemoryFile file,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(projectRoot, file);

        try
        {
            return File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    public Task WriteAsync(
        string? projectRoot,
        MemoryFile file,
        string content,
        CancellationToken cancellationToken = default) =>
        SaveAsync(projectRoot, file, content, append: false, cancellationToken);

    public Task AppendAsync(
        string? projectRoot,
        MemoryFile file,
        string entry,
        CancellationToken cancellationToken = default) =>
        SaveAsync(projectRoot, file, entry, append: true, cancellationToken);

    private async Task SaveAsync(
        string? projectRoot,
        MemoryFile file,
        string content,
        bool append,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        // Appending to a file that is meant to be replaced, or replacing one that is
        // meant to be an audit trail, would both be silent corruption. The enum decides,
        // not the caller.
        var mustAppend = file is MemoryFile.Reports or MemoryFile.Governance;
        if (append != mustAppend)
        {
            throw new InvalidOperationException(
                $"{file} is {(mustAppend ? "append-only" : "rewritten")}; use the other method.");
        }

        var path = PathFor(projectRoot, file);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var builder = new StringBuilder();

            if (append)
            {
                var existing = File.Exists(path)
                    ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                    : string.Empty;

                builder.Append(existing.Length == 0 ? Header(file) : existing.TrimEnd());
                builder.Append("\n\n### ")
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"))
                    .Append('\n');
            }
            else
            {
                builder.Append(Header(file));
            }

            builder.Append('\n').Append(content.Trim()).Append('\n');

            await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort by design; see the type remarks.
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string FileName(MemoryFile file) => file switch
    {
        MemoryFile.Planning => "planning.md",
        MemoryFile.Progress => "progress.md",
        MemoryFile.Reports => "reports.md",
        MemoryFile.Governance => "governance.md",
        _ => throw new ArgumentOutOfRangeException(nameof(file)),
    };

    private static string Header(MemoryFile file) => file switch
    {
        MemoryFile.Planning =>
            "# planning.md — current plan\n\nRewritten at the start of every execution request.\n",
        MemoryFile.Progress =>
            "# progress.md — task board\n\nWhere the work is right now.\n",
        MemoryFile.Reports =>
            "# reports.md — work log\n\nAppend-only. Entries are never rewritten.\n",
        MemoryFile.Governance =>
            "# governance.md — approvals and escalations\n\nAppend-only audit trail.\n",
        _ => string.Empty,
    };
}
