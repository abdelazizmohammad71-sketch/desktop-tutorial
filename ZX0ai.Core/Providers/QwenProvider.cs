using System.Net;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

/// <summary>
/// Qwen adapter: direct Qwen v1 chat completion streaming.
/// </summary>
/// <remarks>
/// Streams OpenAI-compatible SSE deltas and supports tool calling with the same
/// transport contract as the existing OpenRouter provider.
/// </remarks>
public sealed class QwenProvider(
    IConfigService config,
    HttpClient httpClient,
    ILogger<QwenProvider> logger) : IChatProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name => "qwen";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveCredential());

    private string? ResolveCredential() => config.ResolveCredential(config.ActiveTier);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(ModelInvocation.Direct(model), messages, cancellationToken);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(invocation, messages, tools: null, cancellationToken);

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credential = ResolveCredential();
        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new ChatProviderException(ChatFailureReason.NotConfigured, "Error_NoApiKey");
        }

        var payload = BuildPayload(invocation, messages, tools);
        using var request = new HttpRequestMessage(HttpMethod.Post, QwenEndpointPolicy.Build(config.Options.Qwen, "chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential}");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Qwen request failed at the transport layer.");
            throw new ChatProviderException(ChatFailureReason.Network, "Error_Network", ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatProviderException(ChatFailureReason.Network, "Error_Network", ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            logger.LogError("Qwen returned {Status} for model {Model}: {Body}", (int)response.StatusCode, invocation.ResolvedSlug, body);
            throw ChatProviderException.FromStatus((int)response.StatusCode, body);
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var parser = new SseParser();
            var accumulator = new ToolCallAccumulator();
            var state = new StreamState();

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!parser.TryFeedLine(line, out var eventPayload))
                {
                    if (line is null)
                    {
                        break;
                    }
                    continue;
                }

                if (SseParser.IsDone(eventPayload))
                {
                    break;
                }

                foreach (var delta in ParseChunk(eventPayload, accumulator, state))
                {
                    yield return delta;
                }
            }

            foreach (var call in accumulator.Complete())
            {
                yield return ChatDelta.Tool(call);
            }

            yield return ChatDelta.Done(state.Model);
        }
    }

    private static object BuildPayload(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools)
    {
        return new
        {
            model = invocation.ResolvedSlug,
            stream = true,
            temperature = 0.7m,
            top_p = 0.95m,
            max_tokens = 2048,
            messages = messages.Select(ToWireMessage).ToArray(),
            tools = tools?.Count > 0 ? tools.Select(t => t.ToWire()).ToArray() : null,
            tool_choice = tools is { Count: > 0 } ? "auto" : null,
        };
    }

    private static Dictionary<string, object?> ToWireMessage(ChatMessage message)
    {
        var wire = new Dictionary<string, object?>
        {
            ["role"] = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => "user",
            },
            ["content"] = message.Content,
        };

        if (message.Role == ChatRole.Tool && message.ToolCallId is { Length: > 0 } id)
        {
            wire["tool_call_id"] = id;
        }

        if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 } calls)
        {
            wire["tool_calls"] = calls.Select(call => new Dictionary<string, object?>
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            }).ToArray();
        }

        return wire;
    }

    private static IEnumerable<ChatDelta> ParseChunk(
        string payload,
        ToolCallAccumulator accumulator,
        StreamState state)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                state.Model = modelElement.GetString();
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var promptTokens = ReadInt(usage, "prompt_tokens");
                var completionTokens = ReadInt(usage, "completion_tokens");
                var totalTokens = ReadInt(usage, "total_tokens");
                decimal? cost = null;
                if (usage.TryGetProperty("cost", out var costElement) && costElement.TryGetDecimal(out var parsedCost))
                {
                    cost = parsedCost;
                }

                yield return ChatDelta.UsageUpdate(new ProviderUsage(
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    cost));
            }

            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new ChatProviderException(ChatFailureReason.ModelError, "Error_ModelFailed", message);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String && reasoning.GetString() is { Length: > 0 } reasoningText)
                {
                    yield return ChatDelta.Reasoning(reasoningText);
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    yield return ChatDelta.Content(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    accumulator.Feed(toolCalls);
                }
            }
        }
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? raw;
            }

            return raw;
        }
        catch (Exception)
        {
            return response.StatusCode.ToString();
        }
    }

    private sealed class StreamState
    {
        public string? Model { get; set; }
    }
}
