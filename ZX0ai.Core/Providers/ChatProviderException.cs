namespace ZX0ai.Core.Providers;

/// <summary>Why a chat request failed, in terms the UI can act on.</summary>
public enum ChatFailureReason
{
    /// <summary>No API key configured yet.</summary>
    NotConfigured,

    /// <summary>401/403 — the key is missing, wrong or revoked.</summary>
    Unauthorized,

    /// <summary>429 — rate limited or out of credit.</summary>
    RateLimited,

    /// <summary>
    /// The account cannot afford the requested output length. Distinct from
    /// <see cref="RateLimited"/>: unlike a 429, this is resolved by requesting fewer
    /// tokens or paying, and it is exactly what <see cref="OpenRouterAffordability"/>
    /// exists to recover from automatically before it ever reaches the user.
    /// </summary>
    InsufficientCredits,

    /// <summary>5xx from the gateway or the upstream model.</summary>
    ServerError,

    /// <summary>The model rejected the request or returned an error payload.</summary>
    ModelError,

    /// <summary>DNS, TLS or socket failure.</summary>
    Network,

    Unknown,
}

/// <summary>
/// A chat failure carrying both a machine-readable reason and the .resw key for the
/// message shown to the user, so no call site has to map status codes to English.
/// </summary>
public sealed class ChatProviderException(
    ChatFailureReason reason,
    string resourceKey,
    string? detail = null,
    Exception? innerException = null)
    : Exception(detail ?? reason.ToString(), innerException)
{
    public ChatFailureReason Reason { get; } = reason;

    public string ResourceKey { get; } = resourceKey;

    /// <summary>Provider-supplied text, for logs. Never shown raw to the user.</summary>
    public string? Detail { get; } = detail;

    /// <summary>
    /// Maps a status code and body to a reason.
    /// </summary>
    /// <remarks>
    /// The credit-shortfall check runs before the status-code switch and regardless of
    /// what status accompanied it: OpenRouter's own status for this case is not
    /// documented and not worth depending on when the message text is specific and
    /// unambiguous enough to detect on its own.
    /// </remarks>
    public static ChatProviderException FromStatus(int statusCode, string? detail)
    {
        if (OpenRouterAffordability.TryParseAffordableTokens(detail, out _))
        {
            return new ChatProviderException(ChatFailureReason.InsufficientCredits, "Error_InsufficientCredits", detail);
        }

        return statusCode switch
        {
            401 or 403 => new ChatProviderException(
                ChatFailureReason.Unauthorized, "Error_ApiKeyInvalid", detail),

            429 => new ChatProviderException(
                ChatFailureReason.RateLimited, "Error_RateLimited", detail),

            >= 500 => new ChatProviderException(
                ChatFailureReason.ServerError, "Error_ServerError", detail),

            _ => new ChatProviderException(
                ChatFailureReason.ModelError, "Error_ModelFailed", detail),
        };
    }
}

/// <summary>
/// Recovers from OpenRouter's "can afford fewer tokens than requested" rejection.
/// </summary>
/// <remarks>
/// OpenRouter states the account's exact affordable ceiling in the error text — there is
/// nothing to estimate. Parsing it out is what lets the provider retry once with a
/// request that will actually succeed, rather than surfacing a raw upstream sentence as
/// though there were nothing to do about it.
/// </remarks>
public static class OpenRouterAffordability
{
    private static readonly System.Text.RegularExpressions.Regex AffordablePattern = new(
        @"can only afford (\d+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the message is OpenRouter's credit-shortfall rejection, with the affordable ceiling.</summary>
    public static bool TryParseAffordableTokens(string? message, out int affordableTokens)
    {
        affordableTokens = 0;

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var match = AffordablePattern.Match(message);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var parsed) || parsed <= 0)
        {
            return false;
        }

        affordableTokens = parsed;
        return true;
    }
}
