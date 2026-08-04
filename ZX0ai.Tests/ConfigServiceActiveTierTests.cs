using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ZX0ai.Core.Services;

namespace ZX0ai.Tests;

public sealed class ConfigServiceActiveTierTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _baseSettingsPath;
    private readonly string _userOverridePath;

    public ConfigServiceActiveTierTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ZX0ai.Tests",
            nameof(ConfigServiceActiveTierTests),
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempDirectory);
        _baseSettingsPath = Path.Combine(_tempDirectory, "appsettings.json");
        _userOverridePath = Path.Combine(_tempDirectory, "appsettings.local.json");
    }

    [Fact]
    public async Task LoadAsync_UsesConfiguredDefaultAsActiveTier()
    {
        await WriteSettingsAsync();
        var service = CreateService();

        await service.LoadAsync();

        Assert.Equal("zxa-Pro", service.DefaultTier.Key);
        Assert.Equal("zxa-Pro", service.ActiveTier.Key);
    }

    [Fact]
    public async Task SelectActiveTier_AcceptsConfiguredTier()
    {
        await WriteSettingsAsync();
        var service = CreateService();
        await service.LoadAsync();

        var selected = service.SelectActiveTier("zxa-Lite");

        Assert.True(selected);
        Assert.Equal("zxa-Lite", service.ActiveTier.Key);
        Assert.Equal("provider/lite-v1", service.ActiveTier.Model);
    }

    [Theory]
    [InlineData("provider/lite-v1")]
    [InlineData("zxa-Unknown")]
    public async Task SelectActiveTier_RejectsRawOrUnknownSlug(string key)
    {
        await WriteSettingsAsync();
        var service = CreateService();
        await service.LoadAsync();

        var selected = service.SelectActiveTier(key);

        Assert.False(selected);
        Assert.Equal("zxa-Pro", service.ActiveTier.Key);
    }

    [Fact]
    public async Task ReloadAsync_PreservesSelectedTierWhenItRemainsConfigured()
    {
        await WriteSettingsAsync();
        var service = CreateService();
        await service.LoadAsync();
        Assert.True(service.SelectActiveTier("zxa-Lite"));

        await WriteSettingsAsync(
            liteDisplayName: "zxa-Lite refreshed",
            liteModel: "provider/lite-v2");
        await service.ReloadAsync();

        Assert.Equal("zxa-Lite", service.ActiveTier.Key);
        Assert.Equal("zxa-Lite refreshed", service.ActiveTier.DisplayName);
        Assert.Equal("provider/lite-v2", service.ActiveTier.Model);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ConfigService CreateService() => new(
        new ConfigPaths(_baseSettingsPath, _userOverridePath),
        NullLogger<ConfigService>.Instance);

    private Task WriteSettingsAsync(
        string liteDisplayName = "zxa-Lite",
        string liteModel = "provider/lite-v1")
    {
        var json = $$"""
            {
              "defaultTier": "zxa-Pro",
              "tiers": {
                "zxa-Pro": {
                  "displayName": "zxa-Pro",
                  "mode": "single",
                  "model": "provider/pro"
                },
                "zxa-Lite": {
                  "displayName": "{{liteDisplayName}}",
                  "mode": "single",
                  "model": "{{liteModel}}"
                }
              }
            }
            """;

        return File.WriteAllTextAsync(_baseSettingsPath, json);
    }
}
