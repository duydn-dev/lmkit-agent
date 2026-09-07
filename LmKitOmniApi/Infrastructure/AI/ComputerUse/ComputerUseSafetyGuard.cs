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
/// Detection is grounded in the accessibility tree: a <c>type</c> OR <c>key</c> aimed at a
/// field whose role/name/type looks like a secret (password, OTP, card number, …), or a
/// <c>click</c>/<c>key</c> on an element that looks like a CAPTCHA control, is refused.
/// Marker matching is diacritic-folded, so both English AND Vietnamese labels are caught
/// whether or not the page supplies accents ("Mật khẩu" and "mat khau" both match). A field
/// whose exposed type is <c>"password"</c> is treated as a credential field regardless of
/// its label.
///
/// Short acronym markers ("pin", "otp", "cvv", …) match on LETTER BOUNDARIES, not as bare
/// substrings. Plain <c>Contains</c> made "Shipping address", "Pinterest" and "spinner" all
/// look like PIN fields, which hard-terminated any checkout-style session — the exact flow
/// the shipped grounding-eval fixtures model.
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
    /// <summary>
    /// One marker word/phrase plus HOW it must match.
    ///
    /// <c>WholeWord</c> markers are short acronyms that occur inside ordinary words — the
    /// reason "Shipping address", "Pinterest" and "spinner" all used to be classified as PIN
    /// fields and hard-terminate a checkout session. They must sit on a letter boundary.
    /// Longer, unambiguous markers stay plain substrings so "Password1", "cardnumber2" and
    /// concatenated attribute text still match.
    /// </summary>
    private readonly record struct Marker(string Text, bool WholeWord);

    private static Marker Word(string text) => new(text, WholeWord: true);
    private static Marker Sub(string text) => new(text, WholeWord: false);

    // Credential / secret / payment field indicators. English (ASCII) + Vietnamese (with
    // diacritics for readability). Matching folds diacritics on BOTH sides, so a marker
    // like "mật khẩu" also matches an un-accented "mat khau" in the page text.
    private static readonly Marker[] CredentialMarkers =
    {
        // English — long/unambiguous: substring is safe.
        Sub("password"), Sub("passwd"), Sub("passphrase"),
        Sub("one-time"), Sub("one time"), Sub("verification code"), Sub("auth code"),
        Sub("card number"), Sub("credit card"), Sub("cardnumber"), Sub("security code"),
        Sub("social security"),
        // English — short acronyms: whole-word ONLY, or they fire on ordinary words.
        Word("pwd"),   // not "pwdgen"-style prose
        Word("otp"),   // not "adopt"
        Word("2fa"), Word("mfa"),
        Word("cvv"), Word("cvc"),
        Word("ssn"),
        Word("pin"),   // not "shipping" / "Pinterest" / "spinner" / "pinned"
        // Vietnamese
        Sub("mật khẩu"), Sub("mã pin"), Sub("mã otp"), Sub("mã xác minh"), Sub("mã xác thực"),
        Sub("số thẻ"), Sub("thẻ tín dụng"), Sub("mã bảo mật"), Sub("xác minh"),
    };

    // CAPTCHA / bot-detection indicators (English + Vietnamese). All are long and
    // unambiguous, so substring matching carries no false-positive risk here.
    private static readonly Marker[] CaptchaMarkers =
    {
        // English
        Sub("captcha"), Sub("recaptcha"), Sub("hcaptcha"), Sub("turnstile"),
        Sub("not a robot"), Sub("i'm not a robot"), Sub("im not a robot"), Sub("i am not a robot"),
        Sub("verify you are human"), Sub("verify you're human"), Sub("are you human"), Sub("human verification"),
        // Vietnamese
        Sub("tôi không phải là người máy"), Sub("xác minh bạn là người"), Sub("xác minh con người"),
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

        // `key` is treated as text entry, not just navigation: a key press aimed at a
        // password/OTP field would enter a secret one character at a time, straight around
        // the `type` rule. It carries a ref (the parser requires one), so the target is
        // inspectable and this check is meaningful.
        if (action.Type is ComputerUseActionType.Type or ComputerUseActionType.Key)
        {
            // A field whose ROLE or exposed TYPE is "password" is a credential field even
            // when the (localized/absent) label carries no recognisable marker word.
            if (target is not null
                && (string.Equals(target.Role, "password", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(target.Type, "password", StringComparison.OrdinalIgnoreCase)))
                return "Refusing to send keystrokes to a password field — a human must enter credentials.";

            if (MatchesAny(target, CredentialMarkers, out var credMarker))
                return $"Refusing to send keystrokes to what appears to be a credential/payment field ('{credMarker}') — a human must enter this.";
        }

        if (action.Type is ComputerUseActionType.Click or ComputerUseActionType.Key)
        {
            if (MatchesAny(target, CaptchaMarkers, out var capMarker))
                return $"Refusing to interact with a CAPTCHA / bot-detection control ('{capMarker}') — handing off to a human.";
        }

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

    private static bool MatchesAny(InteractiveElement? element, Marker[] markers, out string matched)
    {
        matched = string.Empty;
        if (element is null) return false;

        var haystack = FoldDiacritics($"{element.Role} {element.Name} {element.Value} {element.Type}".ToLowerInvariant());
        foreach (var marker in markers)
        {
            var folded = FoldDiacritics(marker.Text.ToLowerInvariant());
            if (folded.Length == 0) continue;
            if (marker.WholeWord ? ContainsWord(haystack, folded) : haystack.Contains(folded, StringComparison.Ordinal))
            {
                matched = marker.Text;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whole-word containment: <paramref name="needle"/> must occur in
    /// <paramref name="haystack"/> without a LETTER immediately either side, so short
    /// acronyms match "PIN", "Mã PIN", "pin_code" and "cvv2" but NOT "shipping",
    /// "Pinterest", "spinner", "pinned" or "adopt".
    ///
    /// Digits deliberately count as a boundary: a field labelled "CVV2" / "pin1" is exactly
    /// the credential field the marker is looking for. All comparisons are ordinal on
    /// already-lowercased, diacritic-folded text.
    /// </summary>
    internal static bool ContainsWord(string haystack, string needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;

        var from = 0;
        while (true)
        {
            var index = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (index < 0) return false;

            var end = index + needle.Length;
            var leftOk = index == 0 || !char.IsLetter(haystack[index - 1]);
            var rightOk = end == haystack.Length || !char.IsLetter(haystack[end]);
            if (leftOk && rightOk) return true;

            from = index + 1;
            if (from > haystack.Length - needle.Length) return false;
        }
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
