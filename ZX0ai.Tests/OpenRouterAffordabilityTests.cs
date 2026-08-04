using Xunit;
using ZX0ai.Core.Providers;

namespace ZX0ai.Tests;

/// <summary>
/// The exact rejection this pins: OpenRouter states an affordable ceiling in plain
/// English, and the provider retries once with a request built to fit inside it instead
/// of surfacing the sentence as an unrecoverable failure.
/// </summary>
public sealed class OpenRouterAffordabilityTests
{
    [Fact]
    public void RealRejectionMessage_ParsesTheAffordableCeiling()
    {
        const string message = "This request requires more credits, or fewer max_tokens. " +
            "You requested up to 8192 tokens, but can only afford 1327. To increase, visit " +
            "https://openrouter.ai/settings/credits and upgrade to a paid account";

        Assert.True(OpenRouterAffordability.TryParseAffordableTokens(message, out var tokens));
        Assert.Equal(1327, tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("The model is temporarily overloaded.")]
    [InlineData("Rate limit exceeded, try again later.")]
    public void UnrelatedMessage_DoesNotParse(string? message) =>
        Assert.False(OpenRouterAffordability.TryParseAffordableTokens(message, out _));

    [Fact]
    public void FromStatus_RecognisesTheShortfallRegardlessOfStatusCode()
    {
        // OpenRouter's status for this case is not documented; the check must not
        // depend on it being any particular code.
        var ex = ChatProviderException.FromStatus(
            400,
            "You requested up to 8192 tokens, but can only afford 200.");

        Assert.Equal(ChatFailureReason.InsufficientCredits, ex.Reason);
    }

    [Fact]
    public void FromStatus_OrdinaryBadRequest_StaysModelError()
    {
        var ex = ChatProviderException.FromStatus(400, "Invalid 'temperature': must be between 0 and 2.");

        Assert.Equal(ChatFailureReason.ModelError, ex.Reason);
    }
}
