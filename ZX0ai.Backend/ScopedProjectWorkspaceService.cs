using ZX0ai.Core.Models;
using ZX0ai.Core.Projects;
using ZX0ai.Core.Security;
using ZX0ai.Core.Sessions;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Backend;

/// <summary>
/// Captures an immutable workspace boundary per HTTP request. The underlying index
/// can be refreshed from the desktop process between runs without letting a project
/// switch redirect an already-running agent's file or command tools.
/// </summary>
internal sealed class ScopedProjectWorkspaceService(ProjectWorkspaceService inner)
    : IProjectWorkspaceService
{
    private WorkspaceContext? _captured;

    public IReadOnlyList<ProjectRecord> Projects => inner.Projects;

    public IReadOnlyList<ProjectRecord> ArchivedProjects => inner.ArchivedProjects;

    public ProjectRecord? ActiveProject => inner.ActiveProject;

    public ChatSession? ActiveSession => inner.ActiveSession;

    public WorkspaceContext CurrentWorkspace => _captured ??= inner.CurrentWorkspace;

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await inner.InitializeAsync(cancellationToken).ConfigureAwait(false);
        Capture();
    }

    public async Task<ProjectRecord> AddOrActivateProjectAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var project = await inner
            .AddOrActivateProjectAsync(rootPath, cancellationToken)
            .ConfigureAwait(false);
        Capture();
        return project;
    }

    public async Task ActivateProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await inner.ActivateProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        Capture();
    }

    public async Task<ChatSession> StartChatAsync(
        string? projectId,
        bool readOnlyWithoutProject,
        CancellationToken cancellationToken = default)
    {
        var session = await inner
            .StartChatAsync(projectId, readOnlyWithoutProject, cancellationToken)
            .ConfigureAwait(false);
        Capture();
        return session;
    }

    public async Task OpenChatAsync(
        string projectId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await inner.OpenChatAsync(projectId, sessionId, cancellationToken).ConfigureAwait(false);
        Capture();
    }

    /// <summary>Read-only, so it passes straight through without recapturing state.</summary>
    public Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(
        string? projectId,
        CancellationToken cancellationToken = default) =>
        inner.ListChatsAsync(projectId, cancellationToken);

    public Task SaveActiveSessionAsync(
        IReadOnlyList<ChatMessage> messages,
        string? title = null,
        CancellationToken cancellationToken = default) =>
        inner.SaveActiveSessionAsync(messages, title, cancellationToken);

    public async Task SetPinnedAsync(
        string projectId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        await inner.SetPinnedAsync(projectId, pinned, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetArchivedAsync(
        string projectId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        await inner.SetArchivedAsync(projectId, archived, cancellationToken).ConfigureAwait(false);
        Capture();
    }

    public async Task RenameProjectAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        await inner.RenameProjectAsync(projectId, name, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await inner.RemoveProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        Capture();
    }

    public async Task SetExecutionPolicyAsync(
        ExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await inner.SetExecutionPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        Capture();
    }

    private void Capture()
    {
        _captured = inner.CurrentWorkspace;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
