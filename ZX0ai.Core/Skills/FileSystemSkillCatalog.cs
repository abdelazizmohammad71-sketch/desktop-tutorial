using System.Text;
using ZX0ai.Core.Security;
using ZX0ai.Core.Workspaces;

namespace ZX0ai.Core.Skills;

public enum FileSystemSkillSource
{
    User,
    Project,
}

/// <summary>
/// Metadata and instructions only. Scripts and other assets are intentionally
/// represented as presence flags; catalog discovery never executes or loads them.
/// </summary>
public sealed record FileSystemSkillPackage(
    string Name,
    string Description,
    string Instructions,
    string DirectoryPath,
    string SkillFilePath,
    FileSystemSkillSource Source,
    bool HasScripts,
    bool HasReferences,
    bool HasAssets,
    bool IsTruncated);

public sealed record FileSystemSkillCatalogSnapshot(
    IReadOnlyList<FileSystemSkillPackage> Skills,
    IReadOnlyList<string> Diagnostics)
{
    public static FileSystemSkillCatalogSnapshot Empty { get; } = new([], []);
}

public enum SkillMatchKind
{
    Explicit,
    Description,
}

/// <summary>
/// A matched instruction package. EffectivePolicy is exactly the caller's policy;
/// selecting a skill is not an authority boundary and cannot widen it.
/// </summary>
public sealed record FileSystemSkillMatch(
    FileSystemSkillPackage Skill,
    SkillMatchKind Kind,
    double Score,
    ExecutionPolicy EffectivePolicy);

public sealed record FileSystemSkillCatalogOptions
{
    public int MaxSkillFileBytes { get; init; } = 64 * 1024;

    public int MaxSkills { get; init; } = 256;

    public int MaxDirectoryDepth { get; init; } = 8;

    public int MaxVisitedDirectories { get; init; } = 2048;
}

public interface IFileSystemSkillCatalog
{
    Task<FileSystemSkillCatalogSnapshot> DiscoverAsync(
        string? projectRoot,
        string? userSkillsDirectory = null,
        CancellationToken cancellationToken = default);

    FileSystemSkillMatch? Match(
        FileSystemSkillCatalogSnapshot catalog,
        string task,
        IEnumerable<string> enabledSkillNames,
        ExecutionPolicy currentPolicy,
        string? explicitSkillName = null);
}

