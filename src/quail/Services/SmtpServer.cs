using System.Text;
using Microsoft.AspNetCore.Connections;
using Quail.Data;
using Quail.Models;

namespace Quail.Services;

public class SmtpConnectionHandler(IServiceProvider serviceProvider, ILogger<SmtpConnectionHandler> logger) : ConnectionHandler
{
    private const int MaxRecipients = 50;
    private const int MaxDataSizeBytes = 10 * 1024 * 1024; // 10MB
    private const int MaxFailedAuthAttempts = 3;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var ct = connection.ConnectionClosed;
        await using var inputStream = connection.Transport.Input.AsStream();
        await using var outputStream = connection.Transport.Output.AsStream();
        var reader = new StreamReader(inputStream, Encoding.UTF8);
        var writer = new StreamWriter(outputStream, Encoding.UTF8) { AutoFlush = true };

        await writer.WriteLineAsync("220 quail SMTP ready");

        string? mailFrom = null;
        User? authenticatedUser = null;
        var rcptTo = new List<string>();
        var state = SmtpState.Greeting;
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
                await writer.WriteLineAsync("421 Idle timeout, closing connection");
                return;
            }

            if (line is null) break;

            var parts = line.Split(' ', 2);
            var command = parts[0].ToUpperInvariant();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (command)
            {
                case "EHLO" or "HELO":
                    ResetTransaction();
                    state = SmtpState.Ready;
                    await writer.WriteLineAsync($"250-quail Hello");
                    await writer.WriteLineAsync("250-SIZE 10485760");
                    await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                    await writer.WriteLineAsync("250 OK");
                    break;

                case "AUTH":
                    if (state == SmtpState.Greeting)
                    {
                        await writer.WriteLineAsync("503 Send EHLO first");
                        break;
                    }

                    if (state != SmtpState.Ready)
                    {
                        await writer.WriteLineAsync("503 Reset the current transaction first");
                        break;
                    }

                    if (authenticatedUser is not null)
                    {
                        await writer.WriteLineAsync("503 Already authenticated");
                        break;
                    }

                    if (failedAuthAttempts >= MaxFailedAuthAttempts)
                    {
                        await writer.WriteLineAsync("421 Too many failed auth attempts, closing");
                        return;
                    }

                    authenticatedUser = await AuthenticateAsync(arg, reader, writer, ct);
                    if (authenticatedUser is null)
                    {
                        failedAuthAttempts++;
                    }
                    break;

                case "MAIL":
                    if (state == SmtpState.Greeting)
                    {
                        await writer.WriteLineAsync("503 Send EHLO first");
                        break;
                    }

                    if (authenticatedUser is null)
                    {
                        await writer.WriteLineAsync("530 Authentication required");
                        break;
                    }

                    var sender = ExtractAddress(line);
                    if (string.IsNullOrWhiteSpace(sender))
                    {
                        await writer.WriteLineAsync("501 Invalid sender");
                        break;
                    }

                    if (!string.Equals(sender, authenticatedUser.Email, StringComparison.OrdinalIgnoreCase))
                    {
                        await writer.WriteLineAsync("553 Sender address rejected");
                        break;
                    }

                    mailFrom = sender;
                    rcptTo.Clear();
                    state = SmtpState.MailFrom;
                    await writer.WriteLineAsync("250 OK");
                    break;

                case "RCPT":
                    if (authenticatedUser is null)
                    {
                        await writer.WriteLineAsync("530 Authentication required");
                        break;
                    }

                    if (state < SmtpState.MailFrom)
                    {
                        await writer.WriteLineAsync("503 Send MAIL FROM first");
                        break;
                    }

                    if (rcptTo.Count >= MaxRecipients)
                    {
                        await writer.WriteLineAsync("452 Too many recipients");
                        break;
                    }

                    var to = ExtractAddress(line);
                    if (to is not null)
                    {
                        rcptTo.Add(to);
                        state = SmtpState.RcptTo;
                        await writer.WriteLineAsync("250 OK");
                    }
                    else
                    {
                        await writer.WriteLineAsync("550 Invalid recipient");
                    }
                    break;

                case "DATA":
                    if (authenticatedUser is null)
                    {
                        await writer.WriteLineAsync("530 Authentication required");
                        break;
                    }

                    if (state < SmtpState.RcptTo)
                    {
                        await writer.WriteLineAsync("503 Send RCPT TO first");
                        break;
                    }
                    await writer.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>");
                    var (messageData, exceeded) = await ReadDataAsync(reader, ct);
                    if (exceeded)
                    {
                        await writer.WriteLineAsync("552 Message size exceeds limit");
                        ResetTransaction();
                        break;
                    }
                    await DeliverMessageAsync(mailFrom!, rcptTo, messageData);
                    await writer.WriteLineAsync("250 OK message delivered");
                    ResetTransaction();
                    break;

                case "RSET":
                    ResetTransaction();
                    await writer.WriteLineAsync("250 OK");
                    break;

                case "NOOP":
                    await writer.WriteLineAsync("250 OK");
                    break;

                case "QUIT":
                    await writer.WriteLineAsync("221 Bye");
                    return;

                default:
                    await writer.WriteLineAsync("502 Command not implemented");
                    break;
            }
        }

        void ResetTransaction()
        {
            mailFrom = null;
            rcptTo.Clear();
            if (state != SmtpState.Greeting)
                state = SmtpState.Ready;
        }
    }

    private static string? ExtractAddress(string line)
    {
        var start = line.IndexOf('<');
        var end = line.IndexOf('>');
        if (start >= 0 && end > start)
            return line[(start + 1)..end];

        var colonIdx = line.IndexOf(':');
        if (colonIdx >= 0)
            return line[(colonIdx + 1)..].Trim();

        return null;
    }

    private static async Task<(string data, bool exceeded)> ReadDataAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || line == ".") break;

            // Dot-unstuffing
            if (line.StartsWith(".."))
            {
                line = line[1..];
            }

            if (sb.Length + line.Length + 2 > MaxDataSizeBytes)
            {
                // Drain remaining data to keep protocol in sync
                while (true)
                {
                    var drain = await reader.ReadLineAsync(ct);
                    if (drain is null || drain == ".") break;
                }
                return (string.Empty, true);
            }

            sb.AppendLine(line);
        }
        return (sb.ToString(), false);
    }

    private async Task<User?> AuthenticateAsync(string authArguments, StreamReader reader, StreamWriter writer,
        CancellationToken ct)
    {
        var parts = authArguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await writer.WriteLineAsync("501 Syntax: AUTH mechanism [initial-response]");
            return null;
        }

        return parts[0].ToUpperInvariant() switch
        {
            "PLAIN" => await AuthenticatePlainAsync(parts.Length > 1 ? parts[1] : null, reader, writer, ct),
            "LOGIN" => await AuthenticateLoginAsync(parts.Length > 1 ? parts[1] : null, reader, writer, ct),
            _ => await RejectUnsupportedMechanismAsync(writer)
        };
    }

    private async Task<User?> AuthenticatePlainAsync(string? initialResponse, StreamReader reader,
        StreamWriter writer, CancellationToken ct)
    {
        var payload = initialResponse;
        if (string.IsNullOrWhiteSpace(payload))
        {
            await writer.WriteLineAsync("334 ");
            payload = await reader.ReadLineAsync(ct);
        }

        if (payload is null || payload == "*")
        {
            await writer.WriteLineAsync("501 Authentication cancelled");
            return null;
        }

        if (!MailAuthentication.TryDecodePlainCredentials(payload, out var credentials))
        {
            await writer.WriteLineAsync("535 Authentication credentials invalid");
            return null;
        }

        var user = await ValidateCredentialsAsync(credentials.Login, credentials.Password);
        await writer.WriteLineAsync(user is null
            ? "535 Authentication credentials invalid"
            : "235 Authentication successful");
        return user;
    }

    private async Task<User?> AuthenticateLoginAsync(string? initialUsername, StreamReader reader,
        StreamWriter writer, CancellationToken ct)
    {
        var usernameToken = initialUsername;
        if (string.IsNullOrWhiteSpace(usernameToken))
        {
            await writer.WriteLineAsync("334 VXNlcm5hbWU6");
            usernameToken = await reader.ReadLineAsync(ct);
        }

        if (usernameToken is null || usernameToken == "*")
        {
            await writer.WriteLineAsync("501 Authentication cancelled");
            return null;
        }

        if (!MailAuthentication.TryDecodeBase64Token(usernameToken, out var username) ||
            string.IsNullOrWhiteSpace(username))
        {
            await writer.WriteLineAsync("535 Authentication credentials invalid");
            return null;
        }

        await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
        var passwordToken = await reader.ReadLineAsync(ct);
        if (passwordToken is null || passwordToken == "*")
        {
            await writer.WriteLineAsync("501 Authentication cancelled");
            return null;
        }

        if (!MailAuthentication.TryDecodeBase64Token(passwordToken, out var password))
        {
            await writer.WriteLineAsync("535 Authentication credentials invalid");
            return null;
        }

        var user = await ValidateCredentialsAsync(username, password);
        await writer.WriteLineAsync(user is null
            ? "535 Authentication credentials invalid"
            : "235 Authentication successful");
        return user;
    }

    private static async Task<User?> RejectUnsupportedMechanismAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync("504 Unsupported authentication mechanism");
        return null;
    }

    private async Task<User?> ValidateCredentialsAsync(string usernameOrEmail, string password)
    {
        using var scope = serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        return await authService.ValidateCredentialsAsync(usernameOrEmail, password);
    }

    private async Task DeliverMessageAsync(string from, List<string> recipients, string rawContent)
    {
        using var scope = serviceProvider.CreateScope();
        var emailService = scope.ServiceProvider.GetRequiredService<EmailService>();

        foreach (var recipient in recipients)
        {
            var user = await emailService.FindUserByEmailAsync(recipient);
            if (user is not null)
            {
                await emailService.StoreMessageAsync(rawContent, user.Id);
                logger.LogInformation("Delivered message from {From} to {To}", from, recipient);
            }
            else
            {
                logger.LogWarning("Unknown recipient: {Recipient}", recipient);
            }
        }
    }

    private enum SmtpState
    {
        Greeting,
        Ready,
        MailFrom,
        RcptTo
    }
}
