using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

namespace ZX0ai.Core.Providers;

/// <summary>
/// OpenRouter adapter: one gateway, every vendor, OpenAI-compatible wire format.
/// </summary>
/// <remarks>
/// Streams with SSE. Errors are translated into <see cref="ChatProviderException"/>
/// carrying a .resw key, so nothing above this layer has to reason about HTTP.
/// </remarks>
public sealed class OpenRouterProvider(
    IConfigService config,
    IOpenRouterCapabilityAdapter capabilityAdapter,
    HttpClient httpClient,
    ILogger<OpenRouterProvider> logger) : IChatProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string Name => "openrouter";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveCredential());

    /// <summary>
    /// Credential for the active tier, read from the environment on every request.
    /// </summary>
    /// <remarks>
    /// Each tier may name its own variable, so switching tiers switches key without
    /// anything being stored in the app. Reading per request also means rotating a
    /// variable takes effect on the next message rather than the next launch.
    /// </remarks>
    private string? ResolveCredential() => config.ResolveCredential(config.ActiveTier);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(ModelInvocation.Direct(model), messages, tools: null, cancellationToken);

    public IAsyncEnumerable<ChatDelta> StreamAsync(
        ModelInvocation invocation,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(invocation, messages, tools: null, cancellationToken);

    /// <summary>
    /// Streams a completion, optionally exposing <paramref name="tools"/> to the model.
    /// </summary>
    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var delta in StreamAsync(
            ModelInvocation.Direct(model),
            messages,
            tools,
            cancellationToken).ConfigureAwait(false))
        {
            yield return delta;
        }
    }

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

        OpenRouterAdaptedOptions adapted;
        try
        {
            adapted = capabilityAdapter.Adapt(invocation);
        }
        catch (InvalidOperationException ex)
        {
            throw new ChatProviderException(
                ChatFailureReason.ModelError,
                "Error_ModelFailed",
                ex.Message,
                ex);
        }

        var response = await SendWithAffordabilityRetryAsync(
            invocation, adapted, messages, tools, credential, cancellationToken).ConfigureAwait(false);

        using (response)
        {
            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var parser = new SseParser();
            var accumulator = new ToolCallAccumulator();
            var state = new StreamState();

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (!parser.TryFeedLine(line, out var payload))
                {
                    if (line is null)
                    {
                        break;
                    }

                    continue;
                }

                if (SseParser.IsDone(payload))
                {
                    break;
                }

                foreach (var delta in ParseChunk(payload, accumulator, state))
                {
                    yield return delta;
                }
            }

            // Tool calls arrive fragmented across chunks; emit them once complete.
            foreach (var call in accumulator.Complete())
            {
                yield return ChatDelta.Tool(call);
            }

            yield return ChatDelta.Done(state.Model);
        }
    }

    // ------------------------------------------------------------------ //
    // Request
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Sends the request, and once, transparently, retries it at a lower output ceiling
    /// if OpenRouter rejects the first attempt as unaffordable.
    /// </summary>
    /// <remarks>
    /// OpenRouter's rejection states the account's exact affordable ceiling — there is
    /// nothing to guess. A model tier is configured with a generous default so it reads
    /// well when credits allow it, but that default should never be the reason a plain
    /// "hi" fails outright when the honest answer is "ask for fewer tokens." The retry
    /// happens here, before anything has streamed, so it is invisible to every caller.
    /// </remarks>
    private async Task<HttpResponseMessage> SendWithAffordabilityRetryAsync(
        ModelInvocation invocation,
        OpenRouterAdaptedOptions adapted,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        string credential,
        CancellationToken cancellationToken)
    {
        int? maxTokensOverride = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = BuildRequest(invocation, adapted, messages, tools, credential, maxTokensOverride);

            HttpResponseMessage response;
            try
            {
                response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "OpenRouter request failed at the transport layer.");
                throw new ChatProviderException(ChatFailureReason.Network, "Error_Network", ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout surfaces as cancellation without the token being signalled.
                throw new ChatProviderException(ChatFailureReason.Network, "Error_Network", ex.Message, ex);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var body = await ReadErrorBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();

            logger.LogError(
                "OpenRouter returned {Status} for model {Model}: {Body}",
                (int)response.StatusCode,
                invocation.ResolvedSlug,
                body);

            var exception = ChatProviderException.FromStatus((int)response.StatusCode, body);

            var affordable = 0;
            var canRetry = attempt == 0 &&
                exception.Reason == ChatFailureReason.InsufficientCredits &&
                OpenRouterAffordability.TryParseAffordableTokens(body, out affordable);

            if (!canRetry)
            {
                throw exception;
            }

            logger.LogInformation(
                "Retrying with max output reduced to the account's affordable ceiling of {Tokens} tokens.",
                affordable);
            maxTokensOverride = affordable;
        }

        // Unreachable: the loop always either returns or throws.
        throw new ChatProviderException(ChatFailureReason.Unknown, "Error_ModelFailed");
    }

    private HttpRequestMessage BuildRequest(
        ModelInvocation invocation,
        OpenRouterAdaptedOptions adapted,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        string credential,
        int? maxTokensOverride = null)
    {
        var options = config.Options.OpenRouter;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = invocation.ResolvedSlug,
            ["stream"] = true,
            ["messages"] = messages.Select(ToWireMessage).ToArray(),
        };

        if (adapted.Reasoning is not null)
        {
            payload["reasoning"] = adapted.Reasoning;
        }

        if (adapted.ProviderRouting is not null)
        {
            payload["provider"] = adapted.ProviderRouting;
        }

        if (adapted.OutputLimit is not null)
        {
            foreach (var (name, value) in adapted.OutputLimit)
            {
                // The retry overrides the value but keeps whichever field name the
                // capability adapter chose — some models need max_completion_tokens
                // rather than max_tokens, and that choice does not change on a retry.
                payload[name] = maxTokensOverride ?? value;
            }
        }

        if (adapted.SupportsTools && tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => t.ToWire()).ToArray();
            if (adapted.SupportsToolChoice)
            {
                payload["tool_choice"] = "auto";
            }
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            OpenRouterEndpointPolicy.Build(options, "chat/completions"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(credential)) {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {credential}");
        }

        // OpenRouter uses these for app attribution on its dashboards.
        request.Headers.TryAddWithoutValidation("HTTP-Referer", options.AppUrl);
        request.Headers.TryAddWithoutValidation("X-Title", options.AppName);

        return request;
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

        // Tool results must carry the id of the call they answer.
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

    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // OpenRouter wraps failures as { "error": { "message": "..." } }.
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? raw;
            }

            return raw;
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            return response.StatusCode.ToString();
        }
    }

    // ------------------------------------------------------------------ //
    // Response
    // ------------------------------------------------------------------ //

    /// <summary>Carries values that outlive a single chunk.</summary>
    private sealed class StreamState
    {
        public string? Model { get; set; }
    }

    /// <summary>
    /// Projects one SSE chunk onto zero or more deltas. Malformed chunks are skipped
    /// rather than thrown: a single bad frame must not kill a long generation.
    /// </summary>
    private IEnumerable<ChatDelta> ParseChunk(
        string payload,
        ToolCallAccumulator accumulator,
        StreamState state)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipped a malformed SSE chunk.");
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("model", out var modelElement) &&
                modelElement.ValueKind == JsonValueKind.String)
            {
                state.Model = modelElement.GetString();
            }

            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                var promptTokens = ReadInt(usage, "prompt_tokens");
                var completionTokens = ReadInt(usage, "completion_tokens");
                var totalTokens = ReadInt(usage, "total_tokens");
                decimal? cost = null;

                if (usage.TryGetProperty("cost", out var costElement) &&
                    costElement.TryGetDecimal(out var parsedCost))
                {
                    cost = parsedCost;
                }

                yield return ChatDelta.UsageUpdate(new ProviderUsage(
                    promptTokens,
                    completionTokens,
                    totalTokens,
                    cost));
            }

            // An error can arrive mid-stream, after a 200.
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw new ChatProviderException(
                    ChatFailureReason.ModelError, "Error_ModelFailed", message);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("reasoning", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String &&
                    reasoning.GetString() is { Length: > 0 } reasoningText)
                {
                    yield return new ChatDelta(ChatDeltaKind.Reasoning, reasoningText);
                }

                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } text)
                {
                    yield return ChatDelta.Content(text);
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
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
}


