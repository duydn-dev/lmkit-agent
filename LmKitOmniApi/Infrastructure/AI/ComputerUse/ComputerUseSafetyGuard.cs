namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// The hard refusal boundary for the computer-use loop. Some actions must NEVER be
/// executed by the agent — not even with human approval through the normal gate —
/// because they are exactly the situations where a human must take over: entering
/// passwords / credentials / payment details, or solving a CAPTCHA / bot-detection
/// challenge.
///
/// This guard inspects the CHOSEN action against the CURRENT observation and, when it
/// recognises such a situation, returns a refusal reason. The loop then converts the
/// step into an <c>ask</c> hand-off to the human and stops — it does not click, does not
/// type, and does not route the action through the approval gate. The same rule is
/// stated to the model in the system prompt; this guard is the defence-in-depth that
/// enforces it even if the model ignores the instruction.
///
/// Detection is grounded in the accessibility tree: a <c>type</c> into a field whose
/// role/name looks like a secret (password, OTP, card number, …) or a <c>click</c> on an
/// element that looks like a CAPTCHA control is refused. Coordinate-only actions cannot
/// be grounded to an element, which is one more reason the loop and prompt prefer refs.
/// </summary>
public static class ComputerUseSafetyGuard
{
    // Credential / secret / payment field indicators (lowercase, diacritic-free ASCII).
    private static readonly string[] CredentialMarkers =
    {
        "password", "passwd", "passphrase", "pwd",
        "otp", "one-time", "one time", "2fa", "mfa", "verification code", "auth code",
        "cvv", "cvc", "card number", "credit card", "cardnumber", "security code",
        "ssn", "social security", "pin",
    };

    // CAPTCHA / bot-detection indicators.
    private static readonly string[] CaptchaMarkers =
    {
        "captcha", "recaptcha", "hcaptcha", "turnstile",
        "not a robot", "i'm not a robot", "im not a robot", "i am not a robot",
        "verify you are human", "verify you're human", "are you human", "human verification",
    };

    /// <summary>
    /// Returns a non-null refusal reason when <paramref name="action"/> would enter
    /// credentials/payment details or engage a CAPTCHA — meaning it must be handed off to
    /// a human, never executed. Returns null when the action is allowed to proceed
    /// (subject to the normal allowlist + approval checks).
    /// </summary>
    public static string? RequiresHumanHandoff(ComputerUseAction action, ComputerUseObservation? observation)
    {
        var target = ResolveTarget(action, observation);

        if (action.Type == ComputerUseActionType.Type)
        {
            // Password-typed fields are frequently exposed with a distinct role.
            if (target is not null && string.Equals(target.Role, "password", StringComparison.OrdinalIgnoreCase))
                return "Refusing to type into a password field — a human must enter credentials.";

            if (MatchesAny(target, CredentialMarkers, out var credMarker))
                return $"Refusing to type into what appears to be a credential/payment field ('{credMarker}') — a human must enter this.";
        }

        if (action.Type is ComputerUseActionType.Click or ComputerUseActionType.Key)
        {
            if (MatchesAny(target, CaptchaMarkers, out var capMarker))
                return $"Refusing to interact with a CAPTCHA / bot-detection control ('{capMarker}') — handing off to a human.";
        }

        // A click on a credential-submit or CAPTCHA element is likewise refused.
        if (action.Type == ComputerUseActionType.Click && MatchesAny(target, CaptchaMarkers, out var clickCaptcha))
            return $"Refusing to solve a CAPTCHA ('{clickCaptcha}') — handing off to a human.";

        return null;
    }

    private static InteractiveElement? ResolveTarget(ComputerUseAction action, ComputerUseObservation? observation)
    {
        if (observation is null || action.Ref is not int refId) return null;
        foreach (var element in observation.Elements)
            if (element.Ref == refId)
                return element;
        return null;
    }

    private static bool MatchesAny(InteractiveElement? element, string[] markers, out string matched)
    {
        matched = string.Empty;
        if (element is null) return false;

        var haystack = $"{element.Role} {element.Name} {element.Value}".ToLowerInvariant();
        foreach (var marker in markers)
        {
            if (haystack.Contains(marker, StringComparison.Ordinal))
            {
                matched = marker;
                return true;
            }
        }
        return false;
    }
}
