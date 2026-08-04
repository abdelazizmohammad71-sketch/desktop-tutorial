using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using ZX0ai.Views;

namespace ZX0ai;

/// <summary>
/// Host window: extends the content into the title bar and mounts the shell.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Title = "ZX0ai";
        AppWindow.SetIcon("Assets/zx0ai.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 940));
        CenterOnScreen();

        // Both need the window: the picker needs its handle, the approval dialog needs
        // a XamlRoot. Neither can be resolved before the shell is in the tree.
        App.GetService<Services.FolderPickerService>().Attach(this);

        var shell = new ShellPage();
        shell.Loaded += (_, _) => App.GetService<Services.ApprovalDialogService>().Attach(shell);

        MountShell(shell);
    }

    /// <summary>
    /// Mounts the shell and hands it the title-bar drag region.
    /// </summary>
    /// <remarks>
    /// The shell's own top bar is the title bar. The system caption buttons are drawn
    /// over it, so the shell has to be told how much room they take — and told again on
    /// every resize, because the insets change with DPI.
    /// </remarks>
    private void MountShell(ShellPage shell)
    {
        RootHost.Children.Add(shell);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(shell.DragRegion);

        ApplyCaptionColours(shell.IsDark);
        shell.ThemeChanged += (_, isDark) => ApplyCaptionColours(isDark);

        shell.Loaded += (_, _) => ApplyCaptionInsets(shell);
        SizeChanged += (_, _) => ApplyCaptionInsets(shell);
    }

    /// <summary>
    /// Repaints the system caption buttons to match the shell.
    /// </summary>
    /// <remarks>
    /// The caption buttons are the one part of the window the shell's element theme does
    /// not reach: they belong to the frame, not to the XAML tree, so a flip that repaints
    /// everything else would otherwise leave a light minimise glyph on a dark header.
    /// The backgrounds stay transparent so the header's own fill shows through.
    /// </remarks>
    private void ApplyCaptionColours(bool isDark)
    {
        var titleBar = AppWindow.TitleBar;

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        titleBar.ButtonForegroundColor = isDark
            ? Color.FromArgb(255, 243, 243, 245)
            : Color.FromArgb(255, 23, 23, 26);
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(255, 108, 108, 117)
            : Color.FromArgb(255, 151, 151, 159);

        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(30, 255, 255, 255)
            : Color.FromArgb(20, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonForegroundColor;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(50, 255, 255, 255)
            : Color.FromArgb(35, 0, 0, 0);
        titleBar.ButtonPressedForegroundColor = titleBar.ButtonForegroundColor;
    }

    private void ApplyCaptionInsets(ShellPage shell)
    {
        var titleBar = AppWindow.TitleBar;

        // Insets are reported in physical pixels; the layout works in DIPs.
        var scale = shell.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        shell.SetCaptionInsets(titleBar.LeftInset / scale, titleBar.RightInset / scale);
    }

    /// <summary>Places the window in the centre of its display.</summary>
    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var size = AppWindow.Size;

        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + ((work.Width - size.Width) / 2),
            work.Y + ((work.Height - size.Height) / 2)));
    }
}
