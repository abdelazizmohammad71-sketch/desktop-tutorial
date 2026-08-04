using System.Text;
using System.Text.Json;
using ZX0ai.Core.Commands;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Skills;

/// <summary>Fetches a URL and returns its readable text.</summary>
public sealed class FetchUrlSkill(HttpClient httpClient) : ISkill
{
    /// <summary>Long pages are truncated; a whole site would blow the context window.</summary>
    private const int MaxCharacters = 12000;

    public string Name => "fetch_url";

    public string Description =>
        "Fetch a web page or API endpoint over HTTP(S) and return its text content.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("url", "string", "Absolute http or https URL to fetch.", true));

    public async Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Workspace.Policy.CanUseNetwork)
        {
            return SkillResult.Fail("Network access is disabled for this workspace session.");
        }

        var url = arguments.GetString("url");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return SkillResult.Fail("Provide an absolute http or https URL.");
        }

        try
        {
            using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SkillResult.Fail($"{uri.Host} returned {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = HtmlText.Extract(body);

            if (text.Length > MaxCharacters)
            {
                text = text[..MaxCharacters] + "\n\n[truncated]";
            }

            return SkillResult.Ok(text, $"Fetched {uri.Host} ({text.Length} chars)");
        }
        catch (HttpRequestException ex)
        {
            return SkillResult.Fail($"Could not reach {uri.Host}: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return SkillResult.Fail($"{uri.Host} timed out.");
        }
    }
}

/// <summary>Web search via DuckDuckGo's HTML endpoint, which needs no API key.</summary>
public sealed class WebSearchSkill(HttpClient httpClient) : ISkill
{
    public string Name => "web_search";

    public string Description =>
        "Search the web and return the top result titles, URLs and snippets.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("query", "string", "What to search for.", true),
        ("limit", "integer", "How many results to return, 1-10. Defaults to 5.", false));

    public async Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Workspace.Policy.CanUseNetwork)
        {
            return SkillResult.Fail("Network access is disabled for this workspace session.");
        }

        var query = arguments.GetString("query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return SkillResult.Fail("Provide a search query.");
        }

        var limit = Math.Clamp(arguments.GetInt("limit") ?? 5, 1, 10);
        var url = $"https://duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // The HTML endpoint returns an empty shell without a browser-like agent.
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ZX0ai/0.1");

            using var response = await httpClient
                .SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return SkillResult.Fail($"Search returned {(int)response.StatusCode}.");
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var results = HtmlText.ExtractSearchResults(html, limit);

            if (results.Count == 0)
            {
                return SkillResult.Ok("No results found.", $"Searched '{query}': nothing found");
            }

            var formatted = new StringBuilder();
            foreach (var (title, link, snippet) in results)
            {
                formatted.AppendLine($"- {title}");
                formatted.AppendLine($"  {link}");

                if (!string.IsNullOrWhiteSpace(snippet))
                {
                    formatted.AppendLine($"  {snippet}");
                }

                formatted.AppendLine();
            }

            return SkillResult.Ok(
                formatted.ToString().TrimEnd(),
                $"Searched '{query}': {results.Count} result(s)");
        }
        catch (HttpRequestException ex)
        {
            return SkillResult.Fail($"Search failed: {ex.Message}");
        }
    }
}

/// <summary>Reads a text file from under the working directory.</summary>
public sealed class ReadFileSkill : ISkill
{
    private const int MaxCharacters = 60000;

    public string Name => "read_file";

    public string Description => "Read a UTF-8 text file and return its contents.";

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("path", "string", "Path to the file, relative to the working directory.", true));

    public Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.Workspace.HasProject)
        {
            return Task.FromResult(SkillResult.Fail("Bind a project before reading files."));
        }

        if (!PathGuard.TryResolve(context.WorkingDirectory, arguments.GetString("path"), out var full, out var error))
        {
            return Task.FromResult(SkillResult.Fail(error));
        }

        if (!File.Exists(full))
        {
            return Task.FromResult(SkillResult.Fail($"No such file: {arguments.GetString("path")}"));
        }

        try
        {
            var text = File.ReadAllText(full);
            var truncated = text.Length > MaxCharacters;

            if (truncated)
            {
                text = text[..MaxCharacters] + "\n\n[truncated]";
            }

            return Task.FromResult(SkillResult.Ok(
                text,
                $"Read {Path.GetFileName(full)} ({text.Length} chars)"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(SkillResult.Fail($"Could not read the file: {ex.Message}"));
        }
    }
}

