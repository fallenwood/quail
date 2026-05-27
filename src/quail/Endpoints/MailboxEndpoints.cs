using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Quail.Data;
using Quail.Models;

namespace Quail.Endpoints;

public static class MailboxEndpoints
{
    public record MailboxInfo(int Id, string Name, string? SpecialUse, int MessageCount, int UnreadCount);

    public static RouteGroupBuilder MapMailboxEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mailboxes").RequireAuthorization();

        group.MapGet("/", async (QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mailboxes = await db.Mailboxes
                .Where(m => m.UserId == userId)
                .Select(m => new MailboxInfo(
                    m.Id,
                    m.Name,
                    m.SpecialUse != null ? m.SpecialUse.ToString() : null,
                    m.Messages.Count(mm => !mm.Flags.HasFlag(MessageFlags.Deleted)),
                    m.Messages.Count(mm => !mm.Flags.HasFlag(MessageFlags.Seen) && !mm.Flags.HasFlag(MessageFlags.Deleted))))
                .ToListAsync();

            return Results.Ok(mailboxes);
        });

        group.MapPost("/", async (CreateMailboxRequest req, QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            // Validate mailbox name
            var name = req.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 100)
                return Results.BadRequest(new ErrorResponse("Mailbox name must be 1-100 characters"));
            if (name.Any(c => c is '/' or '\\' or '\0'))
                return Results.BadRequest(new ErrorResponse("Mailbox name contains invalid characters"));

            if (await db.Mailboxes.AnyAsync(m => m.UserId == userId && m.Name == name))
                return Results.Conflict(new ErrorResponse("Mailbox already exists"));

            var mailbox = new Mailbox { UserId = userId.Value, Name = name };
            db.Mailboxes.Add(mailbox);
            await db.SaveChangesAsync();

            return Results.Ok(new MailboxInfo(mailbox.Id, mailbox.Name, null, 0, 0));
        });

        group.MapDelete("/{id:int}", async (int id, QuailDbContext db, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null) return Results.Unauthorized();

            var mailbox = await db.Mailboxes.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId);
            if (mailbox is null) return Results.NotFound();
            if (mailbox.SpecialUse is not null)
                return Results.BadRequest(new ErrorResponse("Cannot delete system mailboxes"));

            db.Mailboxes.Remove(mailbox);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        return group;
    }

    public record CreateMailboxRequest(string Name);

    private static int? GetUserId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : null;
    }
}
