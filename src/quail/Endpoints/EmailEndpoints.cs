using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using MimeKit;
using Quail.Data;
using Quail.Models;
using Quail.Services;

namespace Quail.Endpoints;

public static class EmailEndpoints
{
    public record EmailSummary(int Id, long Uid, string From, string To, string Subject, string? Preview, DateTime Date, bool IsRead, bool IsFlagged);
    public record EmailDetail(int Id, long Uid, string From, string To, string? Cc, string Subject, string? TextBody, string? HtmlBody, DateTime Date, bool IsRead);
    public record ComposeRequest(string To, string Subject, string Body, string? Cc = null, string? Bcc = null, bool IsHtml = false);
    public record MoveRequest(int TargetMailboxId);

    public static RouteGroupBuilder MapEmailEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/emails").RequireAuthorization();

        group.MapGet("/", async (QuailDataStore dataStore, ClaimsPrincipal principal, [FromQuery] int? mailboxId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            // Clamp pagination parameters
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var result = await dataStore.GetEmailPageAsync(userId.Value, mailboxId, page, pageSize);
            var messages = result.Items
                .Select(mm => new EmailSummary(
                    mm.MessageId,
                    mm.Uid,
                    mm.Message.FromAddress,
                    mm.Message.ToAddress,
                    mm.Message.Subject,
                    mm.Message.BodyPreview,
                    mm.InternalDate,
                    mm.Flags.HasFlag(MessageFlags.Seen),
                    mm.Flags.HasFlag(MessageFlags.Flagged)))
                .ToList();

            return Results.Ok(new EmailListResponse(result.TotalCount, page, pageSize, messages));
        });

