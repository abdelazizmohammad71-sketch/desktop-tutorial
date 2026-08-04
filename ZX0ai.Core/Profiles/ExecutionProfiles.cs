using ZX0ai.Core.Security;

namespace ZX0ai.Core.Profiles;

public sealed record ExecutionProfile
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public SandboxMode SandboxMode { get; init; } = SandboxMode.ReadOnly;

    public ApprovalPolicy ApprovalPolicy { get; init; } = ApprovalPolicy.Untrusted;

    public bool NetworkAccess { get; init; }

    public string? DefaultTier { get; init; }

    public IReadOnlySet<string> EnabledSkills { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ExecutionProfileActivation(
    bool Activated,
    bool NeedsFullAccessConfirmation,
    string Reason,
    ExecutionProfile? Profile,
    ExecutionPolicy? Policy);

public interface IExecutionProfileCatalog
{
    IReadOnlyList<ExecutionProfile> All { get; }

    ExecutionProfileActivation Activate(
        string name,
        bool fullAccessConfirmed = false);
}

/// <summary>
/// Named, user-switchable policy bundles. Selecting a full-access profile does
/// not itself grant full access; activation succeeds only after explicit consent.
/// </summary>
public sealed class ExecutionProfileCatalog : IExecutionProfileCatalog
{
    private readonly Dictionary<string, ExecutionProfile> _profiles;

    public ExecutionProfileCatalog(IEnumerable<ExecutionProfile>? customProfiles = null)
    {
        _profiles = BuiltIns().ToDictionary(
            profile => profile.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (var profile in customProfiles ?? [])
        {
            Validate(profile);
            _profiles[profile.Name] = profile;
        }
    }

    public IReadOnlyList<ExecutionProfile> All =>
        _profiles.Values
            .OrderBy(profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public ExecutionProfileActivation Activate(
        string name,
        bool fullAccessConfirmed = false)
    {
        if (string.IsNullOrWhiteSpace(name) || !_profiles.TryGetValue(name, out var profile))
        {
            return new ExecutionProfileActivation(
                false,
                false,
                $"Execution profile '{name}' was not found.",
                null,
                null);
        }

        if (profile.SandboxMode == SandboxMode.FullAccess && !fullAccessConfirmed)
        {
            return new ExecutionProfileActivation(
                false,
                true,
                "Full-access profiles require explicit confirmation.",
                profile,
                new ExecutionPolicy(
                    profile.SandboxMode,
                    profile.ApprovalPolicy,
                    profile.NetworkAccess,
                    FullAccessConfirmed: false));
        }

        var policy = new ExecutionPolicy(
            profile.SandboxMode,
            profile.ApprovalPolicy,
            profile.NetworkAccess,
            FullAccessConfirmed: profile.SandboxMode == SandboxMode.FullAccess);
        return new ExecutionProfileActivation(
            true,
            false,
            "Execution profile activated.",
            profile,
            policy);
    }

    private static IEnumerable<ExecutionProfile> BuiltIns()
    {
        yield return new ExecutionProfile
        {
            Name = "strict",
            DisplayName = "Strict",
            SandboxMode = SandboxMode.ReadOnly,
            ApprovalPolicy = ApprovalPolicy.Untrusted,
            NetworkAccess = false,
        };

        yield return new ExecutionProfile
        {
            Name = "workspace",
            DisplayName = "Workspace",
            SandboxMode = SandboxMode.WorkspaceWrite,
            ApprovalPolicy = ApprovalPolicy.OnRequest,
            NetworkAccess = false,
        };

        yield return new ExecutionProfile
        {
            Name = "auto",
            DisplayName = "Auto (Full Access)",
            SandboxMode = SandboxMode.FullAccess,
            ApprovalPolicy = ApprovalPolicy.Never,
            NetworkAccess = true,
        };
    }

    private static void Validate(ExecutionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Name.Length is < 1 or > 64 ||
            !char.IsAsciiLetterOrDigit(profile.Name[0]) ||
            !profile.Name.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new ArgumentException("Execution profile has an invalid name.", nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName) ||
            profile.DisplayName.Length > 128 ||
            profile.DisplayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Execution profile has an invalid display name.",
                nameof(profile));
        }

        if (profile.DefaultTier is { Length: > 128 } ||
            profile.DefaultTier?.Any(char.IsControl) == true ||
            profile.EnabledSkills.Count > 256 ||
            profile.EnabledSkills.Any(skill =>
                string.IsNullOrWhiteSpace(skill) ||
                skill.Length > 128 ||
                skill.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "Execution profile exceeds its configured limits.",
                nameof(profile));
        }
    }
}
