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
