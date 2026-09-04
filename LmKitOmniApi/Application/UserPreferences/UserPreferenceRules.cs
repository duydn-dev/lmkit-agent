using System.Text;

namespace LmKitOmniApi.Application.UserPreferences;

/// <summary>
/// Shared validation, normalization and prompt composition for user-level custom
/// instructions (ChatGPT-style). Mirrors <see cref="Projects.ProjectRules"/> so the
/// GET/PUT endpoint and the chat handler run the exact same rules.
/// </summary>
public static class UserPreferenceRules
{
    public const int MaxFieldLength = 2000;

    /// <summary>
    /// Validates an upsert payload. Returns the exact Vietnamese error message for a
    /// 400 response, or null when the payload is valid. Both fields are optional; only
    /// their length is bounded (the entity's MaxLength mirrors this).
    /// </summary>
    public static string? Validate(string? aboutUser, string? responseStyle)
    {
        if (aboutUser is { Length: > MaxFieldLength })
            return $"Thông tin về bạn không được vượt quá {MaxFieldLength} ký tự.";
        if (responseStyle is { Length: > MaxFieldLength })
            return $"Phong cách phản hồi không được vượt quá {MaxFieldLength} ký tự.";
        return null;
    }

    /// <summary>Trims an optional field; whitespace-only collapses to null.</summary>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Composes the effective <c>AgentRequestOptions.PersonaPrompt</c> by PREPENDING
    /// the user's custom-instructions block to an already-composed persona (which may
    /// itself be a project + custom-agent composition), so the user context is stated
    /// first and any bound persona still applies after it. Whitespace-only inputs are
    /// treated as absent.
    /// <list type="bullet">
    ///   <item>Neither custom field present → the existing persona untouched
    ///   (byte-identical to a user with no custom instructions).</item>
    ///   <item>Custom fields present, no persona → just the custom-instructions block.</item>
    ///   <item>Both → the custom-instructions block first, then the existing persona.</item>
    /// </list>
    /// </summary>
    public static string? ComposePersonaPrompt(string? aboutUser, string? responseStyle, string? existingPersona)
    {
        var about = NormalizeOptional(aboutUser);
        var style = NormalizeOptional(responseStyle);
        var persona = string.IsNullOrWhiteSpace(existingPersona) ? null : existingPersona;

        if (about is null && style is null) return persona;

        var block = new StringBuilder("## Hướng dẫn tùy chỉnh của người dùng");
        if (about is not null)
            block.Append("\n\n### Thông tin về người dùng\n").Append(about);
        if (style is not null)
            block.Append("\n\n### Phong cách phản hồi mong muốn\n").Append(style);

        return persona is null
            ? block.ToString()
            : block.Append("\n\n").Append(persona).ToString();
    }
}
