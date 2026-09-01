namespace LmKitOmniApi.Application.Schedules.Commands;

/// <summary>
/// JSON-bound request body for POST/PUT <c>/api/schedules</c>. Property names/casing are the
/// wire contract shared with the frontend and must not change:
/// <c>{ name, prompt, scheduleKind, intervalMinutes?, timeOfDayMinutes?, dayOfWeek? }</c>.
/// </summary>
public sealed class SaveScheduledTaskRequest
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string ScheduleKind { get; set; } = string.Empty;
    public int? IntervalMinutes { get; set; }
    public int? TimeOfDayMinutes { get; set; }
    public int? DayOfWeek { get; set; }
}

/// <summary>
/// Shared payload for the create/update commands so both handlers run the exact same
/// validation (<see cref="ScheduledTaskRules.Validate"/>). TenantId/UserId are always set by
/// the controller from claims — never from the request body.
/// </summary>
public abstract class SaveScheduledTaskCommandBase
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string ScheduleKind { get; set; } = string.Empty;
    public int? IntervalMinutes { get; set; }
    public int? TimeOfDayMinutes { get; set; }
    public int? DayOfWeek { get; set; }
}

/// <summary>
/// Outcome the controller maps back onto the HTTP contract:
/// ValidationFailed → 400 <c>{ message }</c>, NotFound → empty 404 (owner-scoped, never 403),
/// Success → 201 with <see cref="Task"/> (create) / 204 (update, delete, toggle).
/// </summary>
public enum ScheduledTaskMutationStatus
{
    Success,
    NotFound,
    ValidationFailed
}

public sealed class SaveScheduledTaskResult
{
    public ScheduledTaskMutationStatus Status { get; init; }

    /// <summary>Vietnamese message serialized as <c>{ message }</c> on 400 responses.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Populated on successful create; serialized as the 201 body.</summary>
    public ScheduledTaskDto? Task { get; init; }

    public static SaveScheduledTaskResult Success(ScheduledTaskDto? task = null) =>
        new() { Status = ScheduledTaskMutationStatus.Success, Task = task };

    public static SaveScheduledTaskResult ValidationFailed(string message) =>
        new() { Status = ScheduledTaskMutationStatus.ValidationFailed, ErrorMessage = message };

    public static SaveScheduledTaskResult NotFound() =>
        new() { Status = ScheduledTaskMutationStatus.NotFound };
}

/// <summary>
/// Wire DTO for GET <c>/api/schedules</c> rows and the POST 201 body. Serialized camelCase:
/// <c>{ id, name, prompt, scheduleKind, intervalMinutes, timeOfDayMinutes, dayOfWeek, enabled,
/// nextRunUtc, lastRunUtc, lastStatus, lastError }</c>.
/// </summary>
public sealed class ScheduledTaskDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string ScheduleKind { get; init; } = string.Empty;
    public int? IntervalMinutes { get; init; }
    public int? TimeOfDayMinutes { get; init; }
    public int? DayOfWeek { get; init; }
    public bool Enabled { get; init; }
    public DateTime NextRunUtc { get; init; }
    public DateTime? LastRunUtc { get; init; }
    public string? LastStatus { get; init; }
    public string? LastError { get; init; }
}
