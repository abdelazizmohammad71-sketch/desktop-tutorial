using System.Text;

namespace ZX0ai.Core.Agents;

/// <summary>The leader's durable memory for a project.</summary>
public interface IBrainFile
{
    /// <summary>Resolves where the brain lives for a given project, or app data.</summary>
    string PathFor(string? projectRoot);

    /// <summary>Reads the brain, or an empty string when there is nothing yet.</summary>
    Task<string> ReadAsync(string? projectRoot, CancellationToken cancellationToken = default);

    /// <summary>Appends one dated note, trimming the file if it has grown too large.</summary>
    Task AppendAsync(string? projectRoot, string note, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>brain.md</c>: what the leader learned that is worth carrying into the next turn.
/// </summary>
/// <remarks>
/// <para>
/// Conversation history is not memory — it ends with the session, and it is trimmed to
/// the last few turns before it ever reaches a model. The brain is the leader's own
/// running notes: decisions taken, conventions discovered, threads left open. It is
/// plain markdown in the project so a person can read it, edit it, or delete it.
/// </para>
/// <para>
/// It is capped and trimmed from the front. An unbounded memory file eventually costs
/// more context than it is worth, and the oldest notes are the ones most likely to have
/// gone stale.
/// </para>
/// </remarks>
public sealed class BrainFile(string appDataDirectory) : IBrainFile
{
    /// <summary>Notes beyond this are dropped, oldest first.</summary>
    public const int MaxCharacters = 16_384;

    private const string FileName = "brain.md";
    private const string ProjectFolder = ".zx0ai";
    private const string Header = "# DXM brain";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string PathFor(string? projectRoot) =>
        string.IsNullOrWhiteSpace(projectRoot)
            // A read-only session has no project to write into, but the leader should
            // still remember across sessions, so it falls back to app data.
            ? Path.Combine(appDataDirectory, FileName)
            : Path.Combine(projectRoot, ProjectFolder, FileName);

    public async Task<string> ReadAsync(
        string? projectRoot,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(projectRoot);

        try
        {
            return File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Memory is an optimisation. A locked or unreadable file must not fail a turn.
            return string.Empty;
        }
    }

    public async Task AppendAsync(
        string? projectRoot,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var path = PathFor(projectRoot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var existing = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;

            var builder = new StringBuilder();
            if (existing.Length == 0)
            {
                builder.Append(Header).Append("\n\n");
                builder.Append(
                    "Notes DXM keeps for this project. Safe to edit or delete.\n\n");
            }
            else
            {
                builder.Append(existing.TrimEnd()).Append("\n\n");
            }

            builder
                .Append("## ")
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"))
                .Append("\n\n")
                .Append(note.Trim())
                .Append('\n');

            await File
                .WriteAllTextAsync(path, Trim(builder.ToString()), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same reasoning as ReadAsync: never fail a turn over a note.
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the oldest sections until the file fits, keeping the header.</summary>
    private static string Trim(string content)
    {
        if (content.Length <= MaxCharacters)
        {
            return content;
        }

        var sections = content.Split("\n## ", StringSplitOptions.None);
        if (sections.Length <= 1)
        {
            return content[^MaxCharacters..];
        }

        var head = sections[0];
        var kept = new List<string>();
        var budget = MaxCharacters - head.Length;

        // Walk newest to oldest so the notes that survive are the current ones.
        for (var i = sections.Length - 1; i >= 1; i--)
        {
            var cost = sections[i].Length + 4;
            if (cost > budget)
            {
                break;
            }

            budget -= cost;
            kept.Insert(0, sections[i]);
        }

        return kept.Count == 0 ? head : head + "\n## " + string.Join("\n## ", kept);
    }
}
