using LmKitOmniApi.Application.DatabaseConnections.Queries;
using LmKitOmniApi.Infrastructure.AI.Database;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.DatabaseConnections.Handlers;

/// <summary>
/// Builds the schema diagram for one connection. Everything safety-relevant is inherited
/// from the existing paths rather than re-implemented: the connection is resolved with the
/// same tenant scoping as the rest of the admin API (tenant row OR system-wide row, so an
/// unknown id looks exactly like a non-existent one) and the schema is read through
/// <see cref="ExternalDatabaseService"/> — egress-vetted, read-only, timeout-capped.
/// The secret is unprotect-ed in memory only and never leaves this handler.
/// </summary>
public sealed class GetDatabaseSchemaQueryHandler : IRequestHandler<GetDatabaseSchemaQuery, DatabaseSchemaResult>
{
    private readonly HermesDbContext _dbContext;
    private readonly DbConnectionSecretProtector _protector;
    private readonly ExternalDatabaseService _databases;
    private readonly MongoDatabaseService _mongo;
    private readonly ILogger<GetDatabaseSchemaQueryHandler> _logger;

    public GetDatabaseSchemaQueryHandler(
        HermesDbContext dbContext,
        DbConnectionSecretProtector protector,
        ExternalDatabaseService databases,
        MongoDatabaseService mongo,
        ILogger<GetDatabaseSchemaQueryHandler> logger)
    {
        _dbContext = dbContext;
        _protector = protector;
        _databases = databases;
        _mongo = mongo;
        _logger = logger;
    }

    public async Task<DatabaseSchemaResult> Handle(GetDatabaseSchemaQuery request, CancellationToken ct)
    {
        var entity = await _dbContext.DatabaseConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == request.Id && (c.TenantId == request.TenantId || c.TenantId == null), ct);
        if (entity is null) return DatabaseSchemaResult.Missing();

        var isMongo = MongoDatabaseService.Handles(entity.Provider);
        DbProvider provider = default;
        if (!isMongo && !_databases.TryParseProvider(entity.Provider, out provider))
            return DatabaseSchemaResult.Fail("Loại cơ sở dữ liệu không được hỗ trợ.");

        try
        {
            var connectionString = _protector.Unprotect(entity.ConnectionStringProtected);
            var tables = isMongo
                // MongoDB is schemaless and never Qdrant-indexed: fields are sampled live.
                ? await _mongo.GetCollectionsSchemaAsync(connectionString, SchemaDiagramBuilder.DefaultMaxTables, ct)
                : await _databases.IntrospectAsync(provider, connectionString, ct);

            // Deterministic order BEFORE the cap, so the same connection always yields the
            // same diagram — otherwise which tables survive the cap would drift per request.
            var ordered = tables
                .OrderBy(SchemaDiagramBuilder.Qualify, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var diagram = SchemaDiagramBuilder.Build(ordered);

            return DatabaseSchemaResult.Ok(new DatabaseSchemaDto
            {
                ConnectionId = entity.Id,
                Name = entity.Name,
                Provider = entity.Provider,
                IsActive = entity.IsActive,
                IsIndexed = entity.IsIndexed,
                IndexStatus = entity.IndexStatus,
                LastIndexedAtUtc = entity.LastIndexedAtUtc,
                TableCount = diagram.Tables.Count,
                TotalTableCount = diagram.TotalTableCount,
                Truncated = diagram.Truncated,
                Tables = diagram.Tables,
                Relations = diagram.Relations
            });
        }
        catch (DatabaseOperationRefusedException ex)
        {
            // Egress denial (internal host, blocked range…) — a policy refusal, not a bug.
            return DatabaseSchemaResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema introspection for the diagram failed for connection {ConnectionId}.", entity.Id);
            var message = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
            return DatabaseSchemaResult.Fail($"Không đọc được schema: {message}");
        }
    }
}
