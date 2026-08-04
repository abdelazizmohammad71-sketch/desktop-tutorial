using System.Text;
using System.Text.Json;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

/// <summary>
/// Reassembles tool calls that arrive split across many SSE chunks.
/// </summary>
/// <remarks>
/// OpenAI-compatible streams send a tool call as fragments keyed by array index: the
/// first fragment usually carries id and name, and the arguments JSON dribbles in one
/// piece at a time. Nothing can be executed until the stream ends, so fragments are
/// buffered per index and emitted together.
/// </remarks>
public sealed class ToolCallAccumulator
{
    private sealed class Fragment
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }

    private readonly SortedDictionary<int, Fragment> _fragments = [];

    /// <summary>Consumes the <c>tool_calls</c> array from one delta.</summary>
    public void Feed(JsonElement toolCalls)
    {
        var fallbackIndex = 0;

        foreach (var call in toolCalls.EnumerateArray())
        {
            var index = call.TryGetProperty("index", out var indexElement) &&
                        indexElement.TryGetInt32(out var parsed)
                ? parsed
                : fallbackIndex;

            fallbackIndex++;

            if (!_fragments.TryGetValue(index, out var fragment))
            {
                fragment = new Fragment();
                _fragments[index] = fragment;
            }

            if (call.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } idValue)
            {
                fragment.Id = idValue;
            }

            if (!call.TryGetProperty("function", out var function))
            {
                continue;
            }

            if (function.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } nameValue)
            {
                fragment.Name = nameValue;
            }

            if (function.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.String)
            {
                fragment.Arguments.Append(arguments.GetString());
            }
        }
    }

    /// <summary>Returns the finished calls and resets for the next turn.</summary>
    public IReadOnlyList<ToolCall> Complete()
    {
        if (_fragments.Count == 0)
        {
            return [];
        }

        var calls = new List<ToolCall>(_fragments.Count);

        foreach (var (index, fragment) in _fragments)
        {
            // A fragment with no name is unusable; the model never finished emitting it.
            if (string.IsNullOrEmpty(fragment.Name))
            {
                continue;
            }

            calls.Add(new ToolCall(
                fragment.Id ?? $"call_{index}",
                fragment.Name,
                fragment.Arguments.ToString()));
        }

        _fragments.Clear();
        return calls;
    }
}
