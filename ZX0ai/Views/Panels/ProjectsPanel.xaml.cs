using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;
using ZX0ai.Core.Projects;
using ZX0ai.Services;
using ZX0ai.ViewModels;

namespace ZX0ai.Views.Panels;

/// <summary>
/// Every project the agent has been pointed at: pinned, working, and archived.
/// </summary>
/// <remarks>
/// Opening a project here does two things at once: it activates the record in
/// <see cref="IProjectWorkspaceService"/> (so the rail's ordering and recency follow it),
/// and it binds <see cref="AgentWorkspace"/> to the same folder (so tools, the terminal
/// and the Files panel immediately point at it). Those are two different systems that
/// happen to need the same folder, and this panel is what keeps them in step.
/// </remarks>
public sealed partial class ProjectsPanel : UserControl
{
    private readonly IProjectWorkspaceService _projects;
    private readonly AgentWorkspace _workspace;
    private readonly FolderPickerService _picker;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private string _filter = string.Empty;
    private bool _archivedExpanded;

    public ProjectsPanel()
    {
        InitializeComponent();

        _projects = App.GetService<IProjectWorkspaceService>();
        _workspace = App.GetService<AgentWorkspace>();
        _picker = App.GetService<FolderPickerService>();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _projects.Changed += OnProjectsChanged;
        Unloaded += (_, _) => _projects.Changed -= OnProjectsChanged;

        Loaded += (_, _) => Reload();
    }

    /// <summary>Raised once a project has been activated and the workspace bound to it.</summary>
    public event EventHandler<string>? ProjectOpened;

    /// <summary>
    /// Filters the list. The rail has one search box for both chats and projects rather
    /// than a second field repeating the same job a few pixels below the first.
    /// </summary>
    public void ApplyFilter(string text)
    {
        _filter = text.Trim();
        Reload();
    }

    /// <summary>
    /// Reacts to <see cref="IProjectWorkspaceService.Changed"/>.
    /// </summary>
    /// <remarks>
    /// Not guaranteed to arrive on the UI thread. Core's async methods correctly use
    /// <c>ConfigureAwait(false)</c> throughout — Core has no business forcing a UI-thread
    /// continuation — so the event after any <c>await</c> inside them can fire from a
    /// thread-pool thread. <see cref="Reload"/> sets <c>ItemsSource</c> on WinUI
    /// controls, which throws <c>RPC_E_WRONG_THREAD</c> off the UI thread, so every path
    /// here is marshalled through the dispatcher captured at construction.
    /// </remarks>
    private void OnProjectsChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _dispatcher.TryEnqueue(Reload);
    }

    // ================================ Add ================================

    private async void OnAddProjectClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (await _picker.PickAsync().ConfigureAwait(true) is not { } path)
        {
            return;
        }

        try
        {
            var project = await _projects.AddOrActivateProjectAsync(path).ConfigureAwait(true);
            _workspace.Bind(project.RootPath);
            HideNotice();
            ProjectOpened?.Invoke(this, project.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    // ============================== Rows =================================

    private async void OnProjectRowClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        var record = Find(id);
        if (record is null)
        {
            return;
        }

        try
        {
            var activated = await _projects.AddOrActivateProjectAsync(record.RootPath).ConfigureAwait(true);

            // Opening an archived project is how you use it again; leaving it archived
            // while also active would be a state nothing else in the panel can show.
            if (record.IsArchived)
            {
                await _projects.SetArchivedAsync(activated.Id, archived: false).ConfigureAwait(true);
            }

            _workspace.Bind(activated.RootPath);
            HideNotice();
            ProjectOpened?.Invoke(this, activated.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            ShowNotice($"Could not open '{record.Name}': {ex.Message}", isError: true);
            Reload();
        }
    }

    private void OnProjectMenuClick(object sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Button { Tag: string id } anchor)
        {
            return;
        }

        var record = Find(id);
        if (record is null)
        {
            return;
        }

        var flyout = new MenuFlyout { Placement = FlyoutPlacementMode.Left };

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Rename",
            Command = new RelayCommand(() => _ = RenameAsync(record)),
        });

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = record.IsPinned ? "Unpin" : "Pin",
            Command = new RelayCommand(() => _ = _projects.SetPinnedAsync(record.Id, !record.IsPinned)),
        });

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Open in Explorer",
            IsEnabled = record.IsAvailable,
            Command = new RelayCommand(() => _ = Launcher.LaunchFolderPathAsync(record.RootPath).AsTask()),
        });

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = record.IsArchived ? "Unarchive" : "Archive",
            Command = new RelayCommand(() =>
                _ = _projects.SetArchivedAsync(record.Id, !record.IsArchived)),
        });

        flyout.Items.Add(new MenuFlyoutItem
        {
            Text = "Remove",
            Command = new RelayCommand(() => _ = _projects.RemoveProjectAsync(record.Id)),
        });

        flyout.ShowAt(anchor);
    }

    /// <summary>Renames a project via a small modal — inline row-editing is not worth the state.</summary>
    private async Task RenameAsync(ProjectRecord record)
    {
        var box = new TextBox { Text = record.Name, SelectionStart = record.Name.Length };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename project",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _projects.RenameProjectAsync(record.Id, box.Text).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    private void OnToggleArchivedClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        _archivedExpanded = !_archivedExpanded;
        ApplyArchivedState();
    }

    // ============================== Rendering =============================

    private void Reload()
    {
        var query = _filter;
        bool Matches(ProjectRecord project) =>
            query.Length == 0 ||
            project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            project.RootPath.Contains(query, StringComparison.OrdinalIgnoreCase);

        var activeId = _projects.ActiveProject?.Id;
        var activeSessionId = _projects.ActiveSession?.Id;
        var pinned = _projects.Projects.Where(p => p.IsPinned && Matches(p))
            .Select(p => new ProjectRow(p, p.Id == activeId, activeSessionId)).ToList();
        var working = _projects.Projects.Where(p => !p.IsPinned && Matches(p))
            .Select(p => new ProjectRow(p, p.Id == activeId, activeSessionId)).ToList();
        var archived = _projects.ArchivedProjects.Where(Matches)
            .Select(p => new ProjectRow(p, isActive: false, activeSessionId)).ToList();

        PinnedList.ItemsSource = pinned;
        PinnedSection.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ProjectList.ItemsSource = working;
        EmptyNote.Visibility = working.Count == 0 && pinned.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyNote.Text = _filter.Length > 0 ? "Nothing matches." : "No projects yet.";

        ArchivedList.ItemsSource = archived;
        ArchivedSection.Visibility = archived.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchivedToggleLabel.Text = $"Archived ({archived.Count})";

        ApplyArchivedState();
    }

    private void ApplyArchivedState()
    {
        ArchivedList.Visibility = _archivedExpanded ? Visibility.Visible : Visibility.Collapsed;

        // A right-pointing chevron, rotated open. A rotation transform reuses one glyph
        // for both states instead of swapping geometry for a ninety-degree turn.
        ArchivedChevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        ArchivedChevron.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform
        {
            Angle = _archivedExpanded ? 90 : 0,
        };
    }

    private ProjectRecord? Find(string id) =>
        _projects.Projects.FirstOrDefault(p => p.Id == id) ??
        _projects.ArchivedProjects.FirstOrDefault(p => p.Id == id);

    private void ShowNotice(string text, bool isError)
    {
        _ = isError;
        NoticeText.Text = text;
        Notice.Visibility = Visibility.Visible;
    }

    private void HideNotice() => Notice.Visibility = Visibility.Collapsed;
}
