using Microsoft.UI.Xaml;
using ZX0ai.Core.Sessions;
using ZX0ai.Services;

namespace ZX0ai.ViewModels;

/// <summary>One conversation as the history rail needs it.</summary>
/// <remarks>
/// <para>
/// A projection rather than the <see cref="ChatSession"/> itself. The rail needs a
/// formatted age and a selected state, and neither belongs on a persisted record — the
/// age is relative to now, and which row is open is a property of the window, not of the
/// file on disk.
/// </para>
/// <para>
/// The selected state is a <see cref="Visibility"/> because the theme is flipped on the
/// shell root: a brush resolved in C# would come from the application theme and stop
/// following the flip. Handing the template a visibility keeps every colour in XAML.
/// </para>
/// </remarks>
public sealed class HistoryRow(ChatSession session, bool isActive)
{
    public string Id { get; } = session.Id;

    public string Title { get; } = session.Title;

    /// <summary>Compact relative age: <c>now</c>, <c>4m</c>, <c>3h</c>, <c>2d</c>, <c>5w</c>.</summary>
    public string Age { get; } = SessionStore.AgeOf(session.UpdatedAt);

    public Visibility SelectionVisibility { get; } =
        isActive ? Visibility.Visible : Visibility.Collapsed;
}
