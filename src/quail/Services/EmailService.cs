using MimeKit;
using Quail.Data;
using Quail.Models;
using ZLinq;

namespace Quail.Services;

public class EmailService(QuailDataStore dataStore)
{
    /// <summary>
    /// Atomically allocates the next UID for a mailbox using a SQL UPDATE + RETURNING pattern.
    /// Prevents race conditions when multiple concurrent deliveries target the same mailbox.
    /// </summary>
    public async Task<long> AllocateUidAsync(int mailboxId)
    {
        return await dataStore.AllocateUidAsync(mailboxId);
    }

    public async Task<Message> StoreMessageAsync(string rawContent, int recipientUserId)
    {
        var message = ParseMessage(rawContent);
        await dataStore.InsertMessageAsync(message);

        // Deliver to recipient's INBOX
        var inbox = await dataStore.GetMailboxBySpecialUseAsync(recipientUserId, SpecialFolder.Inbox);

        if (inbox is not null)
        {
            var uid = await AllocateUidAsync(inbox.Id);
            var mailboxMessage = new MailboxMessage
            {
                MailboxId = inbox.Id,
                MessageId = message.Id,
                Uid = uid,
                InternalDate = message.InternalDate
            };
            await dataStore.InsertMailboxMessageAsync(mailboxMessage);
        }

        return message;
    }

    public async Task<Message> StoreToSentAsync(string rawContent, int senderUserId)
    {
        var message = ParseMessage(rawContent);
        await dataStore.InsertMessageAsync(message);

        var sent = await dataStore.GetMailboxBySpecialUseAsync(senderUserId, SpecialFolder.Sent);

        if (sent is not null)
        {
            var uid = await AllocateUidAsync(sent.Id);
            var mailboxMessage = new MailboxMessage
            {
                MailboxId = sent.Id,
                MessageId = message.Id,
                Uid = uid,
                Flags = MessageFlags.Seen,
                InternalDate = message.InternalDate
            };
            await dataStore.InsertMailboxMessageAsync(mailboxMessage);
        }

        return message;
    }

    public async Task<(Message message, MailboxMessage mailboxMessage)?> StoreToOutboxAsync(string rawContent, int senderUserId)
    {
        var message = ParseMessage(rawContent);
        await dataStore.InsertMessageAsync(message);

        var outbox = await dataStore.GetMailboxBySpecialUseAsync(senderUserId, SpecialFolder.Outbox);

        if (outbox is null)
        {
            return null;
        }

        var uid = await AllocateUidAsync(outbox.Id);
        var mm = new MailboxMessage
        {
            MailboxId = outbox.Id,
            MessageId = message.Id,
            Uid = uid,
            InternalDate = message.InternalDate
        };
        await dataStore.InsertMailboxMessageAsync(mm);

        return (message, mm);
    }

    public async Task MoveToSentAsync(MailboxMessage mailboxMessage, int senderUserId)
    {
        var sent = await dataStore.GetMailboxBySpecialUseAsync(senderUserId, SpecialFolder.Sent);

        if (sent is not null)
        {
            var uid = await AllocateUidAsync(sent.Id);
            mailboxMessage.MailboxId = sent.Id;
            mailboxMessage.Uid = uid;
            mailboxMessage.Flags = MessageFlags.Seen;
            await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
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
        return await dataStore.GetUserByEmailAsync(email);
    }

    private static string? GetJoinedAddressesOrNull(IEnumerable<MailboxAddress> mailboxes)
    {
        var addresses = mailboxes.AsValueEnumerable().Select(m => m.Address).JoinToString(", ");
        return addresses.Length == 0 ? null : addresses;
    }
}
