using System.Text.Json;
using LmKitOmniApi.Application.AgentRuns.Queries;
using LmKitOmniApi.Infrastructure.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.AgentRuns.Handlers;

public sealed class GetAgentRunQueryHandler : IRequestHandler<GetAgentRunQuery, AgentRunDetailDto?>
{
    private readonly HermesDbContext _dbContext;

    public GetAgentRunQueryHandler(HermesDbContext dbContext) => _dbContext = dbContext;

    public async Task<AgentRunDetailDto?> Handle(GetAgentRunQuery request, CancellationToken cancellationToken)
    {
        return await _dbContext.AgentRuns
            .AsNoTracking()
            .Where(run => run.Id == request.RunId
                && run.TenantId == request.TenantId
                && run.UserId == request.UserId)
            .Select(run => new AgentRunDetailDto
            {
                Id = run.Id,
                Goal = run.Goal,
                Status = run.Status,
                Result = run.Result,
                Error = run.Error,
                CreatedAtUtc = run.CreatedAtUtc,
                CompletedAtUtc = run.CompletedAtUtc,
                Steps = run.Steps
                    .OrderBy(step => step.Ordinal)
                    .Select(step => new AgentRunStepDto
                    {
                        Ordinal = step.Ordinal,
                        Action = step.Action,
                        Input = step.Input,
                        Observation = step.Observation,
                        CreatedAtUtc = step.CreatedAtUtc
                    })
                    .ToList(),
                ProducedFiles = ParseProducedFiles(run.ProducedFilesJson),
                WebSources = ParseWebSources(run.WebSourcesJson)
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Deserializes the persisted [FILE:] descriptors; malformed/legacy JSON
    /// degrades to an empty list — never a failed request.</summary>
    private static List<ProducedFileDto> ParseProducedFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new ProducedFileDto
                {
                    Id = element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString() ?? string.Empty : string.Empty,
                    Name = element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? string.Empty : string.Empty,
                    ContentType = element.TryGetProperty("contentType", out var contentType) && contentType.ValueKind == JsonValueKind.String
                        ? contentType.GetString() ?? string.Empty : string.Empty,
                    Size = element.TryGetProperty("size", out var size) && size.TryGetInt64(out var value)
                        ? value : 0
                })
                .Where(file => file.Id.Length > 0)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Deserializes the persisted [WEB_SEARCH] URLs; malformed/legacy JSON
    /// degrades to an empty list — never a failed request.</summary>
    private static List<string> ParseWebSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
