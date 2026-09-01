using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.Data;

public class HermesDbContext : DbContext
{
    public DbSet<Tenant> Tenants { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<ChatSession> ChatSessions { get; set; } = null!;
    public DbSet<ChatMessage> ChatMessages { get; set; } = null!;
    public DbSet<AuditLog> AuditLogs { get; set; } = null!;
    public DbSet<Document> Documents { get; set; } = null!;
    public DbSet<DocumentChunk> DocumentChunks { get; set; } = null!;
    
    // New Entities
    public DbSet<AgentMemory> AgentMemories { get; set; } = null!;
    public DbSet<CanvasArtifact> CanvasArtifacts { get; set; } = null!;
    public DbSet<ChatShareLink> ChatShareLinks { get; set; } = null!;
    public DbSet<CustomAgent> CustomAgents { get; set; } = null!;
    public DbSet<Project> Projects { get; set; } = null!;
    public DbSet<ScheduledTask> ScheduledTasks { get; set; } = null!;
    public DbSet<ExternalMcpServer> ExternalMcpServers { get; set; } = null!;
    public DbSet<GraphEntity> GraphEntities { get; set; } = null!;
    public DbSet<GraphRelationship> GraphRelationships { get; set; } = null!;
    public DbSet<Notification> Notifications { get; set; } = null!;
    public DbSet<TaskApproval> TaskApprovals { get; set; } = null!;
    public DbSet<TenantApiCryptoKey> TenantApiCryptoKeys { get; set; } = null!;
    public DbSet<TenantApiKey> TenantApiKeys { get; set; } = null!;
    public DbSet<TenantWidgetSettings> TenantWidgetSettings { get; set; } = null!;
    public DbSet<UserSession> UserSessions { get; set; } = null!;
    public DbSet<AgentRun> AgentRuns { get; set; } = null!;
    public DbSet<AgentRunStep> AgentRunSteps { get; set; } = null!;
    public DbSet<DatabaseConnection> DatabaseConnections { get; set; } = null!;

    public HermesDbContext(DbContextOptions<HermesDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Config Entity Relationships
        modelBuilder.Entity<Tenant>().HasKey(t => t.Id);

        modelBuilder.Entity<User>()
            .HasOne(u => u.Tenant)
            .WithMany()
            .HasForeignKey(u => u.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Document>()
            .HasOne(d => d.User)
            .WithMany(u => u.Documents)
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Document>()
            .HasIndex(d => new { d.VectorizationStatus, d.ProcessingLeaseUntilUtc, d.UploadedAt });

        modelBuilder.Entity<DocumentChunk>()
            .HasOne(c => c.Document)
            .WithMany(d => d.Chunks)
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatSession>().HasKey(s => s.Id);

        modelBuilder.Entity<ChatSession>()
            .HasOne(s => s.Tenant)
            .WithMany(t => t.ChatSessions)
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatSession>()
            .HasOne(s => s.User)
            .WithMany(u => u.ChatSessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ChatMessage>().HasKey(m => m.Id);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.ChatSession)
            .WithMany(s => s.Messages)
            .HasForeignKey(m => m.ChatSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CustomAgent>()
            .HasOne(a => a.Tenant).WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomAgent>()
            .HasOne(a => a.OwnerUser).WithMany().HasForeignKey(a => a.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CustomAgent>()
            .HasIndex(a => new { a.TenantId, a.OwnerUserId });
        modelBuilder.Entity<CustomAgent>()
            .HasIndex(a => new { a.TenantId, a.IsSharedWithTenant });

        // An agent deletion must not break its sessions; they fall back to the default assistant.
        modelBuilder.Entity<ChatSession>()
            .HasOne(s => s.CustomAgent)
            .WithMany()
            .HasForeignKey(s => s.CustomAgentId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Project>()
            .HasOne(p => p.Tenant).WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Project>()
            .HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Project>()
            .HasIndex(p => new { p.TenantId, p.UserId });

        // A project deletion keeps its sessions; they just leave the project.
        modelBuilder.Entity<ChatSession>()
            .HasOne(s => s.Project)
            .WithMany()
            .HasForeignKey(s => s.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // API keys are looked up by hash on every request — must be unique and indexed.
        modelBuilder.Entity<TenantApiKey>()
            .HasIndex(k => k.ApiKey)
            .IsUnique();
        modelBuilder.Entity<TenantApiKey>()
            .HasIndex(k => new { k.TenantId, k.UserId });

        modelBuilder.Entity<CanvasArtifact>()
            .HasOne(c => c.Tenant).WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CanvasArtifact>()
            .HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CanvasArtifact>()
            .HasOne(c => c.ChatSession).WithMany().HasForeignKey(c => c.ChatSessionId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<CanvasArtifact>()
            .HasIndex(c => new { c.TenantId, c.UserId, c.RootId, c.Version });

        modelBuilder.Entity<ScheduledTask>()
            .HasOne(t => t.Tenant).WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ScheduledTask>()
            .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ScheduledTask>()
            .HasIndex(t => new { t.Enabled, t.NextRunUtc });
        modelBuilder.Entity<ScheduledTask>()
            .HasIndex(t => new { t.TenantId, t.UserId });

        modelBuilder.Entity<Notification>()
            .HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAtUtc });

        modelBuilder.Entity<ChatShareLink>()
            .HasOne(l => l.ChatSession)
            .WithMany()
            .HasForeignKey(l => l.ChatSessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ChatShareLink>()
            .HasIndex(l => l.TokenHash)
            .IsUnique();
        modelBuilder.Entity<ChatShareLink>()
            .HasIndex(l => l.ChatSessionId);

        // New Entity Relationships
        modelBuilder.Entity<AgentMemory>()
            .HasOne(a => a.Tenant).WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AgentMemory>()
            .HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<AuditLog>()
            .HasOne(a => a.ActorUser).WithMany().HasForeignKey(a => a.ActorUserId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<AuditLog>()
            .HasIndex(a => new { a.TenantId, a.CreatedAtUtc });

        modelBuilder.Entity<ExternalMcpServer>()
            .HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<GraphEntity>()
            .HasOne(g => g.Tenant).WithMany().HasForeignKey(g => g.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GraphEntity>()
            .HasOne(g => g.Document).WithMany().HasForeignKey(g => g.DocumentId).OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<GraphRelationship>()
            .HasOne(r => r.Tenant).WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GraphRelationship>()
            .HasOne(r => r.SourceEntity).WithMany(g => g.SourceRelationships).HasForeignKey(r => r.SourceEntityId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GraphRelationship>()
            .HasOne(r => r.TargetEntity).WithMany(g => g.TargetRelationships).HasForeignKey(r => r.TargetEntityId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Notification>()
            .HasOne(n => n.Tenant).WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Notification>()
            .HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TaskApproval>()
            .HasOne(t => t.Tenant).WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TaskApproval>()
            .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TaskApproval>()
            .HasOne(t => t.ChatSession).WithMany().HasForeignKey(t => t.ChatSessionId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TenantApiCryptoKey>()
            .HasOne(t => t.Tenant).WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TenantApiKey>()
            .HasOne(t => t.Tenant).WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TenantApiKey>()
            .HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TenantWidgetSettings>()
            .HasOne(t => t.Tenant).WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserSession>()
            .HasOne(u => u.User).WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserSession>()
            .HasIndex(session => session.SessionKey)
            .IsUnique();
        modelBuilder.Entity<UserSession>()
            .HasIndex(session => session.RefreshTokenHash)
            .IsUnique();

        modelBuilder.Entity<AgentRun>()
            .HasOne<ChatSession>().WithMany().HasForeignKey(r => r.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AgentRun>()
            .HasIndex(r => new { r.TenantId, r.UserId, r.CreatedAtUtc });
        modelBuilder.Entity<AgentRunStep>()
            .HasOne(s => s.AgentRun).WithMany(r => r.Steps).HasForeignKey(s => s.AgentRunId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AgentRunStep>()
            .HasIndex(s => new { s.AgentRunId, s.Ordinal });

        modelBuilder.Entity<DatabaseConnection>()
            .HasOne(c => c.Tenant).WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<DatabaseConnection>()
            .HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
    }
}
