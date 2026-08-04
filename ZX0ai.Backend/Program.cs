using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using ZX0ai.Backend;
using ZX0ai.Core.Agents;
using ZX0ai.Core.Governance;
using ZX0ai.Core.Commands;
using ZX0ai.Core.Composition;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Instructions;
using ZX0ai.Core.Profiles;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;

var builder = WebApplication.CreateBuilder(args);

// This process is a local companion for the native desktop app, not a public API.
// Explicit Kestrel endpoints take precedence over ASPNETCORE_URLS and command-line
// URL switches, so a copied configuration cannot accidentally expose the bearer-key
// backed provider on a LAN interface.
var port = LocalBackendPolicy.ReadPort(Environment.GetEnvironmentVariable("ZX0AI_BACKEND_PORT"));
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Listen(IPAddress.Loopback, port);
    if (Socket.OSSupportsIPv6)
    {
        options.Listen(IPAddress.IPv6Loopback, port);
    }
});

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.MaxDepth = 32;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var localDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ZX0ai");

builder.Services.AddSingleton(new ConfigPaths(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    Path.Combine(localDataDirectory, "appsettings.local.json"),
    Path.Combine(localDataDirectory, "openrouter-catalog.json")));
builder.Services.AddSingleton(new WorkspaceStatePaths(Path.Combine(localDataDirectory, "state")));

builder.Services.AddSingleton<IConfigService, ConfigService>();
builder.Services.AddSingleton<ProjectWorkspaceService>();
builder.Services.AddScoped<IProjectWorkspaceService>(services =>
    new ScopedProjectWorkspaceService(services.GetRequiredService<ProjectWorkspaceService>()));

builder.Services.AddSingleton(_ => new HttpClient
{
    Timeout = TimeSpan.FromMinutes(3),
});
builder.Services.AddSingleton<IOpenRouterCatalogService, OpenRouterCatalogService>();
builder.Services.AddSingleton<IOpenRouterCapabilityAdapter, OpenRouterCapabilityAdapter>();
builder.Services.AddSingleton<OpenRouterProvider>();
builder.Services.AddSingleton<QwenProvider>();
builder.Services.AddSingleton<ConfiguredChatProvider>();
builder.Services.AddSingleton<IChatProvider>(sp => sp.GetRequiredService<ConfiguredChatProvider>());

builder.Services.AddSingleton(services => Constitution.Load(
    AppContext.BaseDirectory,
    services.GetRequiredService<IConfigService>().Options.Ui.Language));
builder.Services.AddScoped<ICommandRunner, CommandRunner>();

// There is intentionally no backend implementation of render_preview: it is a
// desktop-only WebView capability. Every registered skill still passes through the
// project workspace and execution-policy guards in SkillRegistry.
builder.Services.AddScoped<ISkill, FetchUrlSkill>();
builder.Services.AddScoped<ISkill, WebSearchSkill>();
builder.Services.AddScoped<ISkill, ReadFileSkill>();
builder.Services.AddScoped<ISkill, WriteFileSkill>();
builder.Services.AddScoped<ISkill, RunCommandSkill>();
builder.Services.AddScoped<SkillRegistry>();
builder.Services.AddScoped<ISkillRegistry>(services => services.GetRequiredService<SkillRegistry>());
builder.Services.AddSingleton<IBrainFile>(_ => new BrainFile(localDataDirectory));
builder.Services.AddSingleton<IProjectMemory>(_ => new ProjectMemory(localDataDirectory));
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// Project-scoped capabilities, registered identically to the desktop host so an
// orchestration run composes the same instructions, config layers and skills on
// either side. Git is deliberately absent: the backend never renders a diff, and
// exposing repository contents over HTTP is not part of its contract.
builder.Services.AddSingleton<IAgentsInstructionDiscovery>(_ => new AgentsInstructionDiscovery());
builder.Services.AddSingleton<ILayeredProjectConfigurationResolver, LayeredProjectConfigurationResolver>();
builder.Services.AddSingleton<IFileSystemSkillCatalog>(_ => new FileSystemSkillCatalog());
builder.Services.AddSingleton(new ProjectTaskContextPaths(
    ShippedConfigPath: Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    UserConfigPath: Path.Combine(localDataDirectory, "appsettings.local.json"),
    UserSkillsDirectory: FileSystemSkillCatalog.DefaultUserSkillsDirectory));
builder.Services.AddSingleton<IProjectTaskContextService, ProjectTaskContextService>();
builder.Services.AddSingleton<IExecutionProfileCatalog>(_ => new ExecutionProfileCatalog());

builder.Services.AddSingleton<AgentRunStore>();
builder.Services.AddSingleton<ChatExecutionGate>();

var app = builder.Build();

// No CORS middleware is registered. The host check additionally closes DNS-rebinding
// and accidental reverse-proxy paths even though Kestrel itself is loopback-only.
app.Use(async (context, next) =>
{
    if (!LocalBackendPolicy.IsAllowedHost(context.Request.Host.Host))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new ApiError("invalid_host", "ZX0ai.Backend accepts loopback requests only."),
            context.RequestAborted);
        return;
    }

    if (context.Request.QueryString.HasValue)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new ApiError("query_not_supported", "ZX0ai.Backend does not accept query parameters."),
            context.RequestAborted);
        return;
    }

    if (LocalBackendPolicy.HasCredentialHeader(context.Request.Headers))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(
            new ApiError(
                "credential_input_rejected",
                "Provider credentials are read only from the backend process environment."),
            context.RequestAborted);
        return;
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.MapGet("/health", BackendEndpoints.Health);
app.MapGet("/tiers", BackendEndpoints.Tiers);
app.MapPost("/models/refresh", BackendEndpoints.RefreshModels);
app.MapGet("/skills", BackendEndpoints.Skills);
app.MapGet("/agents/{runId}", BackendEndpoints.AgentRun);
app.MapPost("/chat", ChatEndpoint.StreamAsync);

// Initialise the same persisted state graph as the desktop process before Kestrel
// accepts work. The catalog service owns its bounded startup timeout and sanitized
// offline cache fallback.
await app.Services.GetRequiredService<IConfigService>().LoadAsync();
await app.Services.GetRequiredService<IOpenRouterCatalogService>().InitializeAsync();
await app.Services.GetRequiredService<ProjectWorkspaceService>().InitializeAsync();

app.Run();

// Makes WebApplicationFactory-style integration testing possible without changing
// the public HTTP surface.
public partial class Program;
