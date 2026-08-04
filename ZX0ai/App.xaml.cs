using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using ZX0ai.Core.Commands;
using ZX0ai.Core.Git;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Security;
using ZX0ai.Core.Services;
using ZX0ai.Core.Skills;
using ZX0ai.Services;
using ZX0ai.ViewModels;

namespace ZX0ai;

/// <summary>
/// Composition root: builds the container, loads configuration, then shows the window.
/// </summary>
/// <remarks>
/// Only what the shell actually calls is registered. The agent orchestrator, the skill
/// registry and the command runner all still exist in <c>ZX0ai.Core</c>, but nothing in
/// this UI drives them yet, and registering a service no surface resolves would make the
/// startup graph read as larger than the product is.
/// </remarks>
public partial class App : Application
{
    private static IServiceProvider? _services;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Resolves a registered service. Throws if the container is not ready.</summary>
    public static T GetService<T>() where T : notnull =>
        (_services ?? throw new InvalidOperationException("Service provider is not initialised."))
            .GetRequiredService<T>();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ = args;
        _ = StartAsync();
    }

    /// <summary>
    /// Startup runs detached from the launch callback, so a failure here would otherwise
    /// surface only as a blank window. Record it where it can be read.
    /// </summary>
    private async Task StartAsync()
    {
        try
        {
            _services = BuildServiceProvider();

            // Configuration first: the tier list, the model and the user name all
            // depend on it, and the shell reads all three in its constructor.
            await _services.GetRequiredService<IConfigService>().LoadAsync().ConfigureAwait(true);

            // Validates the configured slugs against the live model list and resolves
            // fallbacks. Deliberately not fatal: this is the one startup step that needs
            // the network, and being offline should cost model validation, not the app.
            try
            {
                await _services
                    .GetRequiredService<IOpenRouterCatalogService>()
                    .InitializeAsync()
                    .ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                _services
                    .GetRequiredService<ILogger<App>>()
                    .LogWarning(ex, "Model catalog unavailable; continuing with configured slugs.");
            }

            // Forced now, on the UI thread, so it captures the dispatcher it needs to
            // marshal process output with. Resolved lazily it would capture whichever
            // thread first opened the terminal panel.
            _ = _services.GetRequiredService<TerminalSession>();

            // The project list, so the Projects panel opens already populated instead of
            // reading its own state the first time someone looks at it.
            await _services
                .GetRequiredService<IProjectWorkspaceService>()
                .InitializeAsync()
                .ConfigureAwait(true);

            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();

        var localData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZX0ai");

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Shipped defaults ship beside the executable; user overrides and the catalog
        // cache live in LocalAppData. Credentials are in neither — they are read from
        // the environment on every request.
        services.AddSingleton(new ConfigPaths(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(localData, "appsettings.local.json"),
            Path.Combine(localData, "openrouter-catalog.json")));

        services.AddSingleton<IConfigService, ConfigService>();

        services.AddSingleton(_ => new HttpClient
        {
            // Long enough for a slow first token, short enough that a dead endpoint does
            // not hang the window forever. Streaming reads are not bound by this.
            Timeout = TimeSpan.FromMinutes(3),
        });

        services.AddSingleton<OpenRouterCatalogService>();
        services.AddSingleton<IOpenRouterCatalogService>(sp =>
            sp.GetRequiredService<OpenRouterCatalogService>());
        services.AddSingleton<IOpenRouterCapabilityAdapter, OpenRouterCapabilityAdapter>();
        services.AddSingleton<OpenRouterProvider>();
        services.AddSingleton<QwenProvider>();
        services.AddSingleton<ConfiguredChatProvider>();
        services.AddSingleton<IChatProvider>(sp => sp.GetRequiredService<ConfiguredChatProvider>());

        // DashScope verification service (placeholder) — UI can call VerifyAsync; implementation
        // currently returns an informational "not configured" result until a real endpoint
        // and verification format are supplied.
        services.AddSingleton<IDashScopeService, DashScopeService>();

        services.AddSingleton(sp => new SessionStore(
            Path.Combine(localData, "sessions"),
            sp.GetRequiredService<ILogger<SessionStore>>()));

        // Separate state directory from SessionStore above: this is the Projects panel's
        // own record of named projects and their per-project chat history, distinct from
        // the flat session list the main rail reads. See ProjectsPanel's remarks for why
        // the two are not yet merged.
        services.AddSingleton(new WorkspaceStatePaths(Path.Combine(localData, "projects-state")));
        services.AddSingleton<ProjectWorkspaceService>();
        services.AddSingleton<IProjectWorkspaceService>(sp =>
            sp.GetRequiredService<ProjectWorkspaceService>());

        // Read-only: status and diff, never a mutation. Backs the branch name in the
        // composer breadcrumb; branch switching and commits belong to the Git panel.
        services.AddSingleton<IGitCommandExecutor, DirectGitCommandExecutor>();
        services.AddSingleton<IGitRepositoryService, GitRepositoryService>();

        // ------------------------- Agent capabilities ----------------------- //
        // The workspace defines the boundary; the skills act inside it. Nothing here
        // can reach outside the bound folder: file paths go through WorkspacePathGuard
        // and commands through CommandPolicy.
        services.AddSingleton(sp => new AgentWorkspace(
            Path.Combine(localData, "workspace.json"),
            sp.GetRequiredService<ILogger<AgentWorkspace>>()));

        services.AddSingleton<FolderPickerService>();
        services.AddSingleton<ApprovalDialogService>();
        services.AddSingleton<IActionApprovalService>(sp =>
            sp.GetRequiredService<ApprovalDialogService>());
        services.AddSingleton<ICommandPolicy, CommandPolicy>();
        services.AddSingleton<ICommandRunner, CommandRunner>();

        // Registered as ISkill so the runner discovers them; a new skill is one line
        // here and nothing else.
        services.AddSingleton<ISkill, ListFilesSkill>();
        services.AddSingleton<ISkill, ReadFileSkill>();
        services.AddSingleton<ISkill, WriteFileSkill>();
        services.AddSingleton<ISkill, RunCommandSkill>();
        services.AddSingleton<ISkill, FetchUrlSkill>();
        services.AddSingleton<ISkill, WebSearchSkill>();

        // Delegation is a tool, so a specialist can only ever be reached through the
        // orchestrator and can only ever answer back to it.
        services.AddSingleton<AgentTeam>();
        services.AddSingleton<ISkill, DelegateSkill>();

        services.AddSingleton<ToolRunner>();

        // Singleton, and constructed on the UI thread at startup: it captures its
        // dispatcher there, and it has to be recording before the first command runs
        // so the terminal shows what the agent did rather than starting empty.
        services.AddSingleton<TerminalSession>();

        // Singleton: the rail, the transcript and the run panel are three views of one
        // conversation, so they must resolve the same instance.
        services.AddSingleton<ConversationViewModel>();

        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _ = sender;
        WriteCrashLog(e.Exception);
    }

    private static void WriteCrashLog(Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[ZX0ai] startup failed: {ex}");

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZX0ai");
            Directory.CreateDirectory(directory);

            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Diagnostics must never mask the original failure.
        }
    }
}
