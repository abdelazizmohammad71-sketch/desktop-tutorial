using System.Text;
using System.Text.Json;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Configuration;

public sealed record ProjectConfigurationRequest
{
    public required string ProjectRoot { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Optional application-owned session baseline. Repository layers can only
    /// tighten it. Supplying this value is an in-process trust decision, never a
    /// field read from project JSON.
    /// </summary>
    public ExecutionPolicy? TrustedBasePolicy { get; init; }

    /// <summary>Trusted application-owned defaults.</summary>
    public string? ShippedConfigPath { get; init; }

    /// <summary>Trusted preferences explicitly controlled by the local user.</summary>
    public string? UserConfigPath { get; init; }

    /// <summary>Optional profile name selected by the user for this task.</summary>
    public string? ActiveProfile { get; init; }

    /// <summary>
    /// Untrusted task-local JSON. It may tighten security but can never widen it.
    /// </summary>
    public string? TaskOverridesJson { get; init; }
}

public sealed record ConfigurationLayerAudit(
    string Source,
    bool Applied,
    IReadOnlyList<string> Notes);

/// <summary>Effective, bounded project configuration plus an audit trail.</summary>
public sealed record ResolvedProjectConfiguration
{
    public SandboxMode SandboxMode { get; init; } = SandboxMode.ReadOnly;

    public ApprovalPolicy ApprovalPolicy { get; init; } = ApprovalPolicy.Untrusted;

    public bool NetworkAccess { get; init; }

    public string? DefaultTier { get; init; }

