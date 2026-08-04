using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using ZX0ai.Core.Security;

namespace ZX0ai.Services;

/// <summary>
/// Lets the user choose the folder the agent may work in.
/// </summary>
/// <remarks>
/// The picker needs the window handle. In an unpackaged app there is no implicit
/// activation context, so <c>InitializeWithWindow</c> is not optional — without it the
/// picker throws instead of opening.
/// </remarks>
public sealed class FolderPickerService
{
    private nint _windowHandle;

    public void Attach(Window window) => _windowHandle = WindowNative.GetWindowHandle(window);

    /// <summary>The chosen folder path, or null if the user cancelled.</summary>
    public async Task<string?> PickAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");

        InitializeWithWindow.Initialize(picker, _windowHandle);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}

/// <summary>
/// Asks the user before a command the policy considers dangerous is run.
/// </summary>
/// <remarks>
/// <para>
/// This is the gate no mode removes. Full access widens which commands may run without
/// asking; it never removes the confirmation for the ones on the dangerous list, because
/// the whole point of that list is that the model cannot be the last word on them.
/// </para>
/// <para>
/// Fails closed. If no window is attached, or the dialog cannot be shown for any reason,
/// the answer is no — an approval prompt that silently resolves to "yes" when the UI is
/// unavailable would be worse than having no prompt at all.
/// </para>
/// </remarks>
public sealed class ApprovalDialogService : IActionApprovalService
{
    private XamlRoot? _root;

    public void Attach(FrameworkElement element) => _root = element.XamlRoot;

    public async Task<bool> RequestAsync(
        ActionApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (_root is null)
        {
            return false;
        }

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = "The agent wants to run this command in your workspace.",
            TextWrapping = TextWrapping.Wrap,
        });

        body.Children.Add(new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemControlBackgroundListLowBrush"],
            Child = new TextBlock
            {
                Text = request.Detail,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFontFamily"],
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        var dialog = new ContentDialog
        {
            XamlRoot = _root,
            Title = request.Title,
            Content = body,
            PrimaryButtonText = "Run it",
            CloseButtonText = "Refuse",

            // The safe choice is the default, so return or escape both decline.
            DefaultButton = ContentDialogButton.Close,
        };

        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                        System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open, or the tree is gone. Refuse.
            return false;
        }
    }
}
