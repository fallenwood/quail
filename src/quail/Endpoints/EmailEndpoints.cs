using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        group.MapGet("/", async (QuailDbContext db, ClaimsPrincipal principal, [FromQuery] int? mailboxId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            // Clamp pagination parameters
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = db.MailboxMessages
                .Include(mm => mm.Message)
                .Include(mm => mm.Mailbox)
                .Where(mm => mm.Mailbox.UserId == userId);

            if (mailboxId.HasValue)
                query = query.Where(mm => mm.MailboxId == mailboxId.Value);
            else
                query = query.Where(mm => mm.Mailbox.SpecialUse == SpecialFolder.Inbox);

            var total = await query.CountAsync();
            var messages = await query
                .OrderByDescending(mm => mm.InternalDate)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
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
                .ToListAsync();

            return Results.Ok(new EmailListResponse(total, page, pageSize, messages));
        });

        group.MapGet("/{id:int}", async (int id, QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();
            var mm = await db.MailboxMessages
                .Include(m => m.Message)
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();

            // Mark as read
            if (!mm.Flags.HasFlag(MessageFlags.Seen))
            {
                mm.Flags |= MessageFlags.Seen;
                await db.SaveChangesAsync();
            }

            return Results.Ok(new EmailDetail(
                mm.MessageId, mm.Uid, mm.Message.FromAddress, mm.Message.ToAddress,
                mm.Message.CcAddress, mm.Message.Subject, mm.Message.TextBody,
                mm.Message.HtmlBody, mm.InternalDate, true));
        });

        group.MapPost("/", async (ComposeRequest req, QuailDbContext db, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var user = await db.Users.FindAsync(userId.Value);
            if (user is null) return Results.Unauthorized();

            // Input length validation
            if (string.IsNullOrWhiteSpace(req.To))
                return Results.BadRequest(new ErrorResponse("To field is required"));
            if (req.Subject.Length > 500)
                return Results.BadRequest(new ErrorResponse("Subject too long (max 500 characters)"));
            if (req.Body.Length > 1_000_000)
                return Results.BadRequest(new ErrorResponse("Body too long (max 1MB)"));

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
                return Results.BadRequest(new ErrorResponse("Too many recipients (max 50)"));

            // Validate all recipients exist — single batched query
            var recipientUsers = await db.Users
                .Where(u => allRecipients.Contains(u.Email))
                .ToListAsync();

            var foundEmails = recipientUsers.Select(u => u.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var invalidAddresses = allRecipients.Where(a => !foundEmails.Contains(a)).ToList();

            if (invalidAddresses.Count > 0)
            {
                var addressList = string.Join(", ", invalidAddresses);
                return Results.BadRequest(new ErrorResponse($"Recipient(s) not found: {addressList}"));
            }

            // Build MIME message (with Bcc for sender's copy)
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(user.Username, user.Email));

            try
            {
                foreach (var to in toAddresses)
                    message.To.Add(MailboxAddress.Parse(to));

                foreach (var cc in ccAddresses)
                    message.Cc.Add(MailboxAddress.Parse(cc));

                foreach (var bcc in bccAddresses)
                    message.Bcc.Add(MailboxAddress.Parse(bcc));
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
                return Results.StatusCode(500);

            // Deliver to each recipient using batched user lookup results
            foreach (var recipient in recipientUsers)
            {
                await emailService.StoreMessageAsync(recipientRawContent, recipient.Id);
            }

            // Move from Outbox to Sent after successful delivery
            await emailService.MoveToSentAsync(outboxResult.Value.mailboxMessage, userId.Value);

            return Results.Ok(new MessageResponse("Sent"));
        }).RequireRateLimiting("send");

        group.MapPut("/{id:int}/read", async (int id, QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mm = await db.MailboxMessages
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();
            mm.Flags |= MessageFlags.Seen;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPut("/{id:int}/unread", async (int id, QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mm = await db.MailboxMessages
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();
            mm.Flags &= ~MessageFlags.Seen;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPut("/{id:int}/move", async (int id, MoveRequest req, QuailDbContext db, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mm = await db.MailboxMessages
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();

            var targetMailbox = await db.Mailboxes.FirstOrDefaultAsync(
                m => m.Id == req.TargetMailboxId && m.UserId == userId);
            if (targetMailbox is null) return Results.BadRequest(new ErrorResponse("Target mailbox not found"));

            mm.MailboxId = targetMailbox.Id;
            mm.Uid = await emailService.AllocateUidAsync(targetMailbox.Id);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        group.MapPut("/{id:int}/restore", async (int id, QuailDbContext db, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mm = await db.MailboxMessages
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();

            if (mm.Mailbox.SpecialUse != SpecialFolder.Trash)
                return Results.BadRequest(new ErrorResponse("Email is not in Trash"));

            var inbox = await db.Mailboxes.FirstOrDefaultAsync(
                m => m.UserId == userId && m.SpecialUse == SpecialFolder.Inbox);
            if (inbox is null) return Results.NotFound();

            mm.MailboxId = inbox.Id;
            mm.Uid = await emailService.AllocateUidAsync(inbox.Id);
            mm.Flags &= ~MessageFlags.Deleted;
            await db.SaveChangesAsync();
            return Results.Ok(new MessageResponse("Restored"));
        });

        group.MapDelete("/{id:int}", async (int id, QuailDbContext db, ClaimsPrincipal principal, EmailService emailService) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mm = await db.MailboxMessages
                .Include(m => m.Mailbox)
                .FirstOrDefaultAsync(m => m.MessageId == id && m.Mailbox.UserId == userId);

            if (mm is null) return Results.NotFound();

            // Move to trash or hard delete if already in trash
            var trash = await db.Mailboxes.FirstOrDefaultAsync(
                m => m.UserId == userId && m.SpecialUse == SpecialFolder.Trash);

            if (mm.Mailbox.SpecialUse == SpecialFolder.Trash)
            {
                db.MailboxMessages.Remove(mm);
            }
            else if (trash is not null)
            {
                mm.MailboxId = trash.Id;
                mm.Uid = await emailService.AllocateUidAsync(trash.Id);
            }

            await db.SaveChangesAsync();
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
