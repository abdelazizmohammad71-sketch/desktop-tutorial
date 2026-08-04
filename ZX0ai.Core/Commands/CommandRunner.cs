using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ZX0ai.Core.Commands;

/// <summary>Outcome of one command execution.</summary>
/// <param name="Command">The command line as issued.</param>
/// <param name="ExitCode">Process exit code; -1 when it never started.</param>
/// <param name="Output">Interleaved stdout and stderr.</param>
/// <param name="Duration">Wall-clock time.</param>
public sealed record CommandExecution(
    string Command,
    int ExitCode,
    string Output,
    TimeSpan Duration);

/// <summary>Executes shell commands, streaming output as it arrives.</summary>
public interface ICommandRunner
{
    /// <summary>Raised for each line of stdout or stderr.</summary>
    event EventHandler<CommandOutputLine>? OutputReceived;

    /// <summary>Raised when a command starts, so the UI can render a pending step.</summary>
    event EventHandler<string>? CommandStarted;

    Task<CommandExecution> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default);
}

/// <param name="Command">Which command produced this line.</param>
/// <param name="Text">The line, without its newline.</param>
/// <param name="IsError">True when it came from stderr.</param>
public sealed record CommandOutputLine(string Command, string Text, bool IsError);

/// <summary>
/// Runs one executable directly with an allow-list in front.
/// </summary>
/// <remarks>
/// The allow-list is a real safety boundary, not a formality: a model can write any
/// string into <c>run_command</c>. No platform shell is involved, command chaining
/// is rejected, and the child receives an allowlisted environment that never
/// contains provider credentials.
/// </remarks>
public sealed class CommandRunner(ILogger<CommandRunner> logger) : ICommandRunner
{
    /// <summary>Commands considered safe to run without further confirmation.</summary>
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "git", "dotnet", "npm", "npx", "node", "python", "pip", "cargo", "go",
        "ls", "dir", "cat", "type", "echo", "pwd", "cd", "find", "grep", "rg",
        "where", "which", "curl", "head", "tail", "wc", "sort", "tree", "code",
    };

    /// <summary>Characters that would let one command become several.</summary>
    private static readonly char[] ChainingCharacters = ['&', '|', ';', '`', '>', '<', '\n', '\r'];

    private static readonly string[] SafeEnvironmentVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "TEMP", "TMP",
        "USERPROFILE", "LOCALAPPDATA", "APPDATA", "ProgramFiles",
        "ProgramFiles(x86)", "DOTNET_ROOT", "NUGET_PACKAGES",
    ];

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public event EventHandler<CommandOutputLine>? OutputReceived;

    public event EventHandler<string>? CommandStarted;

    public async Task<CommandExecution> RunAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!IsAllowed(command, out var reason))
        {
            logger.LogWarning("Refused command {Command}: {Reason}", command, reason);
            return new CommandExecution(command, -1, reason, stopwatch.Elapsed);
        }

        if (!Directory.Exists(workingDirectory))
        {
            return new CommandExecution(
                command,
                -1,
                "The active project working directory is unavailable.",
                stopwatch.Elapsed);
        }

        if (!TryParseCommandLine(command, out var parts) || parts.Count == 0)
        {
            return new CommandExecution(command, -1, "Could not parse the command.", stopwatch.Elapsed);
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(parts[0]), "echo", StringComparison.OrdinalIgnoreCase))
        {
            var text = string.Join(' ', parts.Skip(1));
            CommandStarted?.Invoke(this, command);
            OutputReceived?.Invoke(this, new CommandOutputLine(command, text, false));
            return new CommandExecution(command, 0, text, stopwatch.Elapsed);
        }

        CommandStarted?.Invoke(this, command);

        var startInfo = BuildStartInfo(parts, workingDirectory);
        var output = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) => Capture(command, e.Data, false, output);
            process.ErrorDataReceived += (_, e) => Capture(command, e.Data, true, output);

            if (!process.Start())
            {
                return new CommandExecution(command, -1, "Could not start the process.", stopwatch.Elapsed);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillQuietly(process);
                return new CommandExecution(
                    command, -1, output + "\n[timed out after 2 minutes]", stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                KillQuietly(process);
                throw;
            }

            return new CommandExecution(
                command,
                process.ExitCode,
                output.ToString().TrimEnd(),
                stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError(ex, "Command {Command} failed to run.", command);
            return new CommandExecution(command, -1, ex.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>Checks a command line against the allow-list. Exposed for tests.</summary>
    public static bool IsAllowed(string command, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "Empty command.";
            return false;
        }

        if (command.IndexOfAny(ChainingCharacters) >= 0)
        {
            reason = "Refused: command chaining and redirection are not permitted.";
            return false;
        }

        if (!TryParseCommandLine(command, out var parts) || parts.Count == 0)
        {
            reason = "Refused: command line could not be parsed.";
            return false;
        }

        var verb = parts[0];

        // Strip any path so /usr/bin/git and git are treated the same.
        verb = Path.GetFileNameWithoutExtension(verb);

        if (!AllowedCommands.Contains(verb))
        {
            reason = $"Refused: '{verb}' is not on the allow-list.";
            return false;
        }

        return true;
    }

    private static ProcessStartInfo BuildStartInfo(
        IReadOnlyList<string> parts,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = parts[0],
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        for (var index = 1; index < parts.Count; index++)
        {
            startInfo.ArgumentList.Add(parts[index]);
        }

        startInfo.Environment.Clear();
        foreach (var name in SafeEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    private static bool TryParseCommandLine(string command, out List<string> parts)
    {
        parts = [];
        var token = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (token.Length > 0)
                {
                    parts.Add(token.ToString());
                    token.Clear();
                }

                continue;
            }

            token.Append(character);
        }

        if (quoted)
        {
            return false;
        }

        if (token.Length > 0)
        {
            parts.Add(token.ToString());
        }

        return true;
    }

    private void Capture(string command, string? line, bool isError, StringBuilder sink)
    {
        if (line is null)
        {
            return;
        }

        sink.AppendLine(line);
        OutputReceived?.Invoke(this, new CommandOutputLine(command, line, isError));
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // The process already exited on its own.
        }
    }
}
