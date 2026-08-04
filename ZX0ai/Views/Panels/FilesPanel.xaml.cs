using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using ZX0ai.Services;

namespace ZX0ai.Views.Panels;

/// <summary>
/// The bound folder, as a tree.
/// </summary>
/// <remarks>
/// <para>
/// Children are read when a node is expanded, not up front. A project folder can hold
/// tens of thousands of files, and walking all of them to draw a tree the user has not
/// scrolled to is the difference between a panel that opens instantly and one that
/// hangs on a real repository.
/// </para>
/// <para>
/// Build output is skipped by name, for the same reason it is skipped in
/// <c>list_files</c>: <c>node_modules</c> is not the project, and it is most of the
/// entries.
/// </para>
/// </remarks>
public sealed partial class FilesPanel : UserControl
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", ".idea", "dist", "build",
        "__pycache__", ".venv", "venv", "target", "packages", ".next",
    };

    private readonly AgentWorkspace _workspace;
    private string _filter = string.Empty;

    public FilesPanel()
    {
        InitializeComponent();

        _workspace = App.GetService<AgentWorkspace>();
        _workspace.Changed += OnWorkspaceChanged;
        Unloaded += (_, _) => _workspace.Changed -= OnWorkspaceChanged;

        Loaded += (_, _) => Reload();
    }

    /// <summary>Raised when the user opens a file, so the shell can show it.</summary>
    public event EventHandler<string>? FileOpened;

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Reload();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Reload();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _ = sender;
        _ = e;

        _filter = FilterBox.Text.Trim();
        Reload();
    }

    private async void OnRevealClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        if (_workspace.Root is { } root && Directory.Exists(root))
        {
            await Launcher.LaunchFolderPathAsync(root);
        }
    }

    private void Reload()
    {
        Tree.RootNodes.Clear();

        var root = _workspace.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            EmptyNote.Text = "No folder is bound.";
            EmptyNote.Visibility = Visibility.Visible;
            return;
        }

        var node = new TreeViewNode
        {
            Content = new FileEntry(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), root, true),
            IsExpanded = true,
        };

        Populate(node);
        Tree.RootNodes.Add(node);

        EmptyNote.Text = node.Children.Count == 0
            ? _filter.Length > 0 ? "Nothing matches." : "This folder is empty."
            : string.Empty;
        EmptyNote.Visibility = node.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills one level, and stubs each subdirectory so it shows an expander.
    /// </summary>
    /// <remarks>
    /// <c>HasUnrealizedChildren</c> is what makes the tree lazy: the node claims children
    /// it has not read, and <see cref="OnExpanding"/> reads them the moment the user asks.
    /// </remarks>
    private void Populate(TreeViewNode node)
    {
        node.Children.Clear();

        if (node.Content is not FileEntry entry || !entry.IsDirectory)
        {
            return;
        }

        try
        {
            foreach (var directory in Directory
                .EnumerateDirectories(entry.Path)
                .Where(path => !Skip.Contains(Path.GetFileName(path)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                // A filter hides files, never folders: hiding the folder would hide the
                // matching files inside it.
                var child = new TreeViewNode
                {
                    Content = new FileEntry(Path.GetFileName(directory), directory, true),
                    HasUnrealizedChildren = true,
                };
                node.Children.Add(child);
            }

            foreach (var file in Directory
                .EnumerateFiles(entry.Path)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (_filter.Length > 0 &&
                    !name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                node.Children.Add(new TreeViewNode
                {
                    Content = new FileEntry(name, file, false),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder is a row that does not expand, not a crash.
        }

        node.HasUnrealizedChildren = false;
    }

    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        _ = sender;

        if (args.InvokedItem is not TreeViewNode node || node.Content is not FileEntry entry)
        {
            return;
        }

        if (!entry.IsDirectory)
        {
            FileOpened?.Invoke(this, entry.Path);
            return;
        }

        if (node.HasUnrealizedChildren)
        {
            Populate(node);
        }

        node.IsExpanded = !node.IsExpanded;
    }

    /// <summary>One row. <c>ToString</c> is what the default template renders.</summary>
    private sealed record FileEntry(string Name, string Path, bool IsDirectory)
    {
        public override string ToString() => IsDirectory ? Name + "/" : Name;
    }
}
