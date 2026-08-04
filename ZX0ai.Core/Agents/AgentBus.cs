using ZX0ai.Core.Models;

namespace ZX0ai.Core.Agents;

/// <summary>One message on the shared team bus.</summary>
/// <param name="AuthorId">Agent that wrote it.</param>
/// <param name="AuthorName">Display name of the author.</param>
/// <param name="Role">Author's role.</param>
/// <param name="Content">Body.</param>
/// <param name="AddresseeId">Optional target agent; null means broadcast.</param>
/// <param name="Timestamp">When it was posted.</param>
public sealed record BusMessage(
    string AuthorId,
    string AuthorName,
    AgentRole Role,
    string Content,
    string? AddresseeId = null,
    DateTimeOffset Timestamp = default)
{
    public DateTimeOffset Timestamp { get; init; } =
        Timestamp == default ? DateTimeOffset.Now : Timestamp;
}

/// <summary>
/// Shared, ordered message stream the team reads and writes.
/// </summary>
/// <remarks>
/// The bus is what makes this a team rather than a fan-out: members see each other's
/// contributions and can critique them. Reads are filtered by addressee so a member
/// only sees broadcasts plus messages aimed at it.
/// </remarks>
public sealed class AgentBus
{
    private readonly List<BusMessage> _messages = [];

    // System.Threading.Lock is .NET 9; this target is .NET 8.
    private readonly object _gate = new();

    /// <summary>Everything posted so far, oldest first.</summary>
    public IReadOnlyList<BusMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    /// <summary>Raised after a message is posted.</summary>
    public event EventHandler<BusMessage>? MessagePosted;

    public void Post(BusMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
        }

        MessagePosted?.Invoke(this, message);
    }

    /// <summary>Broadcasts and messages addressed to <paramref name="agentId"/>.</summary>
    public IReadOnlyList<BusMessage> ReadFor(string agentId)
    {
        lock (_gate)
        {
            return [.. _messages.Where(m => m.AddresseeId is null || m.AddresseeId == agentId)];
        }
    }

    /// <summary>Renders the visible bus as a transcript for a model prompt.</summary>
    public string Transcribe(string agentId, int maxMessages = 40)
    {
        var visible = ReadFor(agentId);

        // IReadOnlyList has no range indexer; skip from the front instead.
        var slice = visible.Count > maxMessages
            ? visible.Skip(visible.Count - maxMessages).ToList()
            : visible;

        return string.Join(
            "\n\n",
            slice.Select(m => $"[{m.AuthorName} · {m.Role}]\n{m.Content}"));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
        }
    }
}