    public IReadOnlySet<string> EnabledSkills { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string? ActiveProfile { get; init; }

    public IReadOnlyList<ConfigurationLayerAudit> Layers { get; init; } = [];

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>
    /// Full access remains inert until a separate, explicit confirmation is supplied.
    /// </summary>
    public ExecutionPolicy ToExecutionPolicy(bool fullAccessConfirmed = false) => new(
        SandboxMode,
        ApprovalPolicy,
        NetworkAccess,
        FullAccessConfirmed: SandboxMode == SandboxMode.FullAccess && fullAccessConfirmed);
}

public interface ILayeredProjectConfigurationResolver
{
    Task<ResolvedProjectConfiguration> ResolveAsync(
        ProjectConfigurationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves shipped → user → project root → cwd → task configuration. Only
/// allowlisted fields are accepted. Project, nested, and task layers are
/// monotonic: they may reduce authority, never increase it.
/// </summary>
public sealed class LayeredProjectConfigurationResolver :
    ILayeredProjectConfigurationResolver
{
    private const int MaxConfigBytes = 128 * 1024;
    private const int MaxLayers = 64;

    public async Task<ResolvedProjectConfiguration> ResolveAsync(
        ProjectConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = WorkspacePathGuard.CanonicalizeDirectory(request.ProjectRoot);
        var workingDirectory = ResolveWorkingDirectory(root, request.WorkingDirectory);
        var diagnostics = new List<string>();
        var audits = new List<ConfigurationLayerAudit>();
        var trustedProfiles = new Dictionary<string, ConfigPatch>(StringComparer.OrdinalIgnoreCase);
        var projectProfiles = new Dictionary<string, ConfigPatch>(StringComparer.OrdinalIgnoreCase);

        // With no trusted configuration, the resolver deliberately starts fail closed.
        var state = MutableConfiguration.SafeDefaults();
        if (request.TrustedBasePolicy is { } trustedBase)
        {
            state.SandboxMode = trustedBase.Sandbox;
            state.ApprovalPolicy = trustedBase.Approval;
            state.NetworkAccess = trustedBase.CanUseNetwork;
        }

        if (!string.IsNullOrWhiteSpace(request.ShippedConfigPath))
        {
            var document = await ReadLayerFileAsync(
                request.ShippedConfigPath,
                "shipped",
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                ApplyTrustedLayer(state, document.Patch);
                MergeProfiles(trustedProfiles, document.Profiles);
                audits.Add(new ConfigurationLayerAudit("shipped", true, document.Notes));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.UserConfigPath))
        {
            var document = await ReadLayerFileAsync(
                request.UserConfigPath,
                "user",
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                ApplyTrustedLayer(state, document.Patch);
                MergeProfiles(trustedProfiles, document.Profiles);
                audits.Add(new ConfigurationLayerAudit("user", true, document.Notes));
            }
        }

        // A trusted user profile establishes the ceiling before repository-owned
        // files are read. Full access still requires a separate consent step.
        if (!string.IsNullOrWhiteSpace(request.ActiveProfile) &&
            trustedProfiles.TryGetValue(request.ActiveProfile, out var trustedProfile))
        {
            ApplyTrustedLayer(state, trustedProfile);
            audits.Add(new ConfigurationLayerAudit(
                $"profile:{request.ActiveProfile}",
                true,
                ["Applied trusted user profile."]));
        }

        foreach (var path in EnumerateProjectConfigPaths(root, workingDirectory).Take(MaxLayers))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizePath(Path.GetRelativePath(root, path));
            var document = await ReadLayerFileAsync(
                path,
                relative,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                continue;
            }

            var notes = ApplyRestrictedLayer(state, document.Patch);
            notes.AddRange(document.Notes);
            MergeProfiles(projectProfiles, document.Profiles);
            audits.Add(new ConfigurationLayerAudit(relative, true, notes));
        }

        if (!string.IsNullOrWhiteSpace(request.ActiveProfile) &&
            !trustedProfiles.ContainsKey(request.ActiveProfile) &&
            projectProfiles.TryGetValue(request.ActiveProfile, out var projectProfile))
        {
            var notes = ApplyRestrictedLayer(state, projectProfile);
            notes.Add("Applied project profile without widening current authority.");
            audits.Add(new ConfigurationLayerAudit(
                $"profile:{request.ActiveProfile}",
                true,
                notes));
        }
        else if (!string.IsNullOrWhiteSpace(request.ActiveProfile) &&
                 !trustedProfiles.ContainsKey(request.ActiveProfile) &&
                 !projectProfiles.ContainsKey(request.ActiveProfile))
        {
            diagnostics.Add($"Profile '{request.ActiveProfile}' was not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.TaskOverridesJson))
        {
            var document = ParseLayer(
                request.TaskOverridesJson,
                "task",
                diagnostics);
            if (document is not null)
            {
                var notes = ApplyRestrictedLayer(state, document.Patch);
                notes.AddRange(document.Notes);
                audits.Add(new ConfigurationLayerAudit("task", true, notes));
            }
        }

        return new ResolvedProjectConfiguration
        {
            SandboxMode = state.SandboxMode,
            ApprovalPolicy = state.ApprovalPolicy,
            NetworkAccess = state.NetworkAccess,
            DefaultTier = state.DefaultTier,
            EnabledSkills = new HashSet<string>(state.EnabledSkills, StringComparer.OrdinalIgnoreCase),
            ActiveProfile = string.IsNullOrWhiteSpace(request.ActiveProfile)
                ? null
                : request.ActiveProfile,
            Layers = audits,
            Diagnostics = diagnostics,
        };
    }

    private static void ApplyTrustedLayer(MutableConfiguration state, ConfigPatch patch)
    {
        if (patch.SandboxMode is { } sandbox)
        {
            state.SandboxMode = sandbox;
        }

        if (patch.ApprovalPolicy is { } approval)
        {
            state.ApprovalPolicy = approval;
        }

        if (patch.NetworkAccess is { } network)
        {
            state.NetworkAccess = network;
        }

        if (patch.DefaultTier is not null)
        {
            state.DefaultTier = patch.DefaultTier;
        }

        if (patch.EnabledSkills is not null)
        {
            state.EnabledSkills.Clear();
            state.EnabledSkills.UnionWith(patch.EnabledSkills);
        }
    }

    private static List<string> ApplyRestrictedLayer(
        MutableConfiguration state,
        ConfigPatch patch)
    {
        var notes = new List<string>();

        if (patch.SandboxMode is { } sandbox)
        {
            if (Authority(sandbox) <= Authority(state.SandboxMode))
            {
                state.SandboxMode = sandbox;
            }
            else
            {
                notes.Add(
                    $"Ignored sandbox escalation from {state.SandboxMode} to {sandbox}.");
            }
        }

        if (patch.ApprovalPolicy is { } approval)
        {
            if (Authority(approval) <= Authority(state.ApprovalPolicy))
            {
                state.ApprovalPolicy = approval;
            }
            else
            {
                notes.Add(
                    $"Ignored approval escalation from {state.ApprovalPolicy} to {approval}.");
            }
        }

        if (patch.NetworkAccess is { } network)
        {
            if (!network || state.NetworkAccess)
            {
                state.NetworkAccess = network;
            }
            else
            {
                notes.Add("Ignored request to enable network access.");
            }
        }

        if (patch.DefaultTier is not null)
        {
            state.DefaultTier = patch.DefaultTier;
        }

        if (patch.EnabledSkills is not null)
        {
            var requested = new HashSet<string>(patch.EnabledSkills, StringComparer.OrdinalIgnoreCase);
            var rejected = requested.Except(state.EnabledSkills, StringComparer.OrdinalIgnoreCase).ToList();
            state.EnabledSkills.IntersectWith(requested);
            if (rejected.Count > 0)
            {
                notes.Add(
                    $"Ignored skill enablement outside the trusted allowlist: {string.Join(", ", rejected)}.");
            }
        }

        return notes;
    }

    private static int Authority(SandboxMode value) => value switch
    {
        SandboxMode.ReadOnly => 0,
        SandboxMode.WorkspaceWrite => 1,
        SandboxMode.FullAccess => 2,
        _ => 0,
    };

    private static int Authority(ApprovalPolicy value) => value switch
    {
        ApprovalPolicy.Untrusted => 0,
        ApprovalPolicy.OnRequest => 1,
        ApprovalPolicy.Never => 2,
        _ => 0,
    };

    private static async Task<ParsedConfigDocument?> ReadLayerFileAsync(
        string path,
        string source,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxConfigBytes)
            {
                diagnostics.Add($"Skipped {source}: config exceeds {MaxConfigBytes} bytes.");
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var memory = new MemoryStream((int)Math.Min(info.Length, MaxConfigBytes));
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            if (memory.Length > MaxConfigBytes)
            {
                diagnostics.Add($"Skipped {source}: config grew beyond its byte limit.");
                return null;
            }

            return ParseLayer(Encoding.UTF8.GetString(memory.ToArray()), source, diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Could not read config layer {source}.");
            return null;
        }
    }

    private static ParsedConfigDocument? ParseLayer(
        string json,
        string source,
        List<string> diagnostics)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
        {
            diagnostics.Add($"Skipped {source}: config exceeds {MaxConfigBytes} bytes.");
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add($"Skipped {source}: config root must be an object.");
                return null;
            }

            var notes = new List<string>();
            var patch = ParsePatch(document.RootElement, source, notes, allowProfiles: true);
            var profiles = ParseProfiles(document.RootElement, source, notes);
            return new ParsedConfigDocument(patch, profiles, notes);
        }
        catch (JsonException)
        {
            diagnostics.Add($"Skipped {source}: config is not valid JSON.");
            return null;
        }
    }

    private static ConfigPatch ParsePatch(
        JsonElement element,
        string source,
        List<string> notes,
        bool allowProfiles)
    {
        SandboxMode? sandbox = null;
        ApprovalPolicy? approval = null;
        bool? network = null;
        string? defaultTier = null;
        IReadOnlySet<string>? enabledSkills = null;

        foreach (var property in element.EnumerateObject())
        {
            var name = NormalizeKey(property.Name);
            switch (name)
            {
                case "sandboxmode":
                    if (TryParseSandbox(property.Value, out var parsedSandbox))
                    {
                        sandbox = parsedSandbox;
                    }
                    else
                    {
                        notes.Add($"Ignored invalid sandbox_mode in {source}.");
                    }

                    break;

                case "approvalpolicy":
                    if (TryParseApproval(property.Value, out var parsedApproval))
                    {
                        approval = parsedApproval;
                    }
                    else
                    {
                        notes.Add($"Ignored invalid approval_policy in {source}.");
                    }

                    break;

                case "networkaccess":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        network = property.Value.GetBoolean();
                    }
                    else
                    {
                        notes.Add($"Ignored invalid network_access in {source}.");
                    }

                    break;

                case "defaulttier":
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        IsSafeIdentifier(property.Value.GetString(), 128))
                    {
                        defaultTier = property.Value.GetString();
                    }
                    else
                    {
                        notes.Add($"Ignored invalid default_tier in {source}.");
                    }

                    break;

                case "enabledskills":
                    enabledSkills = ParseStringSet(property.Value, source, notes);
                    break;

                case "profiles" when allowProfiles:
                case "schema":
                    break;

                default:
                    notes.Add($"Ignored non-allowlisted field '{property.Name}' in {source}.");
                    break;
            }
        }

