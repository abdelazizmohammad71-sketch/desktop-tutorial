using ZX0ai.Core.Providers;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

namespace ZX0ai.Backend;

internal static class BackendEndpoints
{
    internal static IResult Health(
        IConfigService config,
        IChatProvider provider,
        IOpenRouterCatalogService catalog,
        IProjectWorkspaceService workspace)
    {
        var tiers = TierContractMapper.MapAll(config, catalog);
        var currentWorkspace = workspace.CurrentWorkspace;

        return TypedResults.Ok(new HealthResponse(
            "ok",
            "ZX0ai.Backend",
            provider.Name,
            provider.IsConfigured,
            tiers.Count,
            tiers.Count(tier => tier.Runnable),
            catalog.Current.FetchedAt == default ? null : catalog.Current.FetchedAt,
            catalog.Current.Models.Count,
            currentWorkspace.HasProject,
            currentWorkspace.IsAvailable));
    }

    internal static IResult Tiers(
        IConfigService config,
        IOpenRouterCatalogService catalog) =>
        TypedResults.Ok(TierContractMapper.MapAll(config, catalog));

    internal static async Task<IResult> RefreshModels(
        IOpenRouterCatalogService catalog,
        IConfigService config,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
            var tiers = TierContractMapper.MapAll(config, catalog);
            return TypedResults.Ok(new ModelRefreshResponse(
                snapshot.FetchedAt,
                snapshot.Models.Count,
                tiers.Count(tier => tier.Runnable),
                tiers));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TypedResults.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            loggerFactory.CreateLogger("ZX0ai.Backend.Models")
                .LogWarning(ex, "OpenRouter model refresh failed.");
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Model catalog unavailable",
                detail: "The live OpenRouter catalog could not be refreshed.");
        }
    }

    internal static IResult Skills(
        ISkillRegistry registry,
        IProjectWorkspaceService workspace)
    {
        var current = workspace.CurrentWorkspace;
        var skills = registry.All
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .Select(skill => new SkillResponse(
                skill.Name,
                skill.Description,
                skill.IsDestructive,
                IsEnabled(skill.Name, current)))
            .ToList();

        return TypedResults.Ok(skills);
    }

    internal static IResult AgentRun(string runId, AgentRunStore runs)
    {
        if (!Guid.TryParseExact(runId, "N", out _) || !runs.TryGet(runId, out var snapshot))
        {
            return TypedResults.NotFound(new ApiError(
                "run_not_found",
                "No agent run with that id is available."));
        }

        return TypedResults.Ok(snapshot);
    }

    private static bool IsEnabled(
        string skillName,
        ZX0ai.Core.Workspaces.WorkspaceContext workspace) => skillName switch
    {
        "fetch_url" or "web_search" => workspace.IsAvailable && workspace.Policy.CanUseNetwork,
        "read_file" => workspace.IsAvailable,
        "write_file" => workspace.IsAvailable && workspace.Policy.CanWriteFiles,
        "run_command" => workspace.IsAvailable && workspace.Policy.CanRunCommands,
        _ => false,
    };
}