/// <summary>Writes a text file. Destructive: needs leader approval.</summary>
public sealed class WriteFileSkill : ISkill
{
    public string Name => "write_file";

    public string Description => "Create or overwrite a UTF-8 text file.";

    public bool IsDestructive => true;

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("path", "string", "Path to write, relative to the working directory.", true),
        ("content", "string", "Full file contents.", true));

    public Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (!context.Workspace.HasProject || !context.Workspace.Policy.CanWriteFiles)
        {
            return Task.FromResult(SkillResult.Fail(
                "File writes are blocked by the active workspace policy."));
        }

        if (!PathGuard.TryResolve(context.WorkingDirectory, arguments.GetString("path"), out var full, out var error))
        {
            return Task.FromResult(SkillResult.Fail(error));
        }

        var content = arguments.GetString("content");
        if (content is null)
        {
            return Task.FromResult(SkillResult.Fail("Provide the file content."));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var existed = File.Exists(full);
            File.WriteAllText(full, content, Encoding.UTF8);

            return Task.FromResult(SkillResult.Ok(
                $"Wrote {content.Length} characters.",
                $"{(existed ? "Updated" : "Created")} {Path.GetFileName(full)}"));
        }
        catch (IOException ex)
        {
            return Task.FromResult(SkillResult.Fail($"Could not write the file: {ex.Message}"));
        }
    }
}

/// <summary>Runs a shell command. Destructive: needs leader approval and an allow-list hit.</summary>
public sealed class RunCommandSkill(
    ICommandRunner runner,
    ICommandPolicy? commandPolicy = null,
    IActionApprovalService? approvals = null) : ISkill
{
    public string Name => "run_command";

    public string Description =>
        "Run a shell command in the working directory and return its output.";

    public bool IsDestructive => true;

    public JsonElement InputSchema { get; } = SchemaBuilder.Object(
        ("command", "string", "The command line to run.", true));

    public async Task<SkillResult> ExecuteAsync(
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        var command = arguments.GetString("command");
        if (string.IsNullOrWhiteSpace(command))
        {
            return SkillResult.Fail("Provide a command to run.");
        }

        var policy = (commandPolicy ?? new CommandPolicy()).Evaluate(command, context.Workspace);
        if (policy.Decision == CommandPolicyDecision.Block)
        {
            return SkillResult.Fail(policy.Reason);
        }

        if (policy.Decision == CommandPolicyDecision.Prompt)
        {
            var approved = await (approvals ?? new DenyActionApprovalService())
                .RequestAsync(new ActionApprovalRequest(
                    "Approve terminal command?",
                    command,
                    context.Agent.Id,
                    context.Workspace.SessionId), cancellationToken)
                .ConfigureAwait(false);

            if (!approved)
            {
                return SkillResult.Fail("The command was not approved by the user.");
            }
        }

        var execution = await runner
            .RunAsync(command, context.WorkingDirectory, cancellationToken)
            .ConfigureAwait(false);

        var output = string.IsNullOrWhiteSpace(execution.Output)
            ? "(no output)"
            : execution.Output;

        return execution.ExitCode == 0
            ? SkillResult.Ok(output, $"Ran `{command}`")
            : new SkillResult(false, $"Exit code {execution.ExitCode}\n{output}", $"`{command}` failed");
    }
}

/// <summary>Keeps file skills inside the working directory.</summary>
internal static class PathGuard
{
    /// <summary>
    /// Resolves a relative path and refuses anything that escapes the root. Without
    /// this, "../../.." in a model-authored path would reach the whole disk.
    /// </summary>
    public static bool TryResolve(string root, string? path, out string full, out string error)
    {
        full = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Provide a file path.";
            return false;
        }

        try
        {
            return WorkspacePathGuard.TryResolveRelative(root, path, out full, out error);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "That path is not valid.";
            return false;
        }
    }
}
