using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.EntityFrameworkCore;
using Quail.Data;
using Quail.Models;
using ZLinq;

namespace Quail.Services;

public class Pop3ConnectionHandler(IServiceProvider serviceProvider, ILogger<Pop3ConnectionHandler> logger) : ConnectionHandler
{
    private const int MaxFailedAuthAttempts = 3;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var inputStream = connection.Transport.Input.AsStream();
        await using var outputStream = connection.Transport.Output.AsStream();
        var reader = new StreamReader(inputStream, Encoding.UTF8);
        var writer = new StreamWriter(outputStream, Encoding.UTF8) { AutoFlush = true };

        await writer.WriteLineAsync("+OK quail POP3 server ready");

        string? username = null;
        User? user = null;
        List<MailboxMessage>? messages = null;
        var deletedUids = new HashSet<long>();
        var state = Pop3State.Authorization;
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
                await writer.WriteLineAsync("-ERR Idle timeout, closing connection");
                return;
            }

            if (line is null) break;

            var parts = line.Split(' ', 2);
            var command = parts[0].ToUpperInvariant();
            var arg = parts.Length > 1 ? parts[1] : "";

            switch (command)
            {
                case "USER":
                    username = arg;
                    await writer.WriteLineAsync("+OK");
                    break;

                case "PASS":
                    if (username is null)
                    {
                        await writer.WriteLineAsync("-ERR Send USER first");
                        break;
                    }
                    if (failedAuthAttempts >= MaxFailedAuthAttempts)
                    {
                        await writer.WriteLineAsync("-ERR Too many failed auth attempts, closing");
                        return;
                    }
                    user = await AuthenticateAsync(username, arg);
                    if (user is null)
                    {
                        failedAuthAttempts++;
                        await writer.WriteLineAsync("-ERR Invalid credentials");
                        break;
                    }
                    messages = await GetInboxMessagesAsync(user.Id);
                    state = Pop3State.Transaction;
                    await writer.WriteLineAsync($"+OK {messages.Count} messages");
                    break;

                case "STAT":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    var activeMessages = messages!.AsValueEnumerable().Where(m => !deletedUids.Contains(m.Uid)).ToList();
                    var totalSize = activeMessages.AsValueEnumerable().Sum(m => m.Message.Size);
                    await writer.WriteLineAsync($"+OK {activeMessages.Count} {totalSize}");
                    break;

                case "LIST":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out var listIdx))
                    {
                        if (listIdx < 1 || listIdx > messages!.Count)
                        {
                            await writer.WriteLineAsync("-ERR No such message");
                        }
                        else
                        {
                            var msg = messages[listIdx - 1];
                            await writer.WriteLineAsync($"+OK {listIdx} {msg.Message.Size}");
                        }
                    }
                    else
                    {
                        var active = messages!.AsValueEnumerable().Where(m => !deletedUids.Contains(m.Uid)).ToList();
                        await writer.WriteLineAsync($"+OK {active.Count} messages");
                        for (int i = 0; i < messages!.Count; i++)
                        {
                            if (!deletedUids.Contains(messages[i].Uid))
                                await writer.WriteLineAsync($"{i + 1} {messages[i].Message.Size}");
                        }
                        await writer.WriteLineAsync(".");
                    }
                    break;

                case "UIDL":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out var uidlIdx))
                    {
                        if (uidlIdx < 1 || uidlIdx > messages!.Count)
                        {
                            await writer.WriteLineAsync("-ERR No such message");
                        }
                        else
                        {
                            await writer.WriteLineAsync($"+OK {uidlIdx} {messages[uidlIdx - 1].Uid}");
                        }
                    }
                    else
                    {
                        await writer.WriteLineAsync("+OK");
                        for (int i = 0; i < messages!.Count; i++)
                        {
                            if (!deletedUids.Contains(messages[i].Uid))
                                await writer.WriteLineAsync($"{i + 1} {messages[i].Uid}");
                        }
                        await writer.WriteLineAsync(".");
                    }
                    break;

                case "RETR":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    if (int.TryParse(arg, out var retrIdx) && retrIdx >= 1 && retrIdx <= messages!.Count)
                    {
                        var msg = messages[retrIdx - 1];
                        var content = msg.Message.RawContent;
                        await writer.WriteLineAsync($"+OK {content.Length} octets");
                        foreach (var msgLine in content.Split('\n'))
                        {
                            var trimmed = msgLine.TrimEnd('\r');
                            if (trimmed.StartsWith('.'))
                                await writer.WriteAsync(".");
                            await writer.WriteLineAsync(trimmed);
                        }
                        await writer.WriteLineAsync(".");
                    }
                    else
                    {
                        await writer.WriteLineAsync("-ERR No such message");
                    }
                    break;

                case "TOP":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    var topParts = arg.Split(' ', 2);
                    if (topParts.Length == 2 && int.TryParse(topParts[0], out var topIdx) && int.TryParse(topParts[1], out var lines))
                    {
                        if (topIdx >= 1 && topIdx <= messages!.Count)
                        {
                            var msg = messages[topIdx - 1];
                            var allLines = msg.Message.RawContent.Split('\n');
                            var headerEnd = Array.FindIndex(allLines, l => l.Trim() == "");
                            await writer.WriteLineAsync("+OK");
                            for (int i = 0; i <= headerEnd && i < allLines.Length; i++)
                                await writer.WriteLineAsync(allLines[i].TrimEnd('\r'));
                            for (int i = headerEnd + 1; i < Math.Min(headerEnd + 1 + lines, allLines.Length); i++)
                                await writer.WriteLineAsync(allLines[i].TrimEnd('\r'));
                            await writer.WriteLineAsync(".");
                        }
                        else
                        {
                            await writer.WriteLineAsync("-ERR No such message");
                        }
                    }
                    else
                    {
                        await writer.WriteLineAsync("-ERR Invalid arguments");
                    }
                    break;

                case "DELE":
                    if (state != Pop3State.Transaction)
                    {
                        await writer.WriteLineAsync("-ERR Not authenticated");
                        break;
                    }
                    if (int.TryParse(arg, out var deleIdx) && deleIdx >= 1 && deleIdx <= messages!.Count)
                    {
                        deletedUids.Add(messages[deleIdx - 1].Uid);
                        await writer.WriteLineAsync("+OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("-ERR No such message");
                    }
                    break;

                case "RSET":
                    deletedUids.Clear();
                    await writer.WriteLineAsync("+OK");
                    break;

                case "NOOP":
                    await writer.WriteLineAsync("+OK");
                    break;

                case "QUIT":
                    if (state == Pop3State.Transaction && deletedUids.Count > 0)
                    {
                        await CommitDeletionsAsync(messages!, deletedUids);
                    }
                    await writer.WriteLineAsync("+OK Bye");
                    return;

                default:
                    await writer.WriteLineAsync("-ERR Unknown command");
                    break;
            }
        }
    }

    private async Task<User?> AuthenticateAsync(string username, string password)
    {
        using var scope = serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        return await authService.ValidateCredentialsAsync(username, password);
    }

    private async Task<List<MailboxMessage>> GetInboxMessagesAsync(int userId)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        return await db.MailboxMessages
            .Include(mm => mm.Message)
            .Include(mm => mm.Mailbox)
            .Where(mm => mm.Mailbox.UserId == userId && mm.Mailbox.SpecialUse == SpecialFolder.Inbox)
            .Where(mm => !mm.Flags.HasFlag(MessageFlags.Deleted))
            .OrderBy(mm => mm.Uid)
            .ToListAsync();
    }

    private async Task CommitDeletionsAsync(List<MailboxMessage> messages, HashSet<long> deletedUids)
    {
        using var scope = serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<QuailDbContext>();
        var toDelete = messages.AsValueEnumerable().Where(m => deletedUids.Contains(m.Uid)).Select(m => m.Id).ToList();
        await db.MailboxMessages.Where(mm => toDelete.Contains(mm.Id)).ExecuteDeleteAsync();
    }

    private enum Pop3State
    {
        Authorization,
        Transaction
    }
}
