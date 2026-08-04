using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using ZX0ai.Core.Commands;

namespace ZX0ai.Services;

/// <summary>One line of terminal output.</summary>
public sealed class TerminalLine(string text, TerminalLineKind kind)
{
    public string Text { get; } = text;

    public TerminalLineKind Kind { get; } = kind;
}

public enum TerminalLineKind
{
    /// <summary>The command that was issued, echoed back.</summary>
    Command,
    Output,
    Error,

    /// <summary>A note from the app itself — a refusal, an exit code.</summary>
    Notice,
}

/// <summary>
/// The terminal: everything run in the workspace, by the agent or by the user.
/// </summary>
/// <remarks>
/// <para>
/// One session for both, deliberately. The agent already runs commands, and until now
/// their output existed only in a tool result the user never saw. Showing the agent's
/// commands and the user's in the same scrollback is what makes the panel a record of
/// what happened to the folder, rather than a second, parallel shell.
/// </para>
/// <para>
/// Output arrives on a background thread from the process reader, so every mutation is
/// marshalled to the UI thread. An <c>ObservableCollection</c> updated off-thread throws
/// somewhere else entirely, which is a miserable thing to diagnose.
/// </para>
/// </remarks>
public sealed class TerminalSession
{
    /// <summary>Scrollback cap. Past this the panel costs more than the history is worth.</summary>
    private const int MaxLines = 2000;

    private readonly ICommandRunner _runner;
    private readonly AgentWorkspace _workspace;
    private readonly ILogger<TerminalSession> _logger;
    private readonly DispatcherQueue _dispatcher;

    private CancellationTokenSource? _running;

    public TerminalSession(
        ICommandRunner runner,
        AgentWorkspace workspace,
        ILogger<TerminalSession> logger)
    {
        _runner = runner;
        _workspace = workspace;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Subscribed once, for the life of the app: this is how the agent's commands
        // reach the panel without the agent knowing the panel exists.
        _runner.CommandStarted += (_, command) => Append(command, TerminalLineKind.Command);
        _runner.OutputReceived += (_, line) =>
            Append(line.Text, line.IsError ? TerminalLineKind.Error : TerminalLineKind.Output);
    }

    public ObservableCollection<TerminalLine> Lines { get; } = [];

    /// <summary>Commands the user typed, newest last, for up-arrow recall.</summary>
    public List<string> History { get; } = [];

    public bool IsRunning => _running is { IsCancellationRequested: false } && _busy;

    private bool _busy;

    /// <summary>Raised whenever lines or run state change, so the panel can react.</summary>
    public event EventHandler? Changed;

    /// <summary>Runs a command the user typed.</summary>
    public async Task RunAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || _busy)
        {
            return;
        }

        if (!_workspace.IsBound)
        {
            Append("No folder is bound. Choose one from the title bar first.", TerminalLineKind.Notice);
            return;
        }

        History.Add(command);

        _busy = true;
        _running?.Dispose();
        _running = new CancellationTokenSource();
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            // The runner echoes the command and streams output through the events
            // subscribed in the constructor, so nothing is appended here.
            var execution = await _runner
                .RunAsync(command, _workspace.Root!, _running.Token)
                .ConfigureAwait(true);

            if (execution.ExitCode != 0)
            {
                Append($"exit {execution.ExitCode}", TerminalLineKind.Notice);
            }
        }
        catch (OperationCanceledException)
        {
            Append("stopped", TerminalLineKind.Notice);
        }
        catch (Exception ex) when (ex is IOException or
                                        UnauthorizedAccessException or
                                        InvalidOperationException)
        {
            // The command broker refuses anything off its allow-list. That is a normal
            // outcome for a typed command, so it is reported rather than thrown.
            _logger.LogInformation(ex, "Command refused or failed: {Command}", command);
            Append(ex.Message, TerminalLineKind.Error);
        }
        finally
        {
            _busy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop() => _running?.Cancel();

    public void Clear()
    {
        Lines.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The whole scrollback as text, for the copy button.</summary>
    public string AsText() => string.Join(Environment.NewLine, Lines.Select(line => line.Text));

    private void Append(string text, TerminalLineKind kind)
    {
        if (_dispatcher is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            Lines.Add(new TerminalLine(text, kind));

            // Trim from the front so the newest output is always what survives.
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
