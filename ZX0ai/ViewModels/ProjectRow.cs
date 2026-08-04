using Microsoft.UI.Xaml;
using ZX0ai.Core.Projects;

namespace ZX0ai.ViewModels;

/// <summary>One recent chat under a project row.</summary>
public sealed class ProjectChatRow(string title, bool isActive)
{
    public string Title { get; } = title;

    public Visibility ActiveVisibility { get; } =
        isActive ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>One project as the rail's Projects section needs it.</summary>
/// <remarks>
/// <para>
/// A projection over <see cref="ProjectRecord"/> for the same reason
/// <see cref="HistoryRow"/> exists: the row needs a selected state and a small, already
/// sliced list of recent chats, and neither belongs on the persisted record.
/// </para>
/// <para>
/// Colour stays out of this class entirely — every visible state here is a
/// <see cref="Visibility"/>, resolved against <c>ThemeResource</c> brushes in the
/// template. A brush chosen in code would come from the application theme, which never
/// flips, and stop following the shell's own flip the moment the user switches theme.
/// </para>
/// </remarks>
public sealed class ProjectRow(ProjectRecord project, bool isActive, string? activeSessionId)
{
    /// <summary>Recent chats shown under the project name. Past this it is just a list of titles.</summary>
    private const int MaxRecentChats = 2;

    public string Id { get; } = project.Id;

    public string Name { get; } = project.Name;

    public string RootPath { get; } = project.RootPath;

    public bool IsAvailable { get; } = project.IsAvailable;

    public bool IsPinned { get; } = project.IsPinned;

    public bool IsArchived { get; } = project.IsArchived;

    public IReadOnlyList<ProjectChatRow> RecentChats { get; } = [.. project.RecentChats
        .OrderByDescending(chat => chat.UpdatedAt)
        .Take(MaxRecentChats)
        .Select(chat => new ProjectChatRow(chat.Title, chat.Id == activeSessionId))];

    // Exactly one of these three is ever visible. A missing folder is the most urgent
    // fact about a project, so it pre-empts the chat summary rather than sitting beside
    // it — a "No chats" line under "Folder not found" would just be noise, since chat
    // history is moot for a folder the panel cannot even confirm still exists.
    public Visibility HasChatsVisibility { get; } =
        project.IsAvailable && project.RecentChats.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoChatsVisibility { get; } =
        project.IsAvailable && project.RecentChats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MissingVisibility { get; } =
        project.IsAvailable ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SelectionVisibility { get; } =
        isActive ? Visibility.Visible : Visibility.Collapsed;
}