        return new ConfigPatch(sandbox, approval, network, defaultTier, enabledSkills);
    }

    private static Dictionary<string, ConfigPatch> ParseProfiles(
        JsonElement root,
        string source,
        List<string> notes)
    {
        var profiles = new Dictionary<string, ConfigPatch>(StringComparer.OrdinalIgnoreCase);
        var property = root.EnumerateObject().FirstOrDefault(
            item => NormalizeKey(item.Name) == "profiles");

        if (property.Value.ValueKind == JsonValueKind.Undefined)
        {
            return profiles;
        }

        if (property.Value.ValueKind != JsonValueKind.Object)
        {
            notes.Add($"Ignored invalid profiles object in {source}.");
            return profiles;
        }

        foreach (var profile in property.Value.EnumerateObject())
        {
            if (!IsSafeProfileName(profile.Name) || profile.Value.ValueKind != JsonValueKind.Object)
            {
                notes.Add($"Ignored invalid profile '{profile.Name}' in {source}.");
                continue;
            }

            profiles[profile.Name] = ParsePatch(
                profile.Value,
                $"{source} profile {profile.Name}",
                notes,
                allowProfiles: false);
        }

        return profiles;
    }

    private static IReadOnlySet<string>? ParseStringSet(
        JsonElement element,
        string source,
        List<string> notes)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            notes.Add($"Ignored invalid enabled_skills in {source}.");
            return null;
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                IsSafeIdentifier(item.GetString(), 128))
            {
                values.Add(item.GetString()!);
            }
            else
            {
                notes.Add($"Ignored invalid skill name in {source}.");
            }

