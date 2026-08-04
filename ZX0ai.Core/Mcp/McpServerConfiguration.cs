using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZX0ai.Core.Security;

namespace ZX0ai.Core.Mcp;

public enum McpTransportKind
{
    Stdio,
    StreamableHttp,
}

public enum McpConfigurationOrigin
{
    User,
    Project,
}

/// <summary>
/// Declarative MCP server configuration. Secret values are never accepted;
/// environment and header maps contain source environment-variable names only.
/// </summary>
public sealed record McpServerConfiguration
{
    public required string Name { get; init; }

    public bool Enabled { get; init; }

    public McpTransportKind Transport { get; init; }

    public McpConfigurationOrigin Origin { get; init; } = McpConfigurationOrigin.User;

    /// <summary>Executable only, never a shell command line.</summary>
    public string? Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Child variable name → source environment-variable name.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public Uri? Endpoint { get; init; }

    /// <summary>HTTP header name → source environment-variable name.</summary>
    public IReadOnlyDictionary<string, string> HeaderEnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record McpValidationIssue(string Code, string Message);

public sealed record McpValidationResult(IReadOnlyList<McpValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record McpActivationDecision(
    bool CanActivate,
    string Reason,
    string Fingerprint,
    IReadOnlyList<McpValidationIssue> ValidationIssues);

/// <summary>Validates only configurations that can later be launched without a shell.</summary>
public static class McpServerValidator
{
    private static readonly HashSet<string> ShellExecutables = new(
        ["cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
         "sh", "bash", "zsh", "fish", "wsl", "wsl.exe"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ForbiddenHeaders = new(
        ["Host", "Content-Length", "Transfer-Encoding", "Connection"],
        StringComparer.OrdinalIgnoreCase);

    public static McpValidationResult Validate(McpServerConfiguration server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var issues = new List<McpValidationIssue>();

        if (!IsSafeName(server.Name))
        {
            issues.Add(new McpValidationIssue(
                "invalid_name",
                "Server name must be a short ASCII identifier."));
        }

        if (server.Arguments.Count > 64 ||
            server.Arguments.Any(argument => !IsSafeValue(argument, 4096)))
        {
            issues.Add(new McpValidationIssue(
                "invalid_arguments",
                "Arguments must be separate, bounded strings without control characters."));
        }

        ValidateEnvironmentMap(
            server.EnvironmentVariables,
            "environment",
            issues,
            validateHeaderNames: false);
        ValidateEnvironmentMap(
            server.HeaderEnvironmentVariables,
            "headers",
            issues,
            validateHeaderNames: true);

        switch (server.Transport)
        {
            case McpTransportKind.Stdio:
                ValidateStdio(server, issues);
                break;

            case McpTransportKind.StreamableHttp:
                ValidateHttp(server, issues);
                break;

            default:
                issues.Add(new McpValidationIssue(
                    "unknown_transport",
                    "MCP transport is not supported."));
                break;
        }

        return new McpValidationResult(issues);
    }

    private static void ValidateStdio(
        McpServerConfiguration server,
        List<McpValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(server.Command) ||
            !IsSafeValue(server.Command, 4096))
        {
            issues.Add(new McpValidationIssue(
                "missing_command",
                "Stdio transport requires a bounded executable name or path."));
        }
        else
        {
            var command = server.Command;
            var executable = Path.GetFileName(command);
            if (ShellExecutables.Contains(executable))
            {
                issues.Add(new McpValidationIssue(
                    "shell_forbidden",
                    "Shell executables cannot be used as MCP server commands."));
            }

            if (!Path.IsPathRooted(command) && command.Any(char.IsWhiteSpace))
            {
                issues.Add(new McpValidationIssue(
                    "inline_command",
                    "A relative command must contain only the executable; put arguments in the arguments array."));
            }
        }

        if (server.Endpoint is not null || server.HeaderEnvironmentVariables.Count > 0)
        {
            issues.Add(new McpValidationIssue(
                "mixed_transport_fields",
                "Stdio configuration cannot contain HTTP endpoint or header fields."));
        }
    }

    private static void ValidateHttp(
        McpServerConfiguration server,
        List<McpValidationIssue> issues)
    {
        if (server.Endpoint is null || !server.Endpoint.IsAbsoluteUri)
        {
            issues.Add(new McpValidationIssue(
                "missing_endpoint",
                "HTTP transport requires an absolute endpoint."));
        }
        else
        {
            var endpoint = server.Endpoint;
            var secure = string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var loopbackHttp = string.Equals(
                                   endpoint.Scheme,
                                   Uri.UriSchemeHttp,
                                   StringComparison.OrdinalIgnoreCase) &&
                               endpoint.IsLoopback;
            if (!secure && !loopbackHttp)
            {
                issues.Add(new McpValidationIssue(
                    "insecure_endpoint",
                    "MCP HTTP endpoints must use HTTPS; HTTP is allowed only for loopback."));
            }

            if (!string.IsNullOrEmpty(endpoint.UserInfo) ||
                !string.IsNullOrEmpty(endpoint.Query) ||
                !string.IsNullOrEmpty(endpoint.Fragment))
            {
                issues.Add(new McpValidationIssue(
                    "endpoint_credentials",
                    "Endpoint user-info, query, and fragment are forbidden; reference secrets through environment-backed headers."));
            }
        }

        if (!string.IsNullOrWhiteSpace(server.Command) ||
            server.Arguments.Count > 0 ||
            server.EnvironmentVariables.Count > 0)
        {
            issues.Add(new McpValidationIssue(
                "mixed_transport_fields",
                "HTTP configuration cannot contain stdio command, argument, or child environment fields."));
        }
    }

    private static void ValidateEnvironmentMap(
        IReadOnlyDictionary<string, string> values,
        string label,
        List<McpValidationIssue> issues,
        bool validateHeaderNames)
    {
        if (values.Count > 64)
        {
            issues.Add(new McpValidationIssue(
                $"too_many_{label}",
                $"MCP {label} map is capped at 64 entries."));
            return;
        }

        foreach (var (target, sourceEnvironmentVariable) in values)
        {
            var validTarget = validateHeaderNames
                ? IsHttpHeaderName(target) && !ForbiddenHeaders.Contains(target)
                : IsEnvironmentVariableName(target);
            if (!validTarget || !IsEnvironmentVariableName(sourceEnvironmentVariable))
            {
                issues.Add(new McpValidationIssue(
                    $"invalid_{label}",
                    $"MCP {label} entries must map safe names to environment-variable names."));
            }
        }
    }

    private static bool IsSafeName(string value) =>
        value.Length is > 0 and <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeValue(string value, int maxLength) =>
        value.Length <= maxLength &&
        !value.Any(character => char.IsControl(character) && character != '\t');

    private static bool IsEnvironmentVariableName(string value) =>
        value.Length is > 0 and <= 256 &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsHttpHeaderName(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

/// <summary>
/// Fail-closed activation gate. Approval is tied to an exact configuration
/// fingerprint so edits invalidate earlier consent.
/// </summary>
public static class McpActivationPolicy
{
    public static McpActivationDecision Evaluate(
        McpServerConfiguration server,
        ExecutionPolicy executionPolicy,
        IEnumerable<string> approvedFingerprints)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(executionPolicy);
        ArgumentNullException.ThrowIfNull(approvedFingerprints);

        var validation = McpServerValidator.Validate(server);
        var fingerprint = ComputeFingerprint(server);
        if (!server.Enabled)
        {
            return Deny("MCP server is disabled.", fingerprint, validation);
        }

        if (!validation.IsValid)
        {
            return Deny("MCP server configuration is invalid.", fingerprint, validation);
        }

        if (!approvedFingerprints.Contains(fingerprint, StringComparer.Ordinal))
        {
            return Deny(
                "MCP server needs explicit approval for this exact configuration.",
                fingerprint,
                validation);
        }

        // Workspace-write may run the app's narrow routine-command broker, but an
        // arbitrary long-lived MCP child process is a separate authority and
        // requires confirmed full access.
        if (server.Transport == McpTransportKind.Stdio &&
            (executionPolicy.Sandbox != SandboxMode.FullAccess ||
             !executionPolicy.FullAccessConfirmed))
        {
            return Deny(
                "Current execution policy cannot start MCP processes.",
                fingerprint,
                validation);
        }

        if (server.Transport == McpTransportKind.StreamableHttp &&
            !executionPolicy.CanUseNetwork)
        {
            return Deny(
                "Current execution policy does not allow MCP network access.",
                fingerprint,
                validation);
        }

        return new McpActivationDecision(
            true,
            "Approved for activation.",
            fingerprint,
            validation.Issues);
    }

    public static string ComputeFingerprint(McpServerConfiguration server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var canonical = JsonSerializer.Serialize(new
        {
            server.Name,
            server.Enabled,
            Transport = server.Transport.ToString(),
            Origin = server.Origin.ToString(),
            server.Command,
            Arguments = server.Arguments,
            EnvironmentVariables = server.EnvironmentVariables
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray(),
            Endpoint = server.Endpoint?.AbsoluteUri,
            HeaderEnvironmentVariables = server.HeaderEnvironmentVariables
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToArray(),
        });
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static McpActivationDecision Deny(
        string reason,
        string fingerprint,
        McpValidationResult validation) => new(
            false,
            reason,
            fingerprint,
            validation.Issues);
}
