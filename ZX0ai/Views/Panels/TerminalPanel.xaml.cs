using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using ZX0ai.Services;

namespace ZX0ai.Views.Panels;

/// <summary>
/// The terminal panel: the agent's commands and the user's, in one scrollback.
/// </summary>
/// <remarks>
/// The session outlives the panel — it is a singleton that has been recording since
/// startup — so opening the panel shows what already happened rather than starting a
/// fresh, empty shell.
/// </remarks>
public sealed partial class TerminalPanel : UserControl
{
    private readonly TerminalSession _session;

    /// <summary>Cursor into <see cref="TerminalSession.History"/> for up/down recall.</summary>
    private int _historyCursor = -1;

    public TerminalPanel()
    {
        InitializeComponent();

        _session = App.GetService<TerminalSession>();
        OutputList.ItemsSource = _session.Lines;

        _session.Changed += OnSessionChanged;
        Unloaded += (_, _) => _session.Changed -= OnSessionChanged;
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        StopButton.IsEnabled = _session.IsRunning;

        // Follow the tail. A terminal that does not scroll is a log file.
        Scroller.UpdateLayout();
        Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true);
    }

    private async void OnCommandKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _ = sender;

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                var command = CommandBox.Text;
                CommandBox.Text = string.Empty;
                _historyCursor = -1;
                await _session.RunAsync(command).ConfigureAwait(true);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                Recall(-1);
                break;

            case VirtualKey.Down:
                e.Handled = true;
                Recall(1);
                break;

            default:
                break;
        }
    }

    /// <summary>Walks the command history. Past the newest entry the box clears.</summary>
    private void Recall(int direction)
    {
        var history = _session.History;
        if (history.Count == 0)
        {
            return;
        }

        _historyCursor = _historyCursor < 0
            ? history.Count - 1
            : Math.Clamp(_historyCursor + direction, 0, history.Count);

        if (_historyCursor >= history.Count)
        {
            CommandBox.Text = string.Empty;
            return;
        }

        CommandBox.Text = history[_historyCursor];
        CommandBox.SelectionStart = CommandBox.Text.Length;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _session.Stop();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _session.Clear();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(_session.AsText());
        Clipboard.SetContent(package);
    }
}
