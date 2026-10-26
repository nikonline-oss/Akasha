namespace Akasha.Core.Models;

/// <summary>Владелец хранилища (всегда одна запись, но таблица для гибкости).</summary>
public class Owner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }   // argon2id
    public string? DisplayName { get; set; }
    public string? Timezone { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
