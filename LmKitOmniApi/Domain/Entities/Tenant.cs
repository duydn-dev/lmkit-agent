namespace LmKitOmniApi.Domain.Entities;

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Quan hệ 1-N với ChatSessions
    public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
}
