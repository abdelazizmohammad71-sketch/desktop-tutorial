using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Services;

/// <summary>
/// The folder the agent is allowed to work in, and what it may do there.
/// </summary>
/// <remarks>
/// <para>
/// Everything the agent can touch is defined here. A bound folder becomes the root of a
/// <see cref="WorkspaceContext"/>, and <see cref="WorkspacePathGuard"/> refuses any path
/// that resolves outside it — including one that leaves through a symlink or junction.
/// With no folder bound the policy is <see cref="ExecutionPolicy.ReadOnly"/>, which
/// grants no file writes and no commands at all, so the failure mode of "the user never
/// chose a folder" is that nothing runs rather than that everything runs somewhere
/// arbitrary.
/// </para>
/// <para>
/// The binding is persisted, because being asked for the folder on every launch is the
/// fastest way to train someone to click through the one prompt that decides what an
/// autonomous agent is allowed to modify.
/// </para>
/// </remarks>
public sealed class AgentWorkspace
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _statePath;
    private readonly ILogger<AgentWorkspace> _logger;

    private string? _root;
    private bool _fullAccess;

    public AgentWorkspace(string statePath, ILogger<AgentWorkspace> logger)
    {
        _statePath = statePath;
        _logger = logger;

        Restore();
    }

    /// <summary>Absolute path of the bound folder, or null when nothing is bound.</summary>
    public string? Root => _root;

    /// <summary>Just the folder name, for the chip in the title bar.</summary>
    public string DisplayName => _root is null
        ? "No folder"
        : Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name
            ? name
            : _root;

    public bool IsBound => _root is not null && Directory.Exists(_root);

    /// <summary>
    /// Lifts the sandbox from workspace-write to full access.
    /// </summary>
    /// <remarks>
    /// This does not widen the path boundary — writes stay inside the bound folder
    /// either way. What it changes is the command layer: workspace-write permits only
    /// the broker's routine command set, and full access permits anything the policy
    /// does not consider dangerous. Commands on the dangerous list still require a
    /// per-command confirmation, which is the one gate no mode removes.
    /// </remarks>
    public bool FullAccess
    {
        get => _fullAccess;
        set
        {
            if (_fullAccess == value)
            {
                return;
            }

            _fullAccess = value;
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    /// <summary>The binding as the skills see it.</summary>
    public WorkspaceContext Current
    {
        get
        {
            if (_root is null || !Directory.Exists(_root))
            {
                return WorkspaceContext.WithoutProject("zx0ai");
            }

            var policy = new ExecutionPolicy(
                _fullAccess ? SandboxMode.FullAccess : SandboxMode.WorkspaceWrite,
                ApprovalPolicy.OnRequest,
                NetworkEnabled: true,
                FullAccessConfirmed: _fullAccess);

            return WorkspaceContext.ForProject("zx0ai", "workspace", _root, policy);
        }
    }

    /// <summary>Binds a folder. Throws if the path is not a usable directory.</summary>
    public void Bind(string path)
    {
        _root = WorkspacePathGuard.CanonicalizeDirectory(path);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unbind()
    {
        _root = null;
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Restore()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<WorkspaceState>(
                File.ReadAllText(_statePath),
                Json);

            if (state is null)
            {
                return;
            }

            _fullAccess = state.FullAccess;

            // Re-canonicalised rather than trusted: the folder may have been moved,
            // deleted or replaced by a link since it was written.
            if (!string.IsNullOrWhiteSpace(state.Root) && Directory.Exists(state.Root))
            {
                _root = WorkspacePathGuard.CanonicalizeDirectory(state.Root);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                        JsonException or ArgumentException or
                                        DirectoryNotFoundException)
        {
            _logger.LogWarning(ex, "Could not restore the workspace binding.");
            _root = null;
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(
                _statePath,
                JsonSerializer.Serialize(new WorkspaceState(_root, _fullAccess), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the workspace binding.");
        }
    }

    private sealed record WorkspaceState(string? Root, bool FullAccess);
}
