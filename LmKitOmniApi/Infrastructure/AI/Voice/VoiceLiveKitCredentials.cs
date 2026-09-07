using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// THE single LiveKit credential source for the whole voice slice.
///
/// The product used to read credentials from two unrelated places: the browser token
/// endpoint read <c>LiveKit:ApiKey</c> / <c>LiveKit:ApiSecret</c> while the server-side
/// agent read <c>Voice:LiveKitApiKey</c> / <c>Voice:LiveKitApiSecret</c>. Configuring one
/// did nothing for the other, so an operator could have a working browser token and an
/// agent that stood down (or vice versa) with no diagnostic pointing at the cause.
///
/// PRECEDENCE (highest first), applied per-field so a partial override still works:
///  1. <c>Voice:LiveKitUrl</c> / <c>Voice:LiveKitApiKey</c> / <c>Voice:LiveKitApiSecret</c>
///     — the voice-specific keys.
///  2. <c>LiveKit:Url</c> / <c>LiveKit:ApiKey</c> / <c>LiveKit:ApiSecret</c> — the shared
///     server block that also configures the LiveKit container.
///
/// Resolution is a pure static function of (options, configuration), so it needs no DI
/// registration: the controller and the hosted service both already have — or can take —
/// an <see cref="IConfiguration"/> and an options snapshot.
/// </summary>
public sealed record VoiceLiveKitCredentials(string Url, string ApiKey, string ApiSecret)
{
    /// <summary>Shared configuration block (also consumed by the LiveKit server container).</summary>
    public const string SharedSectionName = "LiveKit";

    /// <summary>True only when BOTH the API key and secret are present; a token cannot be minted otherwise.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    /// <summary>True when a media client could actually connect (credentials AND a server URL).</summary>
    public bool CanJoin => IsConfigured && !string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// Resolves the effective credentials from the voice options first, then the shared
    /// <c>LiveKit</c> block. Never throws; missing values come back as empty strings and the
    /// caller decides how to refuse (501/500 for the endpoint, stand-down for the agent).
    /// </summary>
    public static VoiceLiveKitCredentials Resolve(VoiceOptions? options, IConfiguration? configuration) =>
        new(
            First(options?.LiveKitUrl, configuration?[$"{SharedSectionName}:Url"]),
            First(options?.LiveKitApiKey, configuration?[$"{SharedSectionName}:ApiKey"]),
            First(options?.LiveKitApiSecret, configuration?[$"{SharedSectionName}:ApiSecret"]));

    private static string First(string? preferred, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
        return string.Empty;
    }
}