/// <summary>Discovers bounded SKILL.md packages from user and project stores.</summary>
public sealed class FileSystemSkillCatalog : IFileSystemSkillCatalog
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "for", "from", "with", "this", "that", "into", "your",
        "use", "using", "when", "skill", "task", "file", "files", "create",
    };

    private readonly FileSystemSkillCatalogOptions _options;

    public FileSystemSkillCatalog(FileSystemSkillCatalogOptions? options = null)
    {
        _options = options ?? new FileSystemSkillCatalogOptions();
        ValidateOptions(_options);
    }

    public static string DefaultUserSkillsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZX0ai",
        "skills");

    public async Task<FileSystemSkillCatalogSnapshot> DiscoverAsync(
        string? projectRoot,
        string? userSkillsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var byName = new Dictionary<string, FileSystemSkillPackage>(StringComparer.OrdinalIgnoreCase);

        var userRoot = string.IsNullOrWhiteSpace(userSkillsDirectory)
            ? DefaultUserSkillsDirectory
            : Path.GetFullPath(userSkillsDirectory);
        await DiscoverRootAsync(
            userRoot,
            FileSystemSkillSource.User,
            byName,
            diagnostics,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var canonicalProject = WorkspacePathGuard.CanonicalizeDirectory(projectRoot);
            var relative = Path.Combine(".zx0ai", "skills");
            if (WorkspacePathGuard.TryResolveRelative(
                    canonicalProject,
                    relative,
                    out var projectSkillsRoot,
                    out _) &&
                Directory.Exists(projectSkillsRoot))
            {
                await DiscoverRootAsync(
                    projectSkillsRoot,
                    FileSystemSkillSource.Project,
                    byName,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new FileSystemSkillCatalogSnapshot(
            byName.Values
                .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            diagnostics);
    }

    public FileSystemSkillMatch? Match(
        FileSystemSkillCatalogSnapshot catalog,
        string task,
        IEnumerable<string> enabledSkillNames,
        ExecutionPolicy currentPolicy,
        string? explicitSkillName = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(enabledSkillNames);
        ArgumentNullException.ThrowIfNull(currentPolicy);

        if (string.IsNullOrWhiteSpace(task))
        {
            return null;
        }

        var enabled = new HashSet<string>(enabledSkillNames, StringComparer.OrdinalIgnoreCase);
        if (enabled.Count == 0)
        {
            return null;
        }

        var candidates = catalog.Skills
            .Where(skill => enabled.Contains(skill.Name))
            .ToList();

        var requested = explicitSkillName;
        if (string.IsNullOrWhiteSpace(requested))
        {
            requested = candidates
                .Select(skill => skill.Name)
                .FirstOrDefault(name => ContainsExplicitInvocation(task, name));
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var exact = candidates.FirstOrDefault(skill =>
                string.Equals(skill.Name, requested, StringComparison.OrdinalIgnoreCase));
            return exact is null
                ? null
                : new FileSystemSkillMatch(
                    exact,
                    SkillMatchKind.Explicit,
                    1,
                    currentPolicy);
        }

        var taskTokens = Tokenize(task);
        if (taskTokens.Count == 0)
        {
            return null;
        }

        FileSystemSkillPackage? best = null;
        var bestScore = 0d;
        var bestOverlap = 0;
        foreach (var skill in candidates)
        {
            var descriptionTokens = Tokenize($"{skill.Name} {skill.Description}");
            var overlap = descriptionTokens.Count(taskTokens.Contains);
            if (overlap == 0)
            {
                continue;
            }

            var score = overlap /
                Math.Sqrt(taskTokens.Count * Math.Max(descriptionTokens.Count, 1));
            if (score > bestScore)
            {
                best = skill;
                bestScore = score;
                bestOverlap = overlap;
            }
        }

        // One highly specific token can be enough; otherwise require two matching
        // terms to avoid activating packages on generic prose.
        if (best is null || bestScore < 0.18 ||
            bestOverlap < 2 && !HasDistinctiveTokenOverlap(taskTokens, best))
        {
            return null;
        }

        return new FileSystemSkillMatch(
            best,
            SkillMatchKind.Description,
            bestScore,
            currentPolicy);
    }

    private async Task DiscoverRootAsync(
        string root,
        FileSystemSkillSource source,
        Dictionary<string, FileSystemSkillPackage> byName,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || byName.Count >= _options.MaxSkills)
        {
            return;
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Path.GetFullPath(root), 0));
        var visited = 0;

        while (pending.Count > 0 &&
               visited < _options.MaxVisitedDirectories &&
               byName.Count < _options.MaxSkills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            visited++;

            FileSystemInfo info;
            try
            {
                info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add($"Skipped linked skill directory: {directory}");
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Could not inspect skill directory: {directory}");
                continue;
            }

            var skillFile = Path.Combine(directory, "SKILL.md");
            if (File.Exists(skillFile))
            {
                var package = await ParsePackageAsync(
                    directory,
                    skillFile,
                    source,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (package is not null)
                {
                    if (!byName.TryAdd(package.Name, package))
                    {
                        diagnostics.Add(
                            $"Ignored duplicate skill '{package.Name}' from {package.SkillFilePath}.");
                    }
                }

                // scripts/references/assets belong to this package, not child skills.
                continue;
            }

            if (depth >= _options.MaxDirectoryDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    pending.Push((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Could not enumerate skill directory: {directory}");
            }
        }

        if (visited >= _options.MaxVisitedDirectories || byName.Count >= _options.MaxSkills)
        {
            diagnostics.Add("Skill discovery stopped at its configured limit.");
        }
    }

    private async Task<FileSystemSkillPackage?> ParsePackageAsync(
        string directory,
        string skillFile,
        FileSystemSkillSource source,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var bounded = await ReadUtf8BoundedAsync(
                skillFile,
                _options.MaxSkillFileBytes,
                cancellationToken).ConfigureAwait(false);
            if (!TryParseFrontmatter(
                    bounded.Content,
                    out var name,
                    out var description,
                    out var instructions,
                    out var error))
            {
                diagnostics.Add($"Ignored {skillFile}: {error}");
                return null;
            }

            return new FileSystemSkillPackage(
                name,
                description,
                instructions,
                directory,
                skillFile,
                source,
                Directory.Exists(Path.Combine(directory, "scripts")),
                Directory.Exists(Path.Combine(directory, "references")),
                Directory.Exists(Path.Combine(directory, "assets")),
                bounded.IsTruncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Could not read skill file: {skillFile}");
            return null;
        }
    }

    private static bool TryParseFrontmatter(
        string content,
        out string name,
        out string description,
        out string instructions,
        out string error)
    {
        name = string.Empty;
        description = string.Empty;
        instructions = string.Empty;
        error = string.Empty;

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimStart('\uFEFF');
        var lines = normalized.Split('\n');
        if (lines.Length < 4 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
        {
            error = "SKILL.md must begin with YAML frontmatter.";
            return false;
        }

        var closing = Array.FindIndex(
            lines,
            1,
            line => string.Equals(line.Trim(), "---", StringComparison.Ordinal));
        if (closing < 0 || closing > 64)
        {
            error = "SKILL.md frontmatter is missing or too large.";
            return false;
        }

        for (var index = 1; index < closing; index++)
        {
            var line = lines[index];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
            {
                name = value;
            }
            else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
        }

        if (!IsValidSkillName(name))
        {
            error = "frontmatter name is missing or invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(description) ||
            description.Length > 1024 ||
            description.Any(char.IsControl))
        {
            error = "frontmatter description is missing or invalid.";
            return false;
        }

        instructions = string.Join('\n', lines.Skip(closing + 1)).Trim();
        if (instructions.Length == 0)
        {
            error = "skill instructions are empty.";
            return false;
        }

        return true;
    }

    private static bool IsValidSkillName(string value) =>
        value.Length is > 0 and <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' && value[^1] == '"' ||
             value[0] == '\'' && value[^1] == '\''))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool ContainsExplicitInvocation(string task, string name)
    {
        var needle = "$" + name;
        var searchFrom = 0;
        while (searchFrom < task.Length)
        {
            var index = task.IndexOf(
                needle,
                searchFrom,
                StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + needle.Length;
            if (end == task.Length ||
                !char.IsLetterOrDigit(task[end]) && task[end] is not '-' and not '_')
            {
                return true;
            }

            searchFrom = end;
        }

        return false;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            AddToken(builder, tokens);
        }

        AddToken(builder, tokens);
        return tokens;
    }

    private static void AddToken(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length >= 3)
        {
            var token = builder.ToString();
            if (!StopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }

        builder.Clear();
    }

    private static bool HasDistinctiveTokenOverlap(
        IReadOnlySet<string> taskTokens,
        FileSystemSkillPackage skill)
    {
        var skillTokens = Tokenize($"{skill.Name} {skill.Description}");
        return taskTokens.Any(token => token.Length >= 8 && skillTokens.Contains(token));
    }

    private static async Task<BoundedText> ReadUtf8BoundedAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[maxBytes + 1];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(offset, bytes.Length - offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var truncated = offset > maxBytes || stream.Position < stream.Length;
        return new BoundedText(
            Encoding.UTF8.GetString(bytes, 0, Math.Min(offset, maxBytes)),
            truncated);
    }

    private static void ValidateOptions(FileSystemSkillCatalogOptions options)
    {
        if (options.MaxSkillFileBytes is < 1024 or > 1024 * 1024 ||
            options.MaxSkills is < 1 or > 4096 ||
            options.MaxDirectoryDepth is < 1 or > 32 ||
            options.MaxVisitedDirectories is < 1 or > 32_768)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Skill catalog limits are outside the supported range.");
        }
    }

    private sealed record BoundedText(string Content, bool IsTruncated);
}
