using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.EntityFrameworkCore;
using Quail.Data;
using Quail.Models;
using ZLinq;

namespace Quail.Services;

public class ImapConnectionHandler(IServiceProvider serviceProvider, ILogger<ImapConnectionHandler> logger) : ConnectionHandler
{
    private const int MaxFailedAuthAttempts = 3;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var inputStream = connection.Transport.Input.AsStream();
        await using var outputStream = connection.Transport.Output.AsStream();
        var reader = new StreamReader(inputStream, Encoding.UTF8);
        var writer = new StreamWriter(outputStream, Encoding.UTF8) { AutoFlush = true };

        await writer.WriteLineAsync("* OK quail IMAP4rev1 server ready");

        User? user = null;
        Mailbox? selectedMailbox = null;
        List<MailboxMessage>? selectedMessages = null;
        int failedAuthAttempts = 0;

        while (!ct.IsCancellationRequested)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(IdleTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await writer.WriteLineAsync("* BYE Idle timeout, closing connection");
                return;
            }

            if (line is null) break;

            // IMAP commands are: tag SP command SP arguments
            var spaceIdx = line.IndexOf(' ');
            if (spaceIdx < 0)
            {
                await writer.WriteLineAsync("* BAD Invalid command");
                continue;
            }

            var tag = line[..spaceIdx];
            var rest = line[(spaceIdx + 1)..];
            var cmdSpaceIdx = rest.IndexOf(' ');
            var command = (cmdSpaceIdx >= 0 ? rest[..cmdSpaceIdx] : rest).ToUpperInvariant();
            var args = cmdSpaceIdx >= 0 ? rest[(cmdSpaceIdx + 1)..] : "";

