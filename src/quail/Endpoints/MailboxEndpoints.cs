using System.Security.Claims;
using Quail.Data;
using Quail.Models;

namespace Quail.Endpoints;

public static class MailboxEndpoints
{
    public record MailboxInfo(int Id, string Name, string? SpecialUse, int MessageCount, int UnreadCount);

    public static RouteGroupBuilder MapMailboxEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/mailboxes").RequireAuthorization();

        group.MapGet("/", async (QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailboxes = (await dataStore.GetMailboxStatsAsync(userId.Value))
                .Select(m => new MailboxInfo(
                    m.Id,
                    m.Name,
                    m.SpecialUse?.ToString(),
                    m.MessageCount,
                    m.UnreadCount))
                .ToList();

            return Results.Ok(mailboxes);
        });

        group.MapPost("/", async (CreateMailboxRequest req, QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            // Validate mailbox name
            var name = req.Name?.Trim() ?? "";
            if (name.Length == 0 || name.Length > 100)
            {
                return Results.BadRequest(new ErrorResponse("Mailbox name must be 1-100 characters"));
            }
            if (name.Any(c => c is '/' or '\\' or '\0'))
            {
                return Results.BadRequest(new ErrorResponse("Mailbox name contains invalid characters"));
            }

            if (await dataStore.MailboxExistsAsync(userId.Value, name))
            {
                return Results.Conflict(new ErrorResponse("Mailbox already exists"));
            }

            var mailbox = new Mailbox { UserId = userId.Value, Name = name };
            await dataStore.InsertMailboxAsync(mailbox);

            return Results.Ok(new MailboxInfo(mailbox.Id, mailbox.Name, null, 0, 0));
        });

        group.MapDelete("/{id:int}", async (int id, QuailDataStore dataStore, ClaimsPrincipal principal) =>
        {
            var userId = GetUserId(principal);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var mailbox = await dataStore.GetMailboxAsync(id, userId.Value);
            if (mailbox is null)
            {
                return Results.NotFound();
            }
            if (mailbox.SpecialUse is not null)
            {
                return Results.BadRequest(new ErrorResponse("Cannot delete system mailboxes"));
            }

            await dataStore.DeleteMailboxAsync(mailbox.Id);
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
