using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZX0ai.Core.Models;
using ZX0ai.Core.Security;
using ZX0ai.Core.Sessions;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Projects;

public sealed record WorkspaceStatePaths(string StateDirectory);

/// <summary>One saved chat, as the history list needs it.</summary>
/// <param name="Id">Session id, for reopening it.</param>
/// <param name="ProjectId">Owning project, or null for a read-only session.</param>
/// <param name="Title">The chat's title as last saved.</param>
/// <param name="UpdatedAt">When it last changed. Drives ordering and the age label.</param>
/// <param name="MessageCount">How many turns it holds.</param>
public sealed record ChatSessionSummary(
    string Id,
    string? ProjectId,
    string Title,
    DateTimeOffset UpdatedAt,
    int MessageCount);

/// <summary>Owns real projects, active chat persistence, and workspace invariants.</summary>
public interface IProjectWorkspaceService
{
    IReadOnlyList<ProjectRecord> Projects { get; }

    /// <summary>Archived projects, newest-opened first. Not part of <see cref="Projects"/>.</summary>
    IReadOnlyList<ProjectRecord> ArchivedProjects { get; }

    ProjectRecord? ActiveProject { get; }

    ChatSession? ActiveSession { get; }

    WorkspaceContext CurrentWorkspace { get; }

    event EventHandler? Changed;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<ProjectRecord> AddOrActivateProjectAsync(
        string rootPath,
        CancellationToken cancellationToken = default);

    Task ActivateProjectAsync(string projectId, CancellationToken cancellationToken = default);

    Task<ChatSession> StartChatAsync(
        string? projectId,
        bool readOnlyWithoutProject,
        CancellationToken cancellationToken = default);