            switch (command)
            {
                case "CAPABILITY":
                    await writer.WriteLineAsync(user is null
                        ? "* CAPABILITY IMAP4rev1 AUTH=PLAIN"
                        : "* CAPABILITY IMAP4rev1");
                    await writer.WriteLineAsync($"{tag} OK CAPABILITY completed");
                    break;

                case "NOOP":
                    await writer.WriteLineAsync($"{tag} OK NOOP completed");
                    break;

                case "AUTHENTICATE":
                    if (user is not null)
                    {
                        await writer.WriteLineAsync($"{tag} BAD Already authenticated");
                        break;
                    }

                    if (failedAuthAttempts >= MaxFailedAuthAttempts)
                    {
                        await writer.WriteLineAsync($"* BYE Too many failed auth attempts");
                        return;
                    }

                    user = await AuthenticatePlainAsync(tag, args, reader, writer, ct);
                    if (user is null)
                    {
                        failedAuthAttempts++;
                    }
                    break;

                case "LOGIN":
                    if (failedAuthAttempts >= MaxFailedAuthAttempts)
                    {
                        await writer.WriteLineAsync($"* BYE Too many failed auth attempts");
                        return;
                    }

                    var loginParts = ParseLoginArgs(args);
                    if (loginParts is null)
                    {
                        await writer.WriteLineAsync($"{tag} BAD Invalid arguments");
                        break;
                    }
                    user = await AuthenticateAsync(loginParts.Value.username, loginParts.Value.password);
                    if (user is null)
                    {
                        failedAuthAttempts++;
                        await writer.WriteLineAsync($"{tag} NO LOGIN failed");
                    }
                    else
                    {
                        await writer.WriteLineAsync($"{tag} OK LOGIN completed");
                    }
                    break;

                case "LIST":
                    if (user is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    var mailboxes = await GetMailboxesAsync(user.Id);
                    foreach (var mb in mailboxes)
                    {
                        var attrs = mb.SpecialUse switch
                        {
                            SpecialFolder.Inbox => "\\HasNoChildren",
                            SpecialFolder.Sent => "\\Sent \\HasNoChildren",
                            SpecialFolder.Drafts => "\\Drafts \\HasNoChildren",
                            SpecialFolder.Trash => "\\Trash \\HasNoChildren",
                            SpecialFolder.Junk => "\\Junk \\HasNoChildren",
                            _ => "\\HasNoChildren"
                        };
                        await writer.WriteLineAsync($"* LIST ({attrs}) \"/\" \"{mb.Name}\"");
                    }
                    await writer.WriteLineAsync($"{tag} OK LIST completed");
                    break;

                case "LSUB":
                    if (user is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    var subMailboxes = await GetMailboxesAsync(user.Id);
                    foreach (var mb in subMailboxes)
                    {
                        await writer.WriteLineAsync($"* LSUB () \"/\" \"{mb.Name}\"");
                    }
                    await writer.WriteLineAsync($"{tag} OK LSUB completed");
                    break;

                case "SELECT" or "EXAMINE":
                    if (user is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    var mbName = args.Trim('"');
                    selectedMailbox = await GetMailboxByNameAsync(user.Id, mbName);
                    if (selectedMailbox is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Mailbox not found");
                        break;
                    }
                    selectedMessages = await GetMailboxMessagesAsync(selectedMailbox.Id);
                    var exists = selectedMessages.Count;
                    var recent = selectedMessages.AsValueEnumerable().Count(m => !m.Flags.HasFlag(MessageFlags.Seen));
                    await writer.WriteLineAsync($"* {exists} EXISTS");
                    await writer.WriteLineAsync($"* {recent} RECENT");
                    await writer.WriteLineAsync($"* OK [UIDVALIDITY {selectedMailbox.UidValidity}]");
                    await writer.WriteLineAsync($"* OK [UIDNEXT {selectedMailbox.NextUid}]");
                    await writer.WriteLineAsync("* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)");
                    await writer.WriteLineAsync("* OK [PERMANENTFLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft \\*)]");
                    var readOnly = command == "EXAMINE" ? "[READ-ONLY]" : "[READ-WRITE]";
                    await writer.WriteLineAsync($"{tag} OK {readOnly} SELECT completed");
                    break;

                case "STATUS":
                    if (user is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Not authenticated");
                        break;
                    }
                    var statusArgs = args.Split(' ', 2);
                    var statusMbName = statusArgs[0].Trim('"');
                    var statusMb = await GetMailboxByNameAsync(user.Id, statusMbName);
                    if (statusMb is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO Mailbox not found");
                        break;
                    }
                    var statusMsgs = await GetMailboxMessagesAsync(statusMb.Id);
                    var unseen = statusMsgs.AsValueEnumerable().Count(m => !m.Flags.HasFlag(MessageFlags.Seen));
                    await writer.WriteLineAsync($"* STATUS \"{statusMbName}\" (MESSAGES {statusMsgs.Count} UNSEEN {unseen} UIDNEXT {statusMb.NextUid} UIDVALIDITY {statusMb.UidValidity})");
                    await writer.WriteLineAsync($"{tag} OK STATUS completed");
                    break;

                case "FETCH":
                    if (selectedMessages is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleFetchAsync(writer, tag, args, selectedMessages, false);
                    break;

                case "UID":
                    if (selectedMessages is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    var uidCmdIdx = args.IndexOf(' ');
                    var uidCmd = (uidCmdIdx >= 0 ? args[..uidCmdIdx] : args).ToUpperInvariant();
                    var uidArgs = uidCmdIdx >= 0 ? args[(uidCmdIdx + 1)..] : "";
                    switch (uidCmd)
                    {
                        case "FETCH":
                            await HandleFetchAsync(writer, tag, uidArgs, selectedMessages, true);
                            break;
                        case "STORE":
                            await HandleStoreAsync(writer, tag, uidArgs, selectedMessages, true);
                            break;
                        case "SEARCH":
                            await HandleSearchAsync(writer, tag, uidArgs, selectedMessages, true);
                            break;
                        default:
                            await writer.WriteLineAsync($"{tag} BAD Unknown UID command");
                            break;
                    }
                    break;

                case "STORE":
                    if (selectedMessages is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleStoreAsync(writer, tag, args, selectedMessages, false);
                    break;

                case "SEARCH":
                    if (selectedMessages is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    await HandleSearchAsync(writer, tag, args, selectedMessages, false);
                    break;

                case "EXPUNGE":
                    if (selectedMailbox is null || selectedMessages is null)
                    {
                        await writer.WriteLineAsync($"{tag} NO No mailbox selected");
                        break;
                    }
                    var expunged = new List<int>();
                    for (int i = selectedMessages.Count - 1; i >= 0; i--)
                    {
                        if (selectedMessages[i].Flags.HasFlag(MessageFlags.Deleted))
                        {
                            expunged.Add(i + 1);
                        }
                    }
                    if (expunged.Count > 0)
                    {
                        await ExpungeMessagesAsync(selectedMessages.AsValueEnumerable().Where(m => m.Flags.HasFlag(MessageFlags.Deleted)).ToList());
                        expunged.Reverse();
                        foreach (var seq in expunged)
                            await writer.WriteLineAsync($"* {seq} EXPUNGE");
                        selectedMessages.RemoveAll(m => m.Flags.HasFlag(MessageFlags.Deleted));
                    }
                    await writer.WriteLineAsync($"{tag} OK EXPUNGE completed");
                    break;

                case "CLOSE":
                    if (selectedMessages is not null)
                    {
                        var toExpunge = selectedMessages.AsValueEnumerable().Where(m => m.Flags.HasFlag(MessageFlags.Deleted)).ToList();
                        if (toExpunge.Count > 0)
                            await ExpungeMessagesAsync(toExpunge);
                    }
                    selectedMailbox = null;
                    selectedMessages = null;
                    await writer.WriteLineAsync($"{tag} OK CLOSE completed");
                    break;

                case "LOGOUT":
                    await writer.WriteLineAsync("* BYE quail IMAP server signing off");
                    await writer.WriteLineAsync($"{tag} OK LOGOUT completed");
                    return;

                default:
                    await writer.WriteLineAsync($"{tag} BAD Unknown command");
                    break;
            }
        }
    }

    private async Task HandleFetchAsync(StreamWriter writer, string tag, string args,
        List<MailboxMessage> messages, bool useUid)
    {
        var (range, items) = ParseFetchArgs(args);
        var targets = ResolveRange(range, messages, useUid);

        foreach (var (seqNum, mm) in targets)
        {
            var response = BuildFetchResponse(seqNum, mm, items, useUid);
            await writer.WriteLineAsync(response);
        }
        await writer.WriteLineAsync($"{tag} OK FETCH completed");
    }

    private async Task HandleStoreAsync(StreamWriter writer, string tag, string args,
        List<MailboxMessage> messages, bool useUid)
    {
        var parts = args.Split(' ', 3);
        if (parts.Length < 3)
        {
            await writer.WriteLineAsync($"{tag} BAD Invalid STORE arguments");
            return;
        }

        var range = parts[0];
        var action = parts[1].ToUpperInvariant();
        var flagsStr = parts[2];
        var flags = ParseFlags(flagsStr);
        var targets = ResolveRange(range, messages, useUid);

        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();

        foreach (var (seqNum, mm) in targets)
        {
            if (action.Contains("+FLAGS"))
                mm.Flags |= flags;
            else if (action.Contains("-FLAGS"))
                mm.Flags &= ~flags;
            else if (action.Contains("FLAGS"))
                mm.Flags = flags;

            db.Attach(mm);
            db.Entry(mm).Property(x => x.Flags).IsModified = true;

            if (!action.Contains(".SILENT"))
            {
                var flagStr = FormatFlags(mm.Flags);
                var uidPart = useUid ? $" UID {mm.Uid}" : "";
                await writer.WriteLineAsync($"* {seqNum} FETCH (FLAGS ({flagStr}){uidPart})");
            }
        }
        await db.SaveChangesAsync();
        await writer.WriteLineAsync($"{tag} OK STORE completed");
    }

    private async Task HandleSearchAsync(StreamWriter writer, string tag, string args,
        List<MailboxMessage> messages, bool useUid)
    {
        // Simple search implementation: ALL, UNSEEN, SEEN, DELETED
        var criteria = args.ToUpperInvariant().Trim();
        var results = new List<string>();

        for (int i = 0; i < messages.Count; i++)
        {
            var mm = messages[i];
            var matches = criteria switch
            {
                "ALL" => true,
                "UNSEEN" => !mm.Flags.HasFlag(MessageFlags.Seen),
                "SEEN" => mm.Flags.HasFlag(MessageFlags.Seen),
                "DELETED" => mm.Flags.HasFlag(MessageFlags.Deleted),
                "FLAGGED" => mm.Flags.HasFlag(MessageFlags.Flagged),
                _ => true
            };

            if (matches)
                results.Add(useUid ? mm.Uid.ToString() : (i + 1).ToString());
        }

        await writer.WriteLineAsync($"* SEARCH {string.Join(' ', results)}");
        await writer.WriteLineAsync($"{tag} OK SEARCH completed");
    }

    private string BuildFetchResponse(int seqNum, MailboxMessage mm, string items, bool useUid)
    {
        var parts = new List<string>();
        var itemsUpper = items.ToUpperInvariant();

        if (itemsUpper.Contains("FLAGS"))
            parts.Add($"FLAGS ({FormatFlags(mm.Flags)})");

        if (itemsUpper.Contains("UID") || useUid)
            parts.Add($"UID {mm.Uid}");

        if (itemsUpper.Contains("INTERNALDATE"))
            parts.Add($"INTERNALDATE \"{mm.InternalDate:dd-MMM-yyyy HH:mm:ss} +0000\"");

        if (itemsUpper.Contains("RFC822.SIZE"))
            parts.Add($"RFC822.SIZE {mm.Message.Size}");

        if (itemsUpper.Contains("ENVELOPE"))
        {
            var msg = mm.Message;
            parts.Add($"ENVELOPE (NIL \"{EscapeQuotes(msg.Subject)}\" " +
                      $"((\"{EscapeQuotes(msg.FromAddress)}\" NIL \"\" \"\")) " +
                      $"((\"{EscapeQuotes(msg.FromAddress)}\" NIL \"\" \"\")) " +
                      $"((\"{EscapeQuotes(msg.FromAddress)}\" NIL \"\" \"\")) " +
                      $"((\"{EscapeQuotes(msg.ToAddress)}\" NIL \"\" \"\")) " +
                      $"NIL NIL NIL \"<{EscapeQuotes(msg.MessageId)}>\")");
        }

        if (itemsUpper.Contains("BODY[]") || itemsUpper.Contains("RFC822"))
        {
            var content = mm.Message.RawContent;
            parts.Add($"BODY[] {{{content.Length}}}\r\n{content}");
        }
        else if (itemsUpper.Contains("BODY[HEADER]"))
        {
            var raw = mm.Message.RawContent;
            var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) headerEnd = raw.IndexOf("\n\n", StringComparison.Ordinal);
            var headers = headerEnd >= 0 ? raw[..(headerEnd + 2)] : raw;
            parts.Add($"BODY[HEADER] {{{headers.Length}}}\r\n{headers}");
        }
        else if (itemsUpper.Contains("BODY[TEXT]"))
        {
            var raw = mm.Message.RawContent;
            var headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0) headerEnd = raw.IndexOf("\n\n", StringComparison.Ordinal);
            var body = headerEnd >= 0 ? raw[(headerEnd + 4)..] : "";
            parts.Add($"BODY[TEXT] {{{body.Length}}}\r\n{body}");
        }
        else if (itemsUpper.Contains("BODYSTRUCTURE"))
        {
            parts.Add("BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" " +
                      $"{mm.Message.Size} {mm.Message.RawContent.Split('\n').Length})");
        }

        return $"* {seqNum} FETCH ({string.Join(' ', parts)})";
    }

    private static List<(int seqNum, MailboxMessage mm)> ResolveRange(string range,
        List<MailboxMessage> messages, bool useUid)
    {
        var results = new List<(int, MailboxMessage)>();
        int? lastUid = null;
        foreach (var part in range.Split(','))
        {
            if (part.Contains(':'))
            {
                var bounds = part.Split(':');
                var start = bounds[0] == "*" ? (useUid ? lastUid ??= (int)messages.AsValueEnumerable().Last().Uid : messages.Count) : int.Parse(bounds[0]);
                var end = bounds[1] == "*" ? (useUid ? lastUid ??= (int)messages.AsValueEnumerable().Last().Uid : messages.Count) : int.Parse(bounds[1]);
                if (start > end) (start, end) = (end, start);

                for (int i = 0; i < messages.Count; i++)
                {
                    var val = useUid ? (int)messages[i].Uid : i + 1;
                    if (val >= start && val <= end)
                        results.Add((i + 1, messages[i]));
                }
            }
            else
            {
                var num = part == "*" ? (useUid ? lastUid ??= (int)messages.AsValueEnumerable().Last().Uid : messages.Count) : int.Parse(part);
                if (useUid)
                {
                    for (int i = 0; i < messages.Count; i++)
                    {
                        if ((int)messages[i].Uid == num)
                        {
                            results.Add((i + 1, messages[i]));
                            break;
                        }
                    }
                }
                else if (num >= 1 && num <= messages.Count)
                {
                    results.Add((num, messages[num - 1]));
                }
            }
        }
        return results;
    }

    private static (string range, string items) ParseFetchArgs(string args)
    {
        var spaceIdx = args.IndexOf(' ');
        if (spaceIdx < 0) return (args, "");
        return (args[..spaceIdx], args[(spaceIdx + 1)..].Trim('(', ')'));
    }

    private static MessageFlags ParseFlags(string flagsStr)
    {
        var flags = MessageFlags.None;
        var upper = flagsStr.ToUpperInvariant();
        if (upper.Contains("\\SEEN")) flags |= MessageFlags.Seen;
        if (upper.Contains("\\ANSWERED")) flags |= MessageFlags.Answered;
        if (upper.Contains("\\FLAGGED")) flags |= MessageFlags.Flagged;
        if (upper.Contains("\\DELETED")) flags |= MessageFlags.Deleted;
        if (upper.Contains("\\DRAFT")) flags |= MessageFlags.Draft;
        return flags;
    }

    private static string FormatFlags(MessageFlags flags)
    {
        var parts = new List<string>();
        if (flags.HasFlag(MessageFlags.Seen)) parts.Add("\\Seen");
        if (flags.HasFlag(MessageFlags.Answered)) parts.Add("\\Answered");
        if (flags.HasFlag(MessageFlags.Flagged)) parts.Add("\\Flagged");
        if (flags.HasFlag(MessageFlags.Deleted)) parts.Add("\\Deleted");
        if (flags.HasFlag(MessageFlags.Draft)) parts.Add("\\Draft");
        return string.Join(' ', parts);
    }

    private static (string username, string password)? ParseLoginArgs(string args)
    {
        // LOGIN "username" "password" or LOGIN username password
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;

        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) parts.Add(current.ToString());

        return parts.Count >= 2 ? (parts[0], parts[1]) : null;
    }

    private static string EscapeQuotes(string s) => s.Replace("\"", "\\\"");

    private async Task<User?> AuthenticatePlainAsync(string tag, string args, StreamReader reader,
        StreamWriter writer, CancellationToken ct)
    {
        var authParts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (authParts.Length == 0 || authParts[0].ToUpperInvariant() != "PLAIN")
        {
            await writer.WriteLineAsync($"{tag} NO Unsupported authentication mechanism");
            return null;
        }

        var payload = authParts.Length > 1 ? authParts[1] : null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            await writer.WriteLineAsync("+");
            payload = await reader.ReadLineAsync(ct);
        }

        if (payload is null || payload == "*")
        {
            await writer.WriteLineAsync($"{tag} BAD AUTHENTICATE cancelled");
            return null;
        }

        if (!MailAuthentication.TryDecodePlainCredentials(payload, out var credentials))
        {
            await writer.WriteLineAsync($"{tag} NO AUTHENTICATE failed");
            return null;
        }

        var user = await AuthenticateAsync(credentials.Login, credentials.Password);
        if (user is null)
        {
            await writer.WriteLineAsync($"{tag} NO AUTHENTICATE failed");
            return null;
        }

        await writer.WriteLineAsync($"{tag} OK AUTHENTICATE completed");
        return user;
    }

    private async Task<User?> AuthenticateAsync(string username, string password)
    {
        using var scope = serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        return await authService.ValidateCredentialsAsync(username, password);
    }

    private async Task<List<Mailbox>> GetMailboxesAsync(int userId)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        return await db.Mailboxes.Where(m => m.UserId == userId).ToListAsync();
    }

    private async Task<Mailbox?> GetMailboxByNameAsync(int userId, string name)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        return await db.Mailboxes.FirstOrDefaultAsync(
            m => m.UserId == userId && m.Name.ToUpper() == name.ToUpper());
    }

    private async Task<List<MailboxMessage>> GetMailboxMessagesAsync(int mailboxId)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        return await db.MailboxMessages
            .Include(mm => mm.Message)
            .Where(mm => mm.MailboxId == mailboxId)
            .OrderBy(mm => mm.Uid)
            .ToListAsync();
    }

    private async Task ExpungeMessagesAsync(List<MailboxMessage> toDelete)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        var ids = toDelete.AsValueEnumerable().Select(m => m.Id).ToList();
        await db.MailboxMessages.Where(mm => ids.Contains(mm.Id)).ExecuteDeleteAsync();
    }
}