            if (values.Count == 256)
            {
                notes.Add($"Skill list in {source} was capped at 256 entries.");
                break;
            }
        }

        return values;
    }

    private static bool TryParseSandbox(JsonElement element, out SandboxMode result)
    {
        result = SandboxMode.ReadOnly;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return NormalizeKey(element.GetString() ?? string.Empty) switch
        {
            "readonly" => Set(SandboxMode.ReadOnly, out result),
            "workspacewrite" => Set(SandboxMode.WorkspaceWrite, out result),
            "fullaccess" => Set(SandboxMode.FullAccess, out result),
            _ => false,
        };
    }

    private static bool TryParseApproval(JsonElement element, out ApprovalPolicy result)
    {
        result = ApprovalPolicy.Untrusted;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return NormalizeKey(element.GetString() ?? string.Empty) switch
        {
            "untrusted" => Set(ApprovalPolicy.Untrusted, out result),
            "onrequest" => Set(ApprovalPolicy.OnRequest, out result),
            "never" => Set(ApprovalPolicy.Never, out result),
            _ => false,
        };
    }

    private static bool Set<T>(T value, out T result)
    {
        result = value;
        return true;
    }

    private static IReadOnlyList<string> EnumerateProjectConfigPaths(
        string root,
        string workingDirectory)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var directories = new List<string>();
        var current = workingDirectory;
        while (true)
        {
            directories.Add(current);
            if (comparer.Equals(current, root))
            {
                break;
            }

            current = Directory.GetParent(current)?.FullName ?? root;
        }

        directories.Reverse();
        var paths = new List<string>();
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, ".zx0ai", "config.json");
            if (!File.Exists(candidate))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, candidate);
            if (WorkspacePathGuard.TryResolveRelative(root, relative, out var safe, out _) &&
                File.Exists(safe))
            {
                paths.Add(safe);
            }
        }

        return paths;
    }

    private static string ResolveWorkingDirectory(string root, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return root;
        }

        var candidate = Path.IsPathRooted(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Path.GetFullPath(Path.Combine(root, workingDirectory));
        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {candidate}");
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (!WorkspacePathGuard.TryResolveRelative(root, relative, out var safe, out var error) ||
            !Directory.Exists(safe))
        {
            throw new InvalidOperationException(
                $"Working directory must stay inside the active project. {error}");
        }

        return Path.TrimEndingDirectorySeparator(safe);
    }

    private static void MergeProfiles(
        Dictionary<string, ConfigPatch> target,
        IReadOnlyDictionary<string, ConfigPatch> source)
    {
        foreach (var (name, patch) in source)
        {
            target[name] = patch;
        }
    }

    private static bool IsSafeProfileName(string value) =>
        IsSafeIdentifier(value, 64) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeIdentifier(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maxLength &&
        !value.Any(char.IsControl);

    private static string NormalizeKey(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string NormalizePath(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record ConfigPatch(
        SandboxMode? SandboxMode,
        ApprovalPolicy? ApprovalPolicy,
        bool? NetworkAccess,
        string? DefaultTier,
        IReadOnlySet<string>? EnabledSkills);

    private sealed record ParsedConfigDocument(
        ConfigPatch Patch,
        IReadOnlyDictionary<string, ConfigPatch> Profiles,
        List<string> Notes);

    private sealed class MutableConfiguration
    {
        public SandboxMode SandboxMode { get; set; }

        public ApprovalPolicy ApprovalPolicy { get; set; }

        public bool NetworkAccess { get; set; }

        public string? DefaultTier { get; set; }

        public HashSet<string> EnabledSkills { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public static MutableConfiguration SafeDefaults() => new()
        {
            SandboxMode = SandboxMode.ReadOnly,
            ApprovalPolicy = ApprovalPolicy.Untrusted,
            NetworkAccess = false,
        };
    }
}
