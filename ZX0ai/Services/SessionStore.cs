using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Sessions;

namespace ZX0ai.Services;

/// <summary>
/// The conversations on disk. One JSON file per session under
/// <c>%LOCALAPPDATA%\ZX0ai\sessions</c>.
/// </summary>
/// <remarks>
/// <para>
/// A file per session rather than one index file: a conversation is written after every
/// turn, and rewriting a single shared document each time turns one corrupt write into
/// the loss of every chat the user has. The rail's ordering comes from the records
/// themselves, so no index needs to be kept consistent with the files.
/// </para>
/// <para>
/// Every read is defensive. A session file is user-visible on disk and survives app
/// versions, so a malformed or truncated one is an expected condition, not a bug —
/// it is skipped and logged, never thrown out of a listing.
/// </para>
/// </remarks>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(string directory, ILogger<SessionStore> logger)
    {
        _directory = directory;
        _logger = logger;

        Directory.CreateDirectory(_directory);
    }

    /// <summary>Every stored conversation, most recently updated first.</summary>
    public IReadOnlyList<ChatSession> List()
    {
        var sessions = new List<ChatSession>();

        foreach (var path in EnumerateFiles())
        {
            if (Read(path) is { } session)
            {
                sessions.Add(session);
            }
        }

        sessions.Sort(static (left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        return sessions;
    }

    public ChatSession? Load(string id) => Read(PathFor(id));

    /// <summary>Writes a conversation, or deletes it if the user emptied it.</summary>
    public void Save(ChatSession session)
    {
        // An empty conversation is one the user opened and left. Persisting it would put
        // a permanent "New chat" row in the rail that nothing can ever clear.
        if (session.Messages.Count == 0)
        {
            Delete(session.Id);
            return;
        }

        session.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            File.WriteAllText(PathFor(session.Id), JsonSerializer.Serialize(session, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save session {Id}.", session.Id);
        }
    }

    public void Delete(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete session {Id}.", id);
        }
    }

    /// <summary>
    /// A title derived from the first thing the user actually said.
    /// </summary>
    /// <remarks>
    /// Deliberately not model-generated. Asking the provider to name the chat costs a
    /// round trip and a second failure mode, and the opening line is what the user will
    /// scan for anyway.
    /// </remarks>
    public static string TitleFrom(IEnumerable<ChatMessage> messages)
    {
        var first = messages.FirstOrDefault(message => message.Role == ChatRole.User)?.Content;
        if (string.IsNullOrWhiteSpace(first))
        {
            return "New chat";
        }

        var normalised = string.Join(' ', first.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

        return normalised.Length <= 52 ? normalised : normalised[..49] + "…";
    }

    /// <summary>
    /// How long ago, in the rail's compact form: <c>now</c>, <c>4m</c>, <c>3h</c>,
    /// <c>2d</c>, <c>5w</c>.
    /// </summary>
    public static string AgeOf(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}m";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}h";
        }

        return elapsed < TimeSpan.FromDays(7)
            ? $"{(int)elapsed.TotalDays}d"
            : $"{(int)(elapsed.TotalDays / 7)}w";
    }

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list sessions in {Directory}.", _directory);
            return [];
        }
    }

    private ChatSession? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Skipping unreadable session file {Path}.", path);
            return null;
        }
    }

    private string PathFor(string id) => Path.Combine(_directory, $"{id}.json");
}
