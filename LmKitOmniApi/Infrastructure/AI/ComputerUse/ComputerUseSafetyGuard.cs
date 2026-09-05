using System.Globalization;
using System.Text;

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
/// role/name/type looks like a secret (password, OTP, card number, …) or a <c>click</c>
/// on an element that looks like a CAPTCHA control is refused. Marker matching is
/// diacritic-folded, so both English AND Vietnamese labels are caught whether or not the
/// page supplies accents ("Mật khẩu" and "mat khau" both match). A field whose exposed
/// type is <c>"password"</c> is treated as a credential field regardless of its label.
///
/// IMPORTANT — this guard can only judge an action it can GROUND to an element in the
/// observation. A coordinate-only action, an action with no ref, or a ref absent from
/// the current observation resolves to a null target and returns null here; the AGENT
/// LOOP fails those closed separately (it cannot inspect the target, so it cannot rule
/// out a credential/CAPTCHA surface). This guard therefore never green-lights an
/// ungroundable action — it simply defers it to that fail-closed check.
/// </summary>
public static class ComputerUseSafetyGuard
{
    // Credential / secret / payment field indicators. English (ASCII) + Vietnamese (with
    // diacritics for readability). Matching folds diacritics on BOTH sides, so a marker
    // like "mật khẩu" also matches an un-accented "mat khau" in the page text.
    private static readonly string[] CredentialMarkers =
    {
        // English
        "password", "passwd", "passphrase", "pwd",
        "otp", "one-time", "one time", "2fa", "mfa", "verification code", "auth code",
        "cvv", "cvc", "card number", "credit card", "cardnumber", "security code",
        "ssn", "social security", "pin",
        // Vietnamese
        "mật khẩu", "mã pin", "mã otp", "mã xác minh", "mã xác thực",
        "số thẻ", "thẻ tín dụng", "mã bảo mật", "xác minh",
    };

    // CAPTCHA / bot-detection indicators (English + Vietnamese).
    private static readonly string[] CaptchaMarkers =
    {
        // English
        "captcha", "recaptcha", "hcaptcha", "turnstile",
        "not a robot", "i'm not a robot", "im not a robot", "i am not a robot",
        "verify you are human", "verify you're human", "are you human", "human verification",
        // Vietnamese
        "tôi không phải là người máy", "xác minh bạn là người", "xác minh con người",
    };

    /// <summary>
    /// Returns a non-null refusal reason when <paramref name="action"/> would enter
    /// credentials/payment details or engage a CAPTCHA — meaning it must be handed off to
    /// a human, never executed. Returns null when the action is allowed to proceed
    /// (subject to the normal grounding + allowlist + approval checks in the loop).
    /// </summary>
    public static string? RequiresHumanHandoff(ComputerUseAction action, ComputerUseObservation? observation)
    {
        var target = ResolveTarget(action, observation);

        if (action.Type == ComputerUseActionType.Type)
        {
            // A field whose ROLE or exposed TYPE is "password" is a credential field even
            // when the (localized/absent) label carries no recognisable marker word.
            if (target is not null
                && (string.Equals(target.Role, "password", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target.Type, "password", StringComparison.OrdinalIgnoreCase)))
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

        var haystack = FoldDiacritics($"{element.Role} {element.Name} {element.Value} {element.Type}".ToLowerInvariant());
        foreach (var marker in markers)
        {
            var folded = FoldDiacritics(marker.ToLowerInvariant());
            if (haystack.Contains(folded, StringComparison.Ordinal))
            {
                matched = marker;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes combining diacritic marks and maps đ/Đ so Vietnamese folds to ASCII, letting
    /// one marker list match both accented ("mật khẩu") and un-accented ("mat khau") text.
    /// Mirrors <c>ToolSandboxService.FoldDiacritics</c>.
    /// </summary>
    private static string FoldDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(ch switch { 'đ' => 'd', 'Đ' => 'D', _ => ch });
        }
        return builder.ToString();
    }
}
