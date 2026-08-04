namespace ZX0ai.Core.Projects;

/// <summary>A real local project known to ZX0ai. Removing it never deletes the folder.</summary>
public sealed class ProjectRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// True once the user has renamed the project explicitly.
    /// </summary>
    /// <remarks>
    /// Startup re-derives <see cref="Name"/> from the folder for every project that has
    /// never been renamed, so a project picked up before it had a proper name still gets
    /// one if the folder is renamed on disk. Once a user names it themselves that
    /// derivation has to stop, or the rename would silently revert on the next launch.
    /// </remarks>
    public bool HasCustomName { get; set; }

    public string RootPath { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    /// <summary>
    /// Hidden from the working list without losing its history.
    /// </summary>
    /// <remarks>
    /// Archiving and removing are different operations on purpose. Removing forgets a
    /// project outright; archiving is for a project that is finished or paused but whose
    /// chat history is still worth keeping around to search or reopen later.
    /// </remarks>
    public bool IsArchived { get; set; }

    public DateTimeOffset LastOpenedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? LastChatTitle { get; set; }

    public List<ChatSummary> RecentChats { get; set; } = [];

    public bool IsAvailable => Directory.Exists(RootPath);
}

public sealed class ChatSummary
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = "New chat";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
