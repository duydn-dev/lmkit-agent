using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI;

public sealed record ClarificationOption(string Label, string Value, bool Recommended = false);

public sealed record ClarificationRequest(string Question, IReadOnlyList<ClarificationOption> Options)
{
    public static ClarificationRequest Validate(string question, string optionsJson)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length > 1000)
            throw new ArgumentException("Question must contain 1–1000 characters.", nameof(question));

        var options = JsonSerializer.Deserialize<List<ClarificationOption>>(
            optionsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (options is null || options.Count is < 2 or > 3)
            throw new ArgumentException("Provide 2 or 3 choices; the UI adds the Other option.", nameof(optionsJson));
        if (options.Any(option => string.IsNullOrWhiteSpace(option.Label)
                                  || string.IsNullOrWhiteSpace(option.Value)
                                  || option.Label.Length > 100 || option.Value.Length > 500))
            throw new ArgumentException("Each choice requires a label and value within the allowed length.", nameof(optionsJson));
        if (options.Count(option => option.Recommended) != 1)
            throw new ArgumentException("Exactly one choice must be recommended.", nameof(optionsJson));

        return new ClarificationRequest(question.Trim(), options);
    }
}

public sealed class ClarificationRequiredException(ClarificationRequest clarification)
    : Exception("The assistant needs clarification before it can continue.")
{
    public ClarificationRequest Clarification { get; } = clarification;
}
