using Microsoft.EntityFrameworkCore;
using MimeKit;
using Quail.Data;
using Quail.Models;
using ZLinq;

namespace Quail.Services;

public class EmailService(QuailDbContext db)
{
    /// <summary>
    /// Atomically allocates the next UID for a mailbox using a SQL UPDATE + RETURNING pattern.
    /// Prevents race conditions when multiple concurrent deliveries target the same mailbox.
    /// </summary>
    public async Task<long> AllocateUidAsync(int mailboxId)
    {
        // Atomic increment: UPDATE returns the pre-increment value
        var result = await db.Database.SqlQuery<long>(
            $"UPDATE Mailboxes SET NextUid = NextUid + 1 WHERE Id = {mailboxId} RETURNING NextUid - 1")
            .ToListAsync();

        return result.First();
    }

    public async Task<Message> StoreMessageAsync(string rawContent, int recipientUserId)
    {
        var message = ParseMessage(rawContent);
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        // Deliver to recipient's INBOX
        var inbox = await db.Mailboxes.FirstOrDefaultAsync(
            m => m.UserId == recipientUserId && m.SpecialUse == SpecialFolder.Inbox);

        if (inbox is not null)
        {
            var uid = await AllocateUidAsync(inbox.Id);
            db.MailboxMessages.Add(new MailboxMessage
            {
                MailboxId = inbox.Id,
                MessageId = message.Id,
                Uid = uid,
                InternalDate = message.InternalDate
            });
            await db.SaveChangesAsync();
        }

        return message;
    }

    public async Task<Message> StoreToSentAsync(string rawContent, int senderUserId)
    {
        var message = ParseMessage(rawContent);
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var sent = await db.Mailboxes.FirstOrDefaultAsync(
            m => m.UserId == senderUserId && m.SpecialUse == SpecialFolder.Sent);

        if (sent is not null)
        {
            var uid = await AllocateUidAsync(sent.Id);
            db.MailboxMessages.Add(new MailboxMessage
            {
                MailboxId = sent.Id,
                MessageId = message.Id,
                Uid = uid,
                Flags = MessageFlags.Seen,
                InternalDate = message.InternalDate
            });
            await db.SaveChangesAsync();
        }

        return message;
    }

    public async Task<(Message message, MailboxMessage mailboxMessage)?> StoreToOutboxAsync(string rawContent, int senderUserId)
    {
        var message = ParseMessage(rawContent);
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        var outbox = await db.Mailboxes.FirstOrDefaultAsync(
            m => m.UserId == senderUserId && m.SpecialUse == SpecialFolder.Outbox);

        if (outbox is null) return null;

        var uid = await AllocateUidAsync(outbox.Id);
        var mm = new MailboxMessage
        {
            MailboxId = outbox.Id,
            MessageId = message.Id,
            Uid = uid,
            InternalDate = message.InternalDate
        };
        db.MailboxMessages.Add(mm);
        await db.SaveChangesAsync();

        return (message, mm);
    }

    public async Task MoveToSentAsync(MailboxMessage mailboxMessage, int senderUserId)
    {
        var sent = await db.Mailboxes.FirstOrDefaultAsync(
            m => m.UserId == senderUserId && m.SpecialUse == SpecialFolder.Sent);

        if (sent is not null)
        {
            var uid = await AllocateUidAsync(sent.Id);
            mailboxMessage.MailboxId = sent.Id;
            mailboxMessage.Uid = uid;
            mailboxMessage.Flags = MessageFlags.Seen;
            await db.SaveChangesAsync();
        }
    }

    public Message ParseMessage(string rawContent)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawContent));
        var mimeMessage = MimeMessage.Load(stream);

        var textBody = mimeMessage.TextBody ?? "";
        var htmlBody = mimeMessage.HtmlBody;
        var preview = textBody.Length > 200 ? textBody[..200] : textBody;

        return new Message
        {
            RawContent = rawContent,
            MessageId = mimeMessage.MessageId ?? Guid.NewGuid().ToString(),
            FromAddress = mimeMessage.From.Mailboxes.AsValueEnumerable().FirstOrDefault()?.Address ?? "unknown@localhost",
            ToAddress = mimeMessage.To.Mailboxes.AsValueEnumerable().Select(m => m.Address).JoinToString(", "),
            CcAddress = GetJoinedAddressesOrNull(mimeMessage.Cc.Mailboxes),
            Subject = mimeMessage.Subject ?? "(no subject)",
            TextBody = textBody,
            HtmlBody = htmlBody,
            BodyPreview = preview,
            InternalDate = mimeMessage.Date != default ? mimeMessage.Date.UtcDateTime : DateTime.UtcNow,
            Size = rawContent.Length
        };
    }

    public async Task<User?> FindUserByEmailAsync(string email)
    {
        return await db.Users.FirstOrDefaultAsync(u => u.Email == email);
    }

    private static string? GetJoinedAddressesOrNull(IEnumerable<MailboxAddress> mailboxes)
    {
        var addresses = mailboxes.AsValueEnumerable().Select(m => m.Address).JoinToString(", ");
        return addresses.Length == 0 ? null : addresses;
    }
}
