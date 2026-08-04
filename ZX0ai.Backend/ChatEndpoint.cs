using ZX0ai.Core.Composition;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Services;

namespace ZX0ai.Backend;

internal static class ChatEndpoint
{
    private const int MaximumMessages = 64;
    private const int MaximumMessageCharacters = 64 * 1024;
    private const int MaximumTranscriptCharacters = 256 * 1024;
    private const int MaximumEventTextCharacters = 16 * 1024;

    private static readonly JsonSerializerOptions EventJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static async Task StreamAsync(
        ChatStreamRequest? request,
        HttpContext context,
        IConfigService config,
        IOpenRouterCatalogService catalog,
        IChatProvider provider,
        IProjectWorkspaceService workspace,
        IAgentOrchestrator orchestrator,
        IProjectTaskContextService projectContext,
        AgentRunStore runs,
        ChatExecutionGate executionGate,
        ILoggerFactory loggerFactory)
    {
        var cancellationToken = context.RequestAborted;
        if (Validate(request) is { } error)
        {
            await WriteJsonErrorAsync(context.Response, StatusCodes.Status400BadRequest, error, cancellationToken);
            return;
        }

        var tier = config.FindTier(request!.Tier.Trim());
        if (tier is null)
        {
            await WriteJsonErrorAsync(
                context.Response,
                StatusCodes.Status404NotFound,
                new ApiError("tier_not_found", "The requested tier is not configured."),
                cancellationToken);
            return;
        }

        if (!provider.IsConfigured)
        {
            await WriteJsonErrorAsync(
                context.Response,
                StatusCodes.Status503ServiceUnavailable,
                new ApiError(
                    "provider_not_configured",
                    "Set OPENROUTER_API_KEY in the backend process environment."),
                cancellationToken);
            return;
        }

        var tierContract = TierContractMapper.Map(tier, config, catalog);
        var singleInvocation = tier.IsTeam
            ? null
            : TierContractMapper.InvocationForSingle(tier, catalog);
        if (!tierContract.Runnable || (!tier.IsTeam && singleInvocation is null))
        {
            await WriteJsonErrorAsync(
                context.Response,
                StatusCodes.Status409Conflict,
                new ApiError(
                    "tier_unrunnable",
                    "One or more configured models are unavailable; refresh the model catalog or configure an explicit fallback."),
                cancellationToken);
            return;
        }

        try
        {
            // Pull the latest atomically persisted desktop state, then freeze that
            // binding for this request before any model receives file-capable tools.
            await workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loggerFactory.CreateLogger("ZX0ai.Backend.Workspace")
                .LogWarning(ex, "Workspace state could not be loaded for a chat run.");
            await WriteJsonErrorAsync(
                context.Response,
                StatusCodes.Status503ServiceUnavailable,
                new ApiError("workspace_unavailable", "The local workspace state could not be loaded."),
                cancellationToken);
            return;
        }

        var history = request.Messages.Select(MapMessage).ToList();
        var run = runs.Create(tier);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        await WriteEventAsync(
            context.Response,
            "run.started",
            new RunStartedEvent(run.RunId, tier.Key, tier.DisplayName, tierContract.Mode),
            cancellationToken);

        try
        {
            using var lease = await executionGate.EnterAsync(cancellationToken).ConfigureAwait(false);

            if (tier.IsTeam)
            {
                await StreamTeamAsync(
                    tier,
                    history,
                    run.RunId,
                    context.Response,
                    orchestrator,
                    runs,
                    await ComposeContextAsync(projectContext, workspace, history, cancellationToken),
                    request.PlanOnly ? AgentRunOptions.Plan : AgentRunOptions.Default,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StreamSingleAsync(
                    tier,
                    singleInvocation!.Value,
                    history,
                    run.RunId,
                    context.Response,
                    provider,
                    runs,
                    cancellationToken).ConfigureAwait(false);
            }

            runs.Complete(run.RunId);
            await WriteEventAsync(
                context.Response,
                "run.completed",
                new RunCompletedEvent(run.RunId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runs.Cancel(run.RunId);
        }
        catch (ChatProviderException ex)
        {
            var code = FailureCode(ex.Reason);
            runs.Fail(run.RunId, code);
            loggerFactory.CreateLogger("ZX0ai.Backend.Chat")
                .LogWarning(ex, "Provider failed during run {RunId}.", run.RunId);
            await TryWriteStreamErrorAsync(context.Response, code, SafeFailureMessage(ex.Reason), cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            runs.Fail(run.RunId, "provider_timeout");
            loggerFactory.CreateLogger("ZX0ai.Backend.Chat")
                .LogWarning(ex, "Provider timed out during run {RunId}.", run.RunId);
            await TryWriteStreamErrorAsync(
                context.Response,
                "provider_timeout",
                "The provider did not complete the request in time.",
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            runs.Fail(run.RunId, "tier_unrunnable");
            loggerFactory.CreateLogger("ZX0ai.Backend.Chat")
                .LogWarning(ex, "Tier validation changed during run {RunId}.", run.RunId);
            await TryWriteStreamErrorAsync(
                context.Response,
                "tier_unrunnable",
                "The selected tier became unavailable before the run started.",
                cancellationToken);
        }
        catch (Exception ex)
        {
            runs.Fail(run.RunId, "internal_error");
            loggerFactory.CreateLogger("ZX0ai.Backend.Chat")
                .LogError(ex, "Unexpected failure during run {RunId}.", run.RunId);
            await TryWriteStreamErrorAsync(
                context.Response,
                "internal_error",
                "The local backend could not complete this run.",
                cancellationToken);
        }
    }

    /// <summary>
    /// Composes the project's AGENTS.md instructions, layered configuration and any
    /// matched SKILL.md package for this run.
    /// </summary>
    /// <remarks>
    /// Failures degrade to null rather than aborting the stream: unreadable project
    /// guidance should cost the caller that guidance, not their answer. The service
    /// itself is fail-closed when no project is bound.
    /// </remarks>
    private static async Task<ProjectTaskContext?> ComposeContextAsync(
        IProjectTaskContextService projectContext,
        IProjectWorkspaceService workspace,
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken)
    {
        var task = history.LastOrDefault(message => message.Role == ChatRole.User)?.Content;
        if (string.IsNullOrWhiteSpace(task))
        {
            return null;
        }

        try
        {
            return await projectContext
                .BuildAsync(workspace.CurrentWorkspace, task, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _ = ex;
            return null;
        }
    }

    private static async Task StreamTeamAsync(
        ModelTier tier,
        IReadOnlyList<ChatMessage> history,
        string runId,
        HttpResponse response,
        IAgentOrchestrator orchestrator,
        AgentRunStore runs,
        ProjectTaskContext? projectContext,
        AgentRunOptions runOptions,
        CancellationToken cancellationToken)
    {
        await foreach (var update in orchestrator
                           .RunAsync(tier, history, projectContext, runOptions, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (update.Kind)
            {
                case OrchestrationUpdateKind.TurnStarted when update.Turn is { } turn:
                    runs.StartTurn(runId, turn);
                    await WriteEventAsync(
                        response,
                        "turn.started",
                        new TurnStartedEvent(
                            turn.Id,
                            turn.AgentId,
                            turn.AgentName,
                            turn.Role.ToString().ToLowerInvariant(),
                            turn.Model,
                            turn.AccentArgb,
                            SafeText(turn.ReasoningSummary, 512),
                            turn.IsFinalAnswer),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case OrchestrationUpdateKind.TurnDelta when update.Turn is { } turn:
                    runs.AppendTurn(runId, turn.Id, update.Text);
                    foreach (var chunk in Chunk(update.Text))
                    {
                        await WriteEventAsync(
                            response,
                            "turn.delta",
                            new TurnDeltaEvent(turn.Id, chunk),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case OrchestrationUpdateKind.TurnCompleted when update.Turn is { } turn:
                    runs.CompleteTurn(runId, turn);
                    await WriteEventAsync(
                        response,
                        "turn.completed",
                        new TurnCompletedEvent(turn.Id, turn.Status),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case OrchestrationUpdateKind.FinalAnswer:
                    runs.AppendAnswer(runId, update.Text);
                    foreach (var chunk in Chunk(update.Text))
                    {
                        await WriteEventAsync(
                            response,
                            "answer.delta",
                            new AnswerDeltaEvent(chunk),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case OrchestrationUpdateKind.Warning:
                    await WriteEventAsync(
                        response,
                        "warning",
                        new WarningEvent(SafeText(update.Text, 512)),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task StreamSingleAsync(
        ModelTier tier,
        (string RequestedSlug, string ResolvedSlug) resolution,
        IReadOnlyList<ChatMessage> history,
        string runId,
        HttpResponse response,
        IChatProvider provider,
        AgentRunStore runs,
        CancellationToken cancellationToken)
    {
        var invocation = new ModelInvocation(
            resolution.RequestedSlug,
            resolution.ResolvedSlug,
            "provider-default",
            tier.Speed);

        await foreach (var delta in provider
                           .StreamAsync(invocation, history, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (delta.Kind)
            {
                case ChatDeltaKind.Content:
                    runs.AppendAnswer(runId, delta.Text);
                    foreach (var chunk in Chunk(delta.Text))
                    {
                        await WriteEventAsync(
                            response,
                            "answer.delta",
                            new AnswerDeltaEvent(chunk),
                            cancellationToken).ConfigureAwait(false);
                    }
                    break;

                case ChatDeltaKind.Usage when delta.Usage is { } usage:
                    await WriteEventAsync(
                        response,
                        "usage",
                        new UsageEvent(
                            usage.PromptTokens,
                            usage.CompletionTokens,
                            usage.TotalTokens,
                            usage.Cost),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static ApiError? Validate(ChatStreamRequest? request)
    {
        if (request is null)
        {
            return new ApiError("invalid_request", "A JSON request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Tier) || request.Tier.Length > 128)
        {
            return new ApiError("invalid_tier", "Provide a configured tier key.");
        }

        if (request.Messages is null || request.Messages.Count is 0 or > MaximumMessages)
        {
            return new ApiError(
                "invalid_messages",
                $"Provide between 1 and {MaximumMessages} transcript messages.");
        }

        var total = 0;
        foreach (var message in request.Messages)
        {
            if (message is null ||
                !IsAcceptedRole(message.Role) ||
                string.IsNullOrWhiteSpace(message.Content) ||
                message.Content.Length > MaximumMessageCharacters)
            {
                return new ApiError(
                    "invalid_message",
                    "Messages must contain a user or assistant role and bounded non-empty content.");
            }

            total += message.Content.Length;
            if (total > MaximumTranscriptCharacters)
            {
                return new ApiError(
                    "transcript_too_large",
                    "The transcript is too large for one request.");
            }
        }

        if (!string.Equals(
                request.Messages[^1].Role,
                "user",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ApiError("user_turn_required", "The transcript must end with a user message.");
        }

        return null;
    }

    private static bool IsAcceptedRole(string? role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);

    private static ChatMessage MapMessage(ChatInputMessage message) => new()
    {
        Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? ChatRole.Assistant
            : ChatRole.User,
        Content = message.Content,
    };

    private static IEnumerable<string> Chunk(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        for (var offset = 0; offset < text.Length; offset += MaximumEventTextCharacters)
        {
            yield return text.Substring(
                offset,
                Math.Min(MaximumEventTextCharacters, text.Length - offset));
        }
    }

    private static string SafeText(string? text, int maximum) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Length <= maximum ? text : text[..maximum];

    private static async Task WriteJsonErrorAsync(
        HttpResponse response,
        int statusCode,
        ApiError error,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        await response.WriteAsJsonAsync(error, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, EventJson);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken)
            .ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteStreamErrorAsync(
        HttpResponse response,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await WriteEventAsync(
                response,
                "error",
                new StreamErrorEvent(code, message),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client disconnected while the terminal event was being written.
        }
        catch (IOException)
        {
            // The run snapshot already carries the failure for later inspection.
        }
    }

    private static string FailureCode(ChatFailureReason reason) => reason switch
    {
        ChatFailureReason.NotConfigured => "provider_not_configured",
        ChatFailureReason.Unauthorized => "provider_unauthorized",
        ChatFailureReason.RateLimited => "provider_rate_limited",
        ChatFailureReason.ServerError => "provider_server_error",
        ChatFailureReason.ModelError => "model_error",
        ChatFailureReason.Network => "provider_network_error",
        _ => "provider_error",
    };

    private static string SafeFailureMessage(ChatFailureReason reason) => reason switch
    {
        ChatFailureReason.NotConfigured => "OpenRouter is not configured.",
        ChatFailureReason.Unauthorized => "OpenRouter rejected the configured credential.",
        ChatFailureReason.RateLimited => "OpenRouter rate-limited the request or the account has insufficient credit.",
        ChatFailureReason.ServerError => "OpenRouter or the selected model is temporarily unavailable.",
        ChatFailureReason.ModelError => "The selected model rejected the request.",
        ChatFailureReason.Network => "The backend could not reach OpenRouter.",
        _ => "The provider could not complete the request.",
    };
}
