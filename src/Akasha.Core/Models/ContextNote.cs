namespace Akasha.Core.Models;

/// <summary>
/// Запись «контекста агента» — рабочая область, куда AI-агенты пишут напрямую, без апрувов.
/// Отделена от основного хранилища (<c>entries</c>), чтобы не подменять проверенные знания.
/// </summary>
public class ContextNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AgentId { get; set; }           // null = запись владельца
    public Guid? ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