        group.MapGet("/{id:int}", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            // Mark as read
            if (!mailboxMessage.Flags.HasFlag(MessageFlags.Seen))
            {
                mailboxMessage.Flags |= MessageFlags.Seen;
                await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            }

            return Results.Ok(new EmailDetail(
                mailboxMessage.MessageId,
                mailboxMessage.Uid,
                mailboxMessage.Message.FromAddress,
                mailboxMessage.Message.ToAddress,
                mailboxMessage.Message.CcAddress,
                mailboxMessage.Message.Subject,
                mailboxMessage.Message.TextBody,
                mailboxMessage.Message.HtmlBody,
                mailboxMessage.InternalDate,
                true));
        });

        group.MapPost("/", async (ComposeRequest req, QuailDataStore dataStore, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var user = await dataStore.GetUserByIdAsync(userId.Value);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            // Input length validation
            if (string.IsNullOrWhiteSpace(req.To))
            {
                return Results.BadRequest(new ErrorResponse("To field is required"));
            }
            if (req.Subject.Length > 500)
            {
                return Results.BadRequest(new ErrorResponse("Subject too long (max 500 characters)"));
            }
            if (req.Body.Length > 1_000_000)
            {
                return Results.BadRequest(new ErrorResponse("Body too long (max 1MB)"));
            }

            // Parse all recipient addresses
            var toAddresses = req.To.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var ccAddresses = string.IsNullOrWhiteSpace(req.Cc)
                ? Array.Empty<string>()
                : req.Cc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var bccAddresses = string.IsNullOrWhiteSpace(req.Bcc)
                ? Array.Empty<string>()
                : req.Bcc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            var allRecipients = toAddresses.Concat(ccAddresses).Concat(bccAddresses).Distinct().ToList();

            // Cap recipient count to prevent abuse
            if (allRecipients.Count > 50)
            {
                return Results.BadRequest(new ErrorResponse("Too many recipients (max 50)"));
            }

            var recipientUsers = await dataStore.GetUsersByEmailsAsync(allRecipients);

            // Build MIME message (with Bcc for sender's copy)
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(user.Username, user.Email));

            try
            {
                foreach (var to in toAddresses)
                {
                    message.To.Add(MailboxAddress.Parse(to));
                }

                foreach (var cc in ccAddresses)
                {
                    message.Cc.Add(MailboxAddress.Parse(cc));
                }

                foreach (var bcc in bccAddresses)
                {
                    message.Bcc.Add(MailboxAddress.Parse(bcc));
                }
            }
            catch (FormatException)
            {
                return Results.BadRequest(new ErrorResponse("One or more email addresses have invalid format"));
            }

            message.Subject = req.Subject;
            message.Body = req.IsHtml
                ? new TextPart("html") { Text = req.Body }
                : new TextPart("plain") { Text = req.Body };

            // Serialize sender's copy (includes Bcc header) for Outbox/Sent
            using var senderStream = new MemoryStream();
            await message.WriteToAsync(senderStream);
            var senderRawContent = System.Text.Encoding.UTF8.GetString(senderStream.ToArray());

            // Remove Bcc before serializing recipient copy
            message.Bcc.Clear();
            using var recipientStream = new MemoryStream();
            await message.WriteToAsync(recipientStream);
            var recipientRawContent = System.Text.Encoding.UTF8.GetString(recipientStream.ToArray());

            // Store in Outbox (sender's copy with Bcc)
            var outboxResult = await emailService.StoreToOutboxAsync(senderRawContent, userId.Value);
            if (outboxResult is null)
            {
                return Results.StatusCode(500);
            }

            // Deliver to each recipient using batched user lookup results
            foreach (var recipient in recipientUsers)
            {
                await emailService.StoreMessageAsync(recipientRawContent, recipient.Id);
            }

            // Move from Outbox to Sent after successful delivery
            await emailService.MoveToSentAsync(outboxResult.Value.mailboxMessage, userId.Value);

            return Results.Ok(new MessageResponse("Sent"));
        }).RequireRateLimiting("send");

        group.MapPut("/{id:int}/read", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            mailboxMessage.Flags |= MessageFlags.Seen;
            await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            return Results.Ok();
        });

        group.MapPut("/{id:int}/unread", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            mailboxMessage.Flags &= ~MessageFlags.Seen;
            await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            return Results.Ok();
        });

        group.MapPut("/{id:int}/move", async (int id, MoveRequest req, QuailDataStore dataStore, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            var targetMailbox = await dataStore.GetMailboxAsync(req.TargetMailboxId, userId.Value);
            if (targetMailbox is null)
            {
                return Results.BadRequest(new ErrorResponse("Target mailbox not found"));
            }

            mailboxMessage.MailboxId = targetMailbox.Id;
            mailboxMessage.Uid = await emailService.AllocateUidAsync(targetMailbox.Id);
            await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            return Results.Ok();
        });

        group.MapPut("/{id:int}/restore", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            if (mailboxMessage.Mailbox.SpecialUse != SpecialFolder.Trash)
            {
                return Results.BadRequest(new ErrorResponse("Email is not in Trash"));
            }

            var inbox = await dataStore.GetMailboxBySpecialUseAsync(userId.Value, SpecialFolder.Inbox);
            if (inbox is null)
            {
                return Results.NotFound();
            }

            mailboxMessage.MailboxId = inbox.Id;
            mailboxMessage.Uid = await emailService.AllocateUidAsync(inbox.Id);
            mailboxMessage.Flags &= ~MessageFlags.Deleted;
            await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            return Results.Ok(new MessageResponse("Restored"));
        });

        group.MapDelete("/{id:int}", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxMessage = await dataStore.GetMailboxMessageByMessageIdAsync(id, userId.Value);
            if (mailboxMessage is null)
            {
                return Results.NotFound();
            }

            // Move to trash or hard delete if already in trash
            var trash = await dataStore.GetMailboxBySpecialUseAsync(userId.Value, SpecialFolder.Trash);

            if (mailboxMessage.Mailbox.SpecialUse == SpecialFolder.Trash)
            {
                await dataStore.DeleteMailboxMessageAsync(mailboxMessage.Id);
            }
            else if (trash is not null)
            {
                mailboxMessage.MailboxId = trash.Id;
                mailboxMessage.Uid = await emailService.AllocateUidAsync(trash.Id);
                await dataStore.UpdateMailboxMessageAsync(mailboxMessage);
            }

            return Results.Ok();
        });

        return group;
    }

    private static int? GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : null;
    }
}
