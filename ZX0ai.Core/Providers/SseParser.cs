namespace ZX0ai.Core.Providers;

/// <summary>
/// Line-at-a-time parser for the Server-Sent Events wire format.
/// </summary>
/// <remarks>
/// Kept as a pure state machine rather than folded into the HTTP client so the
/// protocol can be tested without a socket. Handles the parts of the spec that
/// providers actually emit: <c>data:</c> payloads, multi-line data joined with
/// newlines, <c>:</c> comment/keep-alive lines, and a blank line as the event
/// terminator.
/// </remarks>
public sealed class SseParser
{
    /// <summary>Sentinel OpenAI-compatible providers send to close a stream.</summary>
    public const string DoneSentinel = "[DONE]";

    private readonly List<string> _dataLines = [];

    /// <summary>
    /// Feeds one line. Returns true when that line completed an event, in which case
    /// <paramref name="payload"/> holds the joined <c>data:</c> content.
    /// </summary>
    public bool TryFeedLine(string? line, out string payload)
    {
        payload = string.Empty;

        // End of stream mid-event: flush whatever was buffered.
        if (line is null)
        {
            return TryFlush(out payload);
        }

        // A blank line terminates the current event.
        if (line.Length == 0)
        {
            return TryFlush(out payload);
        }

        // Comments and keep-alives.
        if (line[0] == ':')
        {
            return false;
        }

        var separator = line.IndexOf(':');
        var field = separator < 0 ? line : line[..separator];

        if (!field.Equals("data", StringComparison.Ordinal))
        {
            // event:, id: and retry: carry no payload for chat completions.
            return false;
        }

        var value = separator < 0 ? string.Empty : line[(separator + 1)..];

        // Exactly one leading space is part of the framing, not the data.
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        _dataLines.Add(value);
        return false;
    }

    /// <summary>True when the payload is the provider's end-of-stream sentinel.</summary>
    public static bool IsDone(string payload) =>
        payload.AsSpan().Trim().SequenceEqual(DoneSentinel);

    private bool TryFlush(out string payload)
    {
        if (_dataLines.Count == 0)
        {
            payload = string.Empty;
            return false;
        }

        payload = string.Join('\n', _dataLines);
        _dataLines.Clear();
        return true;
    }
}