    Task OpenChatAsync(
        string projectId,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every saved chat for a project, newest first.
    /// </summary>
    /// <remarks>
    /// Sessions were already being written to disk one file each; nothing could read
    /// them back as a list, so a user's own history was invisible. Reads the directory
    /// rather than caching an index: the files are the source of truth, and a stale
    /// index would show chats that no longer exist.
    /// </remarks>
    Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(
        string? projectId,
        CancellationToken cancellationToken = default);

    Task SaveActiveSessionAsync(
        IReadOnlyList<ChatMessage> messages,
        string? title = null,
        CancellationToken cancellationToken = default);

    Task SetPinnedAsync(
        string projectId,
        bool pinned,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hides or restores a project without touching its history.
    /// </summary>
    /// <remarks>
    /// Archiving the active project clears the active session — an archived project has
    /// no business staying open in the composer the user is typing into.
    /// </remarks>
    Task SetArchivedAsync(
        string projectId,
        bool archived,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a display name that survives the folder being renamed on disk.</summary>
    Task RenameProjectAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default);

    Task RemoveProjectAsync(string projectId, CancellationToken cancellationToken = default);

    Task SetExecutionPolicyAsync(
        ExecutionPolicy policy,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON-backed project/session store. State lives under LocalAppData, never inside a
/// source repository, and every write is replaced atomically.
/// </summary>
public sealed class ProjectWorkspaceService(WorkspaceStatePaths paths) : IProjectWorkspaceService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkspaceIndex _index = new();

    public IReadOnlyList<ProjectRecord> Projects => _index.Projects
        .Where(project => !project.IsArchived)
        .OrderByDescending(project => project.IsPinned)
        .ThenByDescending(project => project.LastOpenedAt)
        .ToList();

    /// <summary>Archived projects, newest-opened first. Not part of <see cref="Projects"/>.</summary>
    public IReadOnlyList<ProjectRecord> ArchivedProjects => _index.Projects
        .Where(project => project.IsArchived)
        .OrderByDescending(project => project.LastOpenedAt)
        .ToList();

    public ProjectRecord? ActiveProject => _index.Projects.FirstOrDefault(project =>
        string.Equals(project.Id, _index.ActiveProjectId, StringComparison.Ordinal));

    public ChatSession? ActiveSession { get; private set; }

    public WorkspaceContext CurrentWorkspace
    {
        get
        {
            if (ActiveSession is null)
            {
                return WorkspaceContext.WithoutProject("no-session");
            }

            var policy = new ExecutionPolicy(
                ActiveSession.Sandbox,
                ActiveSession.Approval,
                ActiveSession.NetworkEnabled,
                ActiveSession.FullAccessConfirmed);

            return ActiveProject is { IsAvailable: true } project
                ? WorkspaceContext.ForProject(ActiveSession.Id, project.Id, project.RootPath, policy)
                : WorkspaceContext.WithoutProject(ActiveSession.Id);
        }
    }

    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _index = await ReadJsonAsync<WorkspaceIndex>(IndexPath, cancellationToken)
                .ConfigureAwait(false) ?? new WorkspaceIndex();

            // Re-canonicalize existing folders without hiding missing projects. The name
            // is re-derived only while the user has never set one explicitly, so a
            // rename survives even though the folder's own name never changed.
            foreach (var project in _index.Projects.Where(project => Directory.Exists(project.RootPath)))
            {
                project.RootPath = WorkspacePathGuard.CanonicalizeDirectory(project.RootPath);
                if (!project.HasCustomName)
                {
                    project.Name = new DirectoryInfo(project.RootPath).Name;
                }
            }

            if (!string.IsNullOrWhiteSpace(_index.ActiveProjectId) &&
                !string.IsNullOrWhiteSpace(_index.ActiveSessionId))
            {
                ActiveSession = await ReadSessionAsync(
                    _index.ActiveProjectId,
                    _index.ActiveSessionId,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(_index.ActiveSessionId))
            {
                ActiveSession = await ReadSessionAsync(
                    projectId: null,
                    _index.ActiveSessionId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ProjectRecord> AddOrActivateProjectAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var canonical = WorkspacePathGuard.CanonicalizeDirectory(rootPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _index.Projects.FirstOrDefault(project =>
                string.Equals(project.RootPath, canonical, StringComparison.OrdinalIgnoreCase));

            var project = existing ?? new ProjectRecord
            {
                Id = StableProjectId(canonical),
                Name = new DirectoryInfo(canonical).Name,
                RootPath = canonical,
            };

            if (existing is null)
            {
                _index.Projects.Add(project);
            }

            project.LastOpenedAt = DateTimeOffset.UtcNow;
            _index.ActiveProjectId = project.Id;
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
            return project;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task ActivateProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = RequireProject(projectId);
            if (!project.IsAvailable)
            {
                throw new DirectoryNotFoundException($"Project folder is unavailable: {project.RootPath}");
            }

            project.LastOpenedAt = DateTimeOffset.UtcNow;
            _index.ActiveProjectId = project.Id;
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<ChatSession> StartChatAsync(
        string? projectId,
        bool readOnlyWithoutProject,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) && !readOnlyWithoutProject)
        {
            throw new InvalidOperationException(
                "A writable chat must be bound to a project. Explicitly choose read-only without a project.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectRecord? project = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                project = RequireProject(projectId);
                if (!project.IsAvailable)
                {
                    throw new DirectoryNotFoundException($"Project folder is unavailable: {project.RootPath}");
                }
            }

            var session = new ChatSession
            {
                ProjectId = project?.Id,
                Sandbox = project is null ? SandboxMode.ReadOnly : SandboxMode.WorkspaceWrite,
                Approval = ApprovalPolicy.OnRequest,
                NetworkEnabled = false,
            };

            ActiveSession = session;
            _index.ActiveProjectId = project?.Id;
            _index.ActiveSessionId = session.Id;

            if (project is not null)
            {
                project.LastOpenedAt = DateTimeOffset.UtcNow;
                project.LastChatTitle = session.Title;
                project.RecentChats.Insert(0, new ChatSummary
                {
                    Id = session.Id,
                    Title = session.Title,
                    UpdatedAt = session.UpdatedAt,
                });
            }

            await WriteSessionAsync(session, cancellationToken).ConfigureAwait(false);
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task OpenChatAsync(
        string projectId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = RequireProject(projectId);
            var session = await ReadSessionAsync(project.Id, sessionId, cancellationToken)
                .ConfigureAwait(false) ?? throw new FileNotFoundException("Chat session was not found.");

            ActiveSession = session;
            _index.ActiveProjectId = project.Id;
            _index.ActiveSessionId = session.Id;
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SaveActiveSessionAsync(
        IReadOnlyList<ChatMessage> messages,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveSession is null)
            {
                throw new InvalidOperationException("There is no active chat session.");
            }

            ActiveSession.Messages = [.. messages];
            ActiveSession.UpdatedAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(title))
            {
                ActiveSession.Title = title.Trim();
            }

            if (ActiveProject is { } project)
            {
                project.LastChatTitle = ActiveSession.Title;
                var summary = project.RecentChats.FirstOrDefault(chat => chat.Id == ActiveSession.Id);
                if (summary is not null)
                {
                    summary.Title = ActiveSession.Title;
                    summary.UpdatedAt = ActiveSession.UpdatedAt;
                }
            }

            await WriteSessionAsync(ActiveSession, cancellationToken).ConfigureAwait(false);
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetPinnedAsync(
        string projectId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireProject(projectId).IsPinned = pinned;
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetArchivedAsync(
        string projectId,
        bool archived,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireProject(projectId).IsArchived = archived;

            // An archived project has no business staying open in the composer.
            if (archived && _index.ActiveProjectId == projectId)
            {
                _index.ActiveProjectId = null;
                _index.ActiveSessionId = null;
                ActiveSession = null;
            }

            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RenameProjectAsync(
        string projectId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A project name cannot be empty.", nameof(name));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = RequireProject(projectId);
            project.Name = trimmed;
            project.HasCustomName = true;
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RemoveProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = RequireProject(projectId);
            _index.Projects.Remove(project);

            if (_index.ActiveProjectId == projectId)
            {
                _index.ActiveProjectId = null;
                _index.ActiveSessionId = null;
                ActiveSession = null;
            }

            // Deliberately do not delete project.RootPath or session history here.
            await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SetExecutionPolicyAsync(
        ExecutionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveSession is null)
            {
                throw new InvalidOperationException("There is no active chat session.");
            }

            if (ActiveSession.ProjectId is null && policy.Sandbox != SandboxMode.ReadOnly)
            {
                throw new InvalidOperationException("A session without a project is always read-only.");
            }

            if (policy.Sandbox == SandboxMode.FullAccess && !policy.FullAccessConfirmed)
            {
                throw new InvalidOperationException("Full access requires explicit user confirmation.");
            }

            ActiveSession.Sandbox = policy.Sandbox;
            ActiveSession.Approval = policy.Approval;
            ActiveSession.NetworkEnabled = policy.NetworkEnabled;
            ActiveSession.FullAccessConfirmed = policy.FullAccessConfirmed;
            await WriteSessionAsync(ActiveSession, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<IReadOnlyList<ChatSessionSummary>> ListChatsAsync(
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(
            paths.StateDirectory,
            "projects",
            projectId ?? "no-project",
            "sessions");

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var summaries = new List<ChatSessionSummary>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = await ReadJsonAsync<ChatSession>(file, cancellationToken)
                .ConfigureAwait(false);

            // A file that will not parse is a corrupted session, not a fatal error.
            // Skipping it costs one row; throwing would cost the whole history.
            if (session is null)
            {
                continue;
            }

            summaries.Add(new ChatSessionSummary(
                session.Id,
                session.ProjectId,
                string.IsNullOrWhiteSpace(session.Title) ? "New chat" : session.Title,
                session.UpdatedAt,
                session.Messages.Count));
        }

        return [.. summaries.OrderByDescending(s => s.UpdatedAt)];
    }

    private ProjectRecord RequireProject(string id) => _index.Projects.FirstOrDefault(project =>
        string.Equals(project.Id, id, StringComparison.Ordinal)) ??
        throw new KeyNotFoundException($"Unknown project '{id}'.");

    private Task<ChatSession?> ReadSessionAsync(
        string? projectId,
        string sessionId,
        CancellationToken cancellationToken) =>
        ReadJsonAsync<ChatSession>(SessionPath(projectId, sessionId), cancellationToken);

    private Task WriteSessionAsync(ChatSession session, CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(SessionPath(session.ProjectId, session.Id), session, cancellationToken);

    private Task WriteIndexAsync(CancellationToken cancellationToken) =>
        WriteJsonAtomicAsync(IndexPath, _index, cancellationToken);

    private string IndexPath => Path.Combine(paths.StateDirectory, "projects.json");

    private string SessionPath(string? projectId, string sessionId) => Path.Combine(
        paths.StateDirectory,
        "projects",
        projectId ?? "no-project",
        "sessions",
        sessionId + ".json");

    private static string StableProjectId(string canonicalPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private static async Task<T?> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";

        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed class WorkspaceIndex
    {
        public List<ProjectRecord> Projects { get; set; } = [];

        public string? ActiveProjectId { get; set; }

        public string? ActiveSessionId { get; set; }
    }
}
