namespace Quail.Models;

public class Message
{
    public int Id { get; set; }
    public Guid Guid { get; set; } = Guid.NewGuid();
    public required string RawContent { get; set; }
    public required string MessageId { get; set; }
    public required string FromAddress { get; set; }
    public required string ToAddress { get; set; }
    public string? CcAddress { get; set; }
    public required string Subject { get; set; }
    public string? BodyPreview { get; set; }
    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }
    public DateTime InternalDate { get; set; } = DateTime.UtcNow;
    public long Size { get; set; }

    public List<MailboxMessage> MailboxMessages { get; set; } = [];
}
