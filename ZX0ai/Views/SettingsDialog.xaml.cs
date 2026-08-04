using Microsoft.UI.Xaml.Controls;
using ZX0ai.Core.Configuration;
using ZX0ai.Core.Services;

namespace ZX0ai.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly IConfigService _config;

    public SettingsDialog(IConfigService config)
    {
        _config = config;

        InitializeComponent();
        InitializeFields();
    }

    private void InitializeFields()
    {
        ProviderComboBox.ItemsSource = new[] { "openrouter", "qwen" };
        ProviderComboBox.SelectedItem = _config.Options.Provider ?? "openrouter";

        OpenRouterApiKeyVariable.Text = _config.Options.OpenRouter.ApiKeyEnvironmentVariable;
        OpenRouterBaseUrl.Text = _config.Options.OpenRouter.BaseUrl;

        QwenApiKeyVariable.Text = _config.Options.Qwen.ApiKeyEnvironmentVariable;
        QwenBaseUrl.Text = _config.Options.Qwen.BaseUrl;
        QwenDefaultModel.Text = _config.Options.Qwen.DefaultModel;
        QwenTemperature.Text = _config.Options.Qwen.Temperature.ToString();
        QwenTopP.Text = _config.Options.Qwen.TopP.ToString();
        QwenMaxTokens.Text = _config.Options.Qwen.MaxTokens.ToString();
        QwenStream.IsOn = _config.Options.Qwen.Stream;
        QwenReasoningLevel.Text = _config.Options.Qwen.ReasoningLevel;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        sender.IsPrimaryButtonEnabled = false;

        _config.Options.Provider = ProviderComboBox.SelectedItem as string ?? "openrouter";
        _config.Options.OpenRouter.ApiKeyEnvironmentVariable = OpenRouterApiKeyVariable.Text.Trim();

        _config.Options.Qwen.ApiKeyEnvironmentVariable = QwenApiKeyVariable.Text.Trim();
        _config.Options.Qwen.BaseUrl = QwenBaseUrl.Text.Trim();
        _config.Options.Qwen.DefaultModel = QwenDefaultModel.Text.Trim();
        _config.Options.Qwen.Temperature = decimal.TryParse(QwenTemperature.Text, out var temperature) ? temperature : _config.Options.Qwen.Temperature;
        _config.Options.Qwen.TopP = decimal.TryParse(QwenTopP.Text, out var topP) ? topP : _config.Options.Qwen.TopP;
        _config.Options.Qwen.MaxTokens = int.TryParse(QwenMaxTokens.Text, out var maxTokens) ? maxTokens : _config.Options.Qwen.MaxTokens;
        _config.Options.Qwen.Stream = QwenStream.IsOn;
        _config.Options.Qwen.ReasoningLevel = QwenReasoningLevel.Text.Trim();

        await _config.SaveUserOverridesAsync();
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _ = sender;
        _ = args;
    }

    private void OnProviderChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
    }
}
