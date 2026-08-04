using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ZX0ai.Core.Models;
using ZX0ai.Core.Services;

namespace ZX0ai.Core.Providers;

/// <summary>
/// Loads and sanitizes the live OpenRouter catalog, caches capabilities locally,
/// and atomically projects explicit requested/fallback resolution onto tier members.
/// </summary>
public sealed class OpenRouterCatalogService(
    IConfigService config,
    ConfigPaths paths,
    HttpClient httpClient,
    ILogger<OpenRouterCatalogService> logger) : IOpenRouterCatalogService
{
    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private OpenRouterCatalogSnapshot _current = new();

    public OpenRouterCatalogSnapshot Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public OpenRouterModelCapability? Find(string slug) => Current.Find(slug);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var cached = await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            SetSnapshot(cached);
        }

        if (!config.Options.OpenRouter.ValidateModelsOnStartup)
        {
            ApplyResolutions(Current);
            return;
        }

        var maxAge = TimeSpan.FromHours(Math.Clamp(
            config.Options.OpenRouter.CatalogCacheHours,
            1,
            168));

        if (cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt <= maxAge)
        {
            ApplyResolutions(cached);
            return;
        }

        try
        {
            // Startup validation must not leave a blank window behind a slow network.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await RefreshAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OpenRouter catalog refresh failed; using the sanitized cache when available.");
            ApplyResolutions(Current);
        }
    }

    public async Task<OpenRouterCatalogSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = OpenRouterEndpointPolicy.Build(config.Options.OpenRouter, "models");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // The catalog currently permits anonymous reads, but authenticated reads
            // can reflect account-scoped availability. The host is pinned above.
            if (config.ResolveCredential(null) is { Length: > 0 } credential)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            }

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await ParseAsync(stream, cancellationToken).ConfigureAwait(false);

            SetSnapshot(snapshot);
            ApplyResolutions(snapshot);
            await WriteCacheAsync(snapshot, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "OpenRouter catalog refreshed: {Count} sanitized model capability records.",
                snapshot.Models.Count);

            return snapshot;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SetSnapshot(OpenRouterCatalogSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyResolutions(OpenRouterCatalogSnapshot snapshot)
    {
        foreach (var tier in config.Tiers)
        {
            foreach (var member in tier.AllMembers)
            {
                var requested = string.IsNullOrWhiteSpace(member.RequestedSlug)
                    ? member.Model
                    : member.RequestedSlug;

                if (snapshot.Find(requested) is not null)
                {
                    member.ResolvedSlug = requested;
                    member.Availability = ModelAvailability.Available;
                    continue;
                }

                var fallback = member.FallbackSlugs.FirstOrDefault(slug => snapshot.Find(slug) is not null);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    member.ResolvedSlug = fallback;
                    member.Availability = ModelAvailability.Fallback;
                    logger.LogWarning(
                        "Configured model {Requested} is unavailable; explicitly resolved to fallback {Resolved} for tier {Tier}.",
                        requested,
                        fallback,
                        tier.Key);
                    continue;
                }

                member.ResolvedSlug = string.Empty;
                member.Availability = ModelAvailability.Unavailable;
                logger.LogWarning(
                    "Configured model {Requested} is unavailable and has no available explicit fallback (tier {Tier}).",
                    requested,
                    tier.Key);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<OpenRouterCatalogSnapshot> ParseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("OpenRouter catalog response has no data array.");
        }

        var models = new List<OpenRouterModelCapability>();
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var parameters = ReadStringArray(item, "supported_parameters");
            var efforts = Array.Empty<string>();
            var supportsMaxTokens = false;
            var mandatory = false;

            if (item.TryGetProperty("reasoning", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.Object)
            {
                efforts = ReadStringArray(reasoning, "supported_efforts");
                supportsMaxTokens = ReadBoolean(reasoning, "supports_max_tokens");
                mandatory = ReadBoolean(reasoning, "mandatory");
            }

            string? promptPrice = null;
            string? completionPrice = null;
            if (item.TryGetProperty("pricing", out var pricing) &&
                pricing.ValueKind == JsonValueKind.Object)
            {
                promptPrice = ReadString(pricing, "prompt");
                completionPrice = ReadString(pricing, "completion");
            }

            models.Add(new OpenRouterModelCapability
            {
                Id = id,
                Name = ReadString(item, "name") ?? id,
                SupportedParameters = parameters,
                SupportedEfforts = efforts,
                SupportsReasoningMaxTokens = supportsMaxTokens,
                ReasoningMandatory = mandatory,
                ContextLength = ReadInt32(item, "context_length"),
                PromptPrice = promptPrice,
                CompletionPrice = completionPrice,
            });
        }

        return new OpenRouterCatalogSnapshot
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Models = models,
        };
    }

    private async Task<OpenRouterCatalogSnapshot?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        var path = ResolveCachePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<OpenRouterCatalogSnapshot>(stream, CacheJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "Ignoring an unreadable OpenRouter catalog cache.");
            return null;
        }
    }

    private async Task WriteCacheAsync(
        OpenRouterCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var path = ResolveCachePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer
                .SerializeAsync(stream, snapshot, CacheJson, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string ResolveCachePath() => paths.CatalogCachePath ?? Path.Combine(
        Path.GetDirectoryName(paths.UserOverridePath) ?? AppContext.BaseDirectory,
        "openrouter-catalog.json");

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] ReadStringArray(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : [];

    private static bool ReadBoolean(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static int? ReadInt32(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
}
