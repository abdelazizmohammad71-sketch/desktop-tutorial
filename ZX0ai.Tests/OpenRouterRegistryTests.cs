using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;
using ZX0ai.Core.Models;
using ZX0ai.Core.Providers;
using ZX0ai.Core.Services;

namespace ZX0ai.Tests;

public sealed class OpenRouterRegistryTests : IDisposable
{
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(),
        "ZX0ai.Tests",
        nameof(OpenRouterRegistryTests),
        Guid.NewGuid().ToString("n"));

    public OpenRouterRegistryTests() => Directory.CreateDirectory(_temp);

    [Fact]
    public async Task CommittedRegistry_HasExactTierOrderAndMembership()
    {
        var config = CreateConfig();
        await config.LoadAsync();

        // zax-v2 is a standalone single-mode tier; all others are independent teams
        // where every member uses the same model as the tier's leader.
        Assert.Equal(
            ["zax-ultra-full-max", "zax-pro", "zax-v2", "zax-light", "zax-low-free"],
            config.Tiers.Select(tier => tier.Key));
        Assert.Equal([5, 4, 0, 4, 3], config.Tiers.Select(tier => tier.AllMembers.Count));

        Assert.Equal("anthropic/claude-fable-5", config.Tiers[0].LeaderMember!.RequestedSlug);
        Assert.Equal("anthropic/claude-fable-5", config.Tiers[0].Members[0].RequestedSlug);

        // Every member declares a fallback, so a slug that disappears upstream degrades
        // instead of taking its capability offline.
        Assert.All(
            config.Tiers.SelectMany(tier => tier.AllMembers),
            member => Assert.NotEmpty(member.FallbackSlugs));

        // Each capability bills against its own credential.
        Assert.All(config.Tiers, tier => Assert.NotNull(tier.ApiKeyEnvironmentVariable));

        var v2 = config.FindTier("zax-v2")!;
        Assert.Equal(TeamMode.Single, v2.Mode);
        Assert.Equal("qwen-3.8-max", v2.Model);
        Assert.Null(v2.LeaderMember);
        Assert.Empty(v2.Members);

        // The free capability is free all the way down; a paid fallback there would
        // charge someone who chose it precisely because it does not.
        var free = config.FindTier("zax-low-free")!;
        Assert.All(
            free.AllMembers,
            member => Assert.EndsWith(":free", member.RequestedSlug, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TeamTiersUseSingleModelAcrossAllMembers()
    {
        var config = CreateConfig();
        await config.LoadAsync();

        foreach (var tier in config.Tiers.Where(tier => tier.IsTeam))
        {
            var leaderSlug = tier.LeaderMember?.RequestedSlug ?? tier.Leader ?? tier.Model;
            Assert.False(string.IsNullOrWhiteSpace(leaderSlug));
            Assert.All(
                tier.AllMembers,
                member => Assert.Equal(leaderSlug, member.RequestedSlug));
        }
    }

    [Fact]
    public async Task AliasResolvesWithoutAppearingInThePicker()
    {
        var config = CreateConfig();
        await config.LoadAsync();

        Assert.Equal("zax-ultra-full-max", config.FindTier("zax-ultra")?.Key);
        Assert.DoesNotContain(config.Tiers, tier => tier.Key == "zax-ultra");
    }

    [Fact]
    public async Task CatalogUsesOnlyExplicitFallbacksAndFailsClosed()
    {
        var config = CreateConfig();
        await config.LoadAsync();

        // Only the pro tier's declared fallback is listed as available upstream. The
        // requested slug is absent, so every member must engage the fallback. Because
        // all team members share the same model and fallbacks, they all resolve.
        const string body =
            """
            {"data":[
              {"id":"openai/gpt-5.5-pro","name":"GPT-5.5 Pro","context_length":400000,
               "supported_parameters":["reasoning","reasoning_effort","tools"],
               "reasoning":{"supported_efforts":["high","medium","low","minimal"]}}
            ]}
            """;

        using var http = new HttpClient(new StaticHandler(body));
        var paths = Paths();
        var catalog = new OpenRouterCatalogService(
            config,
            paths,
            http,
            NullLogger<OpenRouterCatalogService>.Instance);

        await catalog.RefreshAsync();

        // All pro members use the same slug; the leader resolves to the declared
        // fallback because the requested slug is absent from the catalog.
        var proLeader = config.FindTier("zax-pro")!.LeaderMember!;
        Assert.Equal("openai/gpt-5.5-pro", proLeader.ResolvedSlug);
        Assert.True(proLeader.IsFallbackActive);

        // Every team member resolves identically to the same fallback.
        Assert.All(
            config.FindTier("zax-pro")!.AllMembers,
            member => Assert.Equal("openai/gpt-5.5-pro", member.ResolvedSlug));
    }

    [Fact]
    public async Task CapabilityAdapterNormalizesEffortWithoutUnsupportedFields()
    {
        var config = CreateConfig();
        await config.LoadAsync();
        var catalog = new MemoryCatalog(
        [
            Capability("vendor/max", ["reasoning", "tools"], ["max", "high"]),
            Capability("vendor/xhigh", ["reasoning"], ["xhigh", "high"]),
            Capability("vendor/enabled", ["reasoning", "tools"], []),
            Capability("vendor/plain", ["tools"], []),
        ]);
        var adapter = new OpenRouterCapabilityAdapter(config, catalog);

        var ultra = adapter.Adapt(new ModelInvocation("vendor/max", "vendor/max", "ultra"));
        Assert.Equal("max", ultra.NormalizedEffort);
        Assert.Equal("max", ultra.Reasoning!["effort"]);

        var extraHigh = adapter.Adapt(new ModelInvocation("vendor/xhigh", "vendor/xhigh", "extra-high"));
        Assert.Equal("xhigh", extraHigh.Reasoning!["effort"]);

        var enabled = adapter.Adapt(new ModelInvocation("vendor/enabled", "vendor/enabled", "medium"));
        Assert.Equal(true, enabled.Reasoning!["enabled"]);
        Assert.DoesNotContain("effort", enabled.Reasoning.Keys);

        var plain = adapter.Adapt(new ModelInvocation("vendor/plain", "vendor/plain", "ultra"));
        Assert.Null(plain.Reasoning);
    }

    [Fact]
    public async Task SavingPreferencesNeverSerializesTheEnvironmentCredential()
    {
        const string sentinel = "zx0ai-test-secret-must-not-be-written";
        var previous = Environment.GetEnvironmentVariable(ConfigService.ApiKeyVariable);

        try
        {
            Environment.SetEnvironmentVariable(ConfigService.ApiKeyVariable, sentinel);
            var config = CreateConfig();
            await config.LoadAsync();
            await config.SaveUserOverridesAsync();

            var persisted = await File.ReadAllTextAsync(Paths().UserOverridePath);
            Assert.DoesNotContain(sentinel, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("\"apiKey\"", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigService.ApiKeyVariable, previous);
        }
    }

    [Fact]
    public void EndpointPolicyRejectsCredentialRedirects()
    {
        var options = new Core.Configuration.OpenRouterOptions
        {
            BaseUrl = "https://attacker.invalid/api/v1",
        };

        Assert.Throws<InvalidOperationException>(() =>
            OpenRouterEndpointPolicy.Build(options, "chat/completions"));
    }

    [Fact]
    public async Task UserPreferencesCannotReplaceProviderOrTierRegistry()
    {
        await File.WriteAllTextAsync(
            Paths().UserOverridePath,
            """
            {
              "provider": "attacker",
              "openrouter": { "baseUrl": "https://attacker.invalid/api/v1" },
              "tiers": {
                "evil": { "displayName": "evil", "mode": "single", "model": "evil/model" }
              },
              "defaultTier": "zax-low-free",
              "ui": { "language": "en-US", "userName": "Test" }
            }
            """);

        var config = CreateConfig();
        await config.LoadAsync();

        Assert.Equal("openrouter", config.Options.Provider);
        Assert.Equal("https://openrouter.ai/api/v1", config.Options.OpenRouter.BaseUrl);
        Assert.Null(config.FindTier("evil"));

        // The user file may pick among the shipped capabilities; it may not add one.
        Assert.Equal("zax-low-free", config.DefaultTier.Key);
    }

    [Fact]
    public async Task ProviderPayloadUsesOnlyValidatedResolutionAndAdaptedOptions()
    {
        var previous = Environment.GetEnvironmentVariable(ConfigService.ApiKeyVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConfigService.ApiKeyVariable, "unit-test-key");
            var config = CreateConfig();
            await config.LoadAsync();

            var catalog = new MemoryCatalog(
            [
                Capability("vendor/resolved", ["reasoning", "reasoning_effort", "tools"], ["max", "high"]),
            ]);
            var adapter = new OpenRouterCapabilityAdapter(config, catalog);
            var handler = new CapturingHandler();
            using var http = new HttpClient(handler);
            var provider = new OpenRouterProvider(
                config,
                adapter,
                http,
                NullLogger<OpenRouterProvider>.Instance);

            var deltas = new List<ChatDelta>();
            await foreach (var delta in provider.StreamAsync(
                new ModelInvocation("vendor/requested", "vendor/resolved", "ultra", "Fast"),
                [new ChatMessage { Role = ChatRole.User, Content = "hello" }]))
            {
                deltas.Add(delta);
            }

            using var payload = JsonDocument.Parse(handler.Payload!);
            Assert.Equal("vendor/resolved", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal("max", payload.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal("throughput", payload.RootElement.GetProperty("provider").GetProperty("sort").GetString());
            Assert.DoesNotContain("vendor/requested", handler.Payload, StringComparison.Ordinal);
            Assert.Equal("openrouter.ai", handler.RequestUri?.Host);
            var usage = Assert.Single(deltas, delta => delta.Kind == ChatDeltaKind.Usage).Usage;
            Assert.Equal(7, usage?.TotalTokens);

            var failure = await Assert.ThrowsAsync<ChatProviderException>(async () =>
            {
                await foreach (var _ in provider.StreamAsync(
                    "vendor/not-in-catalog",
                    [new ChatMessage { Role = ChatRole.User, Content = "hello" }]))
                {
                }
            });
            Assert.Equal(ChatFailureReason.ModelError, failure.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigService.ApiKeyVariable, previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ConfigService CreateConfig() => new(
        Paths(),
        NullLogger<ConfigService>.Instance);

    private ConfigPaths Paths() => new(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ZX0ai/appsettings.json")),
        Path.Combine(_temp, "appsettings.local.json"),
        Path.Combine(_temp, "catalog.json"));

    private static OpenRouterModelCapability Capability(
        string id,
        IReadOnlyList<string> parameters,
        IReadOnlyList<string> efforts) => new()
    {
        Id = id,
        Name = id,
        SupportedParameters = parameters,
        SupportedEfforts = efforts,
    };

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Payload { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4,\"total_tokens\":7,\"cost\":0.001}}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class MemoryCatalog(IReadOnlyList<OpenRouterModelCapability> models)
        : IOpenRouterCatalogService
    {
        public OpenRouterCatalogSnapshot Current { get; } = new()
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Models = models,
        };

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<OpenRouterCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public OpenRouterModelCapability? Find(string slug) => Current.Find(slug);
    }
}
