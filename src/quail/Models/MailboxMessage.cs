namespace Quail.Models;

public class MailboxMessage
{
    public int Id { get; set; }
    public int MailboxId { get; set; }
    public int MessageId { get; set; }
    public long Uid { get; set; }
    public MessageFlags Flags { get; set; } = MessageFlags.None;
    public DateTime InternalDate { get; set; } = DateTime.UtcNow;

    public Mailbox Mailbox { get; set; } = null!;
    public Message Message { get; set; } = null!;
}

[Flags]
public enum MessageFlags
{
    None = 0,
    Seen = 1,
    Answered = 2,
    Flagged = 4,
    Deleted = 8,
    Draft = 16
}
