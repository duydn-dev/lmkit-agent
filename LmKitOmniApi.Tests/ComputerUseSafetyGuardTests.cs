using LmKitOmniApi.Infrastructure.AI.ComputerUse;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="ComputerUseSafetyGuard"/> — the credential/CAPTCHA
/// refusal boundary. These pin the BROADENED markers (English + Vietnamese, with diacritic
/// folding) and the field-type rule (type=="password" is a credential regardless of label),
/// and they document that the guard alone returns null for an UNGROUNDABLE action
/// (coordinate-only, or a ref absent from the observation) — which is exactly why the agent
/// loop fails those closed on top of this guard.
/// </summary>
public class ComputerUseSafetyGuardTests
{
    private static ComputerUseObservation Obs(params InteractiveElement[] els) =>
        new() { Url = "https://host/", Title = "t", Elements = els };

    private static ComputerUseAction TypeRef(int r) =>
        new() { Type = ComputerUseActionType.Type, Ref = r, Text = "secret" };

    private static ComputerUseAction ClickRef(int r) =>
        new() { Type = ComputerUseActionType.Click, Ref = r };

    private static ComputerUseAction KeyRef(int r, string keys = "Enter") =>
        new() { Type = ComputerUseActionType.Key, Ref = r, Keys = keys };

    // ── credential fields (English) ──

    [Fact]
    public void EnglishPasswordRole_Type_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "password", "Password", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void CleanTextbox_Type_IsAllowed()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Tìm kiếm", null));
        Assert.Null(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    // ── field type wins over the label ──

    [Fact]
    public void PasswordFieldType_Type_IsRefused_RegardlessOfLabel()
    {
        // Innocuous label, but the exposed input type is "password".
        var obs = Obs(new InteractiveElement(1, "textbox", "Đăng nhập", null, "password"));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    // ── credential fields (Vietnamese, accented + folded) ──

    [Fact]
    public void VietnamesePasswordLabel_Type_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Mật khẩu", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void VietnamesePasswordLabel_WithoutDiacritics_IsStillRefused()
    {
        // The page renders the label without accents — diacritic folding must still catch it.
        var obs = Obs(new InteractiveElement(1, "textbox", "Mat khau", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void VietnameseOtpLabel_Type_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Mã OTP", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void VietnameseCardNumberLabel_Type_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Số thẻ", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    // ── CAPTCHA controls ──

    [Fact]
    public void EnglishCaptcha_Click_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "button", "reCAPTCHA — verify you are human", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(ClickRef(1), obs));
    }

    [Fact]
    public void VietnameseCaptcha_Click_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "button", "Xác minh bạn là người", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(ClickRef(1), obs));
    }

    // ── short acronym markers must match on WORD boundaries, not as bare substrings ──
    //
    // `Contains("pin")` classified "Shipping address", "Pinterest" and "spinner" as PIN
    // fields, so typing a delivery address hard-terminated the session — and the shipped
    // grounding-eval fixtures are checkout flows built from exactly these labels.

    [Theory]
    [InlineData("Shipping address")]
    [InlineData("Pinterest")]
    [InlineData("spinner")]
    [InlineData("Pinned items")]
    [InlineData("Zip / Postal code")]
    public void ShortMarkerLookalikes_AreNotTreatedAsCredentialFields(string label)
    {
        var obs = Obs(new InteractiveElement(1, "textbox", label, null));
        Assert.Null(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Theory]
    [InlineData("PIN")]
    [InlineData("Enter your PIN code")]
    [InlineData("Mã PIN")]
    [InlineData("OTP")]
    [InlineData("Nhập mã OTP")]
    [InlineData("CVV")]
    [InlineData("CVV2")]
    [InlineData("SSN")]
    [InlineData("2FA code")]
    public void ShortMarkers_StillMatchAsWholeWords(string label)
    {
        var obs = Obs(new InteractiveElement(1, "textbox", label, null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void LongMarkers_StillMatchAsSubstrings()
    {
        // Long markers stay substring matches so suffixed/concatenated labels keep working.
        var obs = Obs(new InteractiveElement(1, "textbox", "Password1", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(1), obs));
    }

    [Fact]
    public void ContainsWord_RequiresLetterBoundaries()
    {
        Assert.True(ComputerUseSafetyGuard.ContainsWord("enter pin now", "pin"));
        Assert.True(ComputerUseSafetyGuard.ContainsWord("pin", "pin"));
        Assert.True(ComputerUseSafetyGuard.ContainsWord("cvv2", "cvv"));   // digits are boundaries
        Assert.True(ComputerUseSafetyGuard.ContainsWord("pin_code", "pin"));
        Assert.False(ComputerUseSafetyGuard.ContainsWord("shipping", "pin"));
        Assert.False(ComputerUseSafetyGuard.ContainsWord("pinterest", "pin"));
        Assert.False(ComputerUseSafetyGuard.ContainsWord("spinner", "pin"));
        Assert.False(ComputerUseSafetyGuard.ContainsWord("adopt", "otp"));
        // Must find a LATER whole-word occurrence even after an embedded near-miss.
        Assert.True(ComputerUseSafetyGuard.ContainsWord("shipping pin", "pin"));
    }

    // ── `key` is text entry too: keystrokes must never reach a credential field ──

    [Fact]
    public void KeyPress_IntoPasswordField_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Đăng nhập", null, "password"));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(KeyRef(1, "h"), obs));
    }

    [Fact]
    public void KeyPress_IntoOtpField_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "OTP", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(KeyRef(1, "1"), obs));
    }

    [Fact]
    public void KeyPress_OnCaptchaControl_IsRefused()
    {
        var obs = Obs(new InteractiveElement(1, "button", "reCAPTCHA", null));
        Assert.NotNull(ComputerUseSafetyGuard.RequiresHumanHandoff(KeyRef(1), obs));
    }

    [Fact]
    public void KeyPress_OnOrdinarySearchBox_IsAllowed()
    {
        // Pressing Enter to submit a search is the most common browser primitive; it must not
        // be refused (it used to be impossible to express at all).
        var obs = Obs(new InteractiveElement(1, "textbox", "Search", null));
        Assert.Null(ComputerUseSafetyGuard.RequiresHumanHandoff(KeyRef(1), obs));
    }

    // ── ungroundable actions: the guard defers (returns null); the AGENT loop fails closed ──

    [Fact]
    public void CoordinateOnlyType_CannotBeGrounded_GuardReturnsNull()
    {
        // A password field exists, but the action targets raw x/y (no ref) — the guard cannot
        // resolve a target, so it returns null. The agent loop's grounding check refuses it.
        var action = new ComputerUseAction { Type = ComputerUseActionType.Type, X = 10, Y = 20, Text = "secret" };
        var obs = Obs(new InteractiveElement(1, "password", "Password", null));
        Assert.Null(ComputerUseSafetyGuard.RequiresHumanHandoff(action, obs));
    }

    [Fact]
    public void RefAbsentFromObservation_GuardReturnsNull()
    {
        var obs = Obs(new InteractiveElement(1, "textbox", "Search", null));
        Assert.Null(ComputerUseSafetyGuard.RequiresHumanHandoff(TypeRef(99), obs));
    }
}
