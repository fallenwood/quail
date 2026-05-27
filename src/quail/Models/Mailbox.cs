namespace Quail.Models;

public class Mailbox
{
    public int Id { get; set; }
    public Guid Guid { get; set; } = Guid.NewGuid();
    public int UserId { get; set; }
    public required string Name { get; set; }
    public long UidValidity { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long NextUid { get; set; } = 1;
    public SpecialFolder? SpecialUse { get; set; }

    public User User { get; set; } = null!;
    public List<MailboxMessage> Messages { get; set; } = [];
}

public enum SpecialFolder
{
    Inbox,
    Sent,
    Drafts,
    Trash,
    Junk,
    Outbox
}
