using global::Dapper;
using global::Microsoft.Data.Sqlite;
using Quail.Models;

namespace Quail.Data;

public sealed class QuailDataStore(IConfiguration configuration)
{
    private readonly string connectionString = BuildConnectionString(configuration);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);

        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");

        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER NOT NULL CONSTRAINT PK_Users PRIMARY KEY AUTOINCREMENT,
                Guid TEXT NOT NULL,
                Username TEXT NOT NULL,
                Email TEXT NOT NULL,
                PasswordHash TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER NOT NULL CONSTRAINT PK_Messages PRIMARY KEY AUTOINCREMENT,
                Guid TEXT NOT NULL,
                RawContent TEXT NOT NULL,
                MessageId TEXT NOT NULL,
                FromAddress TEXT NOT NULL,
                ToAddress TEXT NOT NULL,
                CcAddress TEXT NULL,
                Subject TEXT NOT NULL,
                BodyPreview TEXT NULL,
                HtmlBody TEXT NULL,
                TextBody TEXT NULL,
                InternalDate TEXT NOT NULL,
                Size INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Mailboxes (
                Id INTEGER NOT NULL CONSTRAINT PK_Mailboxes PRIMARY KEY AUTOINCREMENT,
                Guid TEXT NOT NULL,
                UserId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                UidValidity INTEGER NOT NULL,
                NextUid INTEGER NOT NULL,
                SpecialUse INTEGER NULL,
                CONSTRAINT FK_Mailboxes_Users_UserId
                    FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS MailboxMessages (
                Id INTEGER NOT NULL CONSTRAINT PK_MailboxMessages PRIMARY KEY AUTOINCREMENT,
                MailboxId INTEGER NOT NULL,
                MessageId INTEGER NOT NULL,
                Uid INTEGER NOT NULL,
                Flags INTEGER NOT NULL,
                InternalDate TEXT NOT NULL,
                CONSTRAINT FK_MailboxMessages_Mailboxes_MailboxId
                    FOREIGN KEY (MailboxId) REFERENCES Mailboxes (Id) ON DELETE CASCADE,
                CONSTRAINT FK_MailboxMessages_Messages_MessageId
                    FOREIGN KEY (MessageId) REFERENCES Messages (Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Guid ON Users (Guid);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users (Username);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Email ON Users (Email);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_Guid ON Messages (Guid);
            CREATE INDEX IF NOT EXISTS IX_Messages_MessageId ON Messages (MessageId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Mailboxes_Guid ON Mailboxes (Guid);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Mailboxes_UserId_Name ON Mailboxes (UserId, Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_MailboxMessages_MailboxId_Uid ON MailboxMessages (MailboxId, Uid);
            CREATE INDEX IF NOT EXISTS IX_MailboxMessages_MailboxId_Flags ON MailboxMessages (MailboxId, Flags);
            CREATE INDEX IF NOT EXISTS IX_MailboxMessages_MessageId ON MailboxMessages (MessageId);
            """);
    }

    public async Task<bool> UserExistsAsync(string username, string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var exists = await connection.QuerySingleAsync<int>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM Users
                WHERE Username = @Username OR Email = @Email
            );
            """,
            new
            {
                Username = username,
                Email = email
            });
        return exists != 0;
    }

    public async Task InsertUserAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var userId = await connection.QuerySingleOrDefaultAsync<int?>(
            """
            INSERT INTO Users (Guid, Username, Email, PasswordHash, CreatedAt)
            VALUES (@Guid, @Username, @Email, @PasswordHash, @CreatedAt)
            RETURNING Id;
            """,
            new
            {
                Guid = user.Guid.ToString(),
                Username = user.Username,
                Email = user.Email,
                PasswordHash = user.PasswordHash,
                CreatedAt = user.CreatedAt
            })
            ?? throw new InvalidOperationException("Failed to insert user");
        user.Id = userId;
    }

    public async Task<User?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT Id, Guid, Username, Email, PasswordHash, CreatedAt
            FROM Users
            WHERE Id = @UserId
            LIMIT 1;
            """,
            new
            {
                UserId = userId
            });
        return row is null ? null : ToUser(row);
    }

    public async Task<User?> GetUserByLoginAsync(string login, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT Id, Guid, Username, Email, PasswordHash, CreatedAt
            FROM Users
            WHERE Username = @Login OR Email = @Login
            LIMIT 1;
            """,
            new
            {
                Login = login
            });
        return row is null ? null : ToUser(row);
    }

    public async Task<User?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
            """
            SELECT Id, Guid, Username, Email, PasswordHash, CreatedAt
            FROM Users
            WHERE Email = @Email
            LIMIT 1;
            """,
            new
            {
                Email = email
            });
        return row is null ? null : ToUser(row);
    }

    public async Task<List<User>> GetUsersByEmailsAsync(IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        if (emails.Count == 0)
        {
            return [];
        }

        var distinctEmails = emails.Distinct(StringComparer.Ordinal).ToArray();

        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var users = new List<User>(distinctEmails.Length);
        foreach (var distinctEmail in distinctEmails)
        {
            var row = await connection.QuerySingleOrDefaultAsync<UserRow>(
                """
                SELECT Id, Guid, Username, Email, PasswordHash, CreatedAt
                FROM Users
                WHERE Email = @Email
                LIMIT 1;
                """,
                new
                {
                    Email = distinctEmail
                });

            if (row is not null)
            {
                users.Add(ToUser(row));
            }
        }

        return users;
    }

    public async Task InsertMailboxAsync(Mailbox mailbox, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var mailboxId = await connection.QuerySingleOrDefaultAsync<int?>(
            """
            INSERT INTO Mailboxes (Guid, UserId, Name, UidValidity, NextUid, SpecialUse)
            VALUES (@Guid, @UserId, @Name, @UidValidity, @NextUid, @SpecialUse)
            RETURNING Id;
            """,
            new
            {
                Guid = mailbox.Guid.ToString(),
                UserId = mailbox.UserId,
                Name = mailbox.Name,
                UidValidity = mailbox.UidValidity,
                NextUid = mailbox.NextUid,
                SpecialUse = ToDbSpecialUse(mailbox.SpecialUse)
            })
            ?? throw new InvalidOperationException("Failed to insert mailbox");
        mailbox.Id = mailboxId;
    }

    public async Task<bool> MailboxExistsAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var exists = await connection.QuerySingleAsync<int>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM Mailboxes
                WHERE UserId = @UserId AND Name = @Name
            );
            """,
            new
            {
                UserId = userId,
                Name = name
            });
        return exists != 0;
    }

    public async Task<Mailbox?> GetMailboxAsync(int mailboxId, int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MailboxRow>(
            """
            SELECT Id, Guid, UserId, Name, UidValidity, NextUid, SpecialUse
            FROM Mailboxes
            WHERE Id = @MailboxId AND UserId = @UserId
            LIMIT 1;
            """,
            new
            {
                MailboxId = mailboxId,
                UserId = userId
            });
        return row is null ? null : ToMailbox(row);
    }

    public async Task<Mailbox?> GetMailboxBySpecialUseAsync(int userId, SpecialFolder specialUse, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MailboxRow>(
            """
            SELECT Id, Guid, UserId, Name, UidValidity, NextUid, SpecialUse
            FROM Mailboxes
            WHERE UserId = @UserId AND SpecialUse = @SpecialUse
            LIMIT 1;
            """,
            new
            {
                UserId = userId,
                SpecialUse = (int)specialUse
            });
        return row is null ? null : ToMailbox(row);
    }

    public async Task<Mailbox?> GetMailboxByNameAsync(int userId, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MailboxRow>(
            """
            SELECT Id, Guid, UserId, Name, UidValidity, NextUid, SpecialUse
            FROM Mailboxes
            WHERE UserId = @UserId AND Name = @Name COLLATE NOCASE
            LIMIT 1;
            """,
            new
            {
                UserId = userId,
                Name = name
            });
        return row is null ? null : ToMailbox(row);
    }

    public async Task<List<Mailbox>> GetMailboxesAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MailboxRow>(
            """
            SELECT Id, Guid, UserId, Name, UidValidity, NextUid, SpecialUse
            FROM Mailboxes
            WHERE UserId = @UserId
            ORDER BY
                CASE WHEN SpecialUse IS NULL THEN 1 ELSE 0 END,
                Id;
            """,
            new
            {
                UserId = userId
            });
        return rows.Select(ToMailbox).ToList();
    }

    public async Task<List<MailboxStats>> GetMailboxStatsAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MailboxStatsRow>(
            """
            SELECT
                mb.Id,
                mb.Name,
                mb.SpecialUse,
                COALESCE(SUM(CASE WHEN (mm.Flags & @DeletedFlag) = 0 THEN 1 ELSE 0 END), 0) AS MessageCount,
                COALESCE(SUM(CASE WHEN (mm.Flags & @SeenFlag) = 0 AND (mm.Flags & @DeletedFlag) = 0 THEN 1 ELSE 0 END), 0) AS UnreadCount
            FROM Mailboxes AS mb
            LEFT JOIN MailboxMessages AS mm ON mm.MailboxId = mb.Id
            WHERE mb.UserId = @UserId
            GROUP BY mb.Id, mb.Name, mb.SpecialUse
            ORDER BY
                CASE WHEN mb.SpecialUse IS NULL THEN 1 ELSE 0 END,
                mb.Id;
            """,
            new
            {
                DeletedFlag = (int)MessageFlags.Deleted,
                SeenFlag = (int)MessageFlags.Seen,
                UserId = userId
            });
        return rows.Select(ToMailboxStats).ToList();
    }

    public async Task DeleteMailboxAsync(int mailboxId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM Mailboxes WHERE Id = @MailboxId;",
            new
            {
                MailboxId = mailboxId
            });
    }

    public async Task<long> AllocateUidAsync(int mailboxId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var uid = await connection.QuerySingleOrDefaultAsync<long?>(
            """
            UPDATE Mailboxes
            SET NextUid = NextUid + 1
            WHERE Id = @MailboxId
            RETURNING NextUid - 1;
            """,
            new
            {
                MailboxId = mailboxId
            })
            ?? throw new InvalidOperationException($"Mailbox {mailboxId} was not found");
        return uid;
    }

    public async Task InsertMessageAsync(Message message, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var messageId = await connection.QuerySingleOrDefaultAsync<int?>(
            """
            INSERT INTO Messages (
                Guid,
                RawContent,
                MessageId,
                FromAddress,
                ToAddress,
                CcAddress,
                Subject,
                BodyPreview,
                HtmlBody,
                TextBody,
                InternalDate,
                Size
            )
            VALUES (
                @Guid,
                @RawContent,
                @MessageId,
                @FromAddress,
                @ToAddress,
                @CcAddress,
                @Subject,
                @BodyPreview,
                @HtmlBody,
                @TextBody,
                @InternalDate,
                @Size
            )
            RETURNING Id;
            """,
            new
            {
                Guid = message.Guid.ToString(),
                RawContent = message.RawContent,
                MessageId = message.MessageId,
                FromAddress = message.FromAddress,
                ToAddress = message.ToAddress,
                CcAddress = message.CcAddress,
                Subject = message.Subject,
                BodyPreview = message.BodyPreview,
                HtmlBody = message.HtmlBody,
                TextBody = message.TextBody,
                InternalDate = message.InternalDate,
                Size = message.Size
            })
            ?? throw new InvalidOperationException("Failed to insert message");
        message.Id = messageId;
    }

    public async Task InsertMailboxMessageAsync(MailboxMessage mailboxMessage, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var mailboxMessageId = await connection.QuerySingleOrDefaultAsync<int?>(
            """
            INSERT INTO MailboxMessages (MailboxId, MessageId, Uid, Flags, InternalDate)
            VALUES (@MailboxId, @MessageId, @Uid, @Flags, @InternalDate)
            RETURNING Id;
            """,
            new
            {
                MailboxId = mailboxMessage.MailboxId,
                MessageId = mailboxMessage.MessageId,
                Uid = mailboxMessage.Uid,
                Flags = (int)mailboxMessage.Flags,
                InternalDate = mailboxMessage.InternalDate
            })
            ?? throw new InvalidOperationException("Failed to insert mailbox message");
        mailboxMessage.Id = mailboxMessageId;
    }

    public async Task UpdateMailboxMessageAsync(MailboxMessage mailboxMessage, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            UPDATE MailboxMessages
            SET
                MailboxId = @MailboxId,
                MessageId = @MessageId,
                Uid = @Uid,
                Flags = @Flags,
                InternalDate = @InternalDate
            WHERE Id = @Id;
            """,
            new
            {
                Id = mailboxMessage.Id,
                MailboxId = mailboxMessage.MailboxId,
                MessageId = mailboxMessage.MessageId,
                Uid = mailboxMessage.Uid,
                Flags = (int)mailboxMessage.Flags,
                InternalDate = mailboxMessage.InternalDate
            });
    }

    public async Task DeleteMailboxMessageAsync(int mailboxMessageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM MailboxMessages WHERE Id = @Id;",
            new
            {
                Id = mailboxMessageId
            });
    }

    public async Task DeleteMailboxMessagesAsync(IReadOnlyCollection<int> mailboxMessageIds, CancellationToken cancellationToken = default)
    {
        if (mailboxMessageIds.Count == 0)
        {
            return;
        }

        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        foreach (var mailboxMessageId in mailboxMessageIds)
        {
            await connection.ExecuteAsync(
                "DELETE FROM MailboxMessages WHERE Id = @Id;",
                new
                {
                    Id = mailboxMessageId
                });
        }
    }

    public async Task<MailboxMessagePage> GetEmailPageAsync(
        int userId,
        int? mailboxId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var offset = (page - 1) * pageSize;

        if (mailboxId.HasValue)
        {
            var totalCount = await connection.QuerySingleAsync<int>(
                """
                SELECT COUNT(*)
                FROM MailboxMessages AS mm
                INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
                WHERE mb.UserId = @UserId AND mm.MailboxId = @MailboxId;
                """,
                new
                {
                    UserId = userId,
                    MailboxId = mailboxId.Value
                });

            var rows = await connection.QueryAsync<MailboxMessageRow>(
                """
                SELECT
                    mm.Id AS MailboxMessageId,
                    mm.MailboxId AS MailboxMessageMailboxId,
                    mm.MessageId AS MailboxMessageMessageId,
                    mm.Uid AS MailboxMessageUid,
                    mm.Flags AS MailboxMessageFlags,
                    mm.InternalDate AS MailboxMessageInternalDate,
                    m.Id AS MessageId,
                    m.Guid AS MessageGuid,
                    m.RawContent AS MessageRawContent,
                    m.MessageId AS MessageMessageId,
                    m.FromAddress AS MessageFromAddress,
                    m.ToAddress AS MessageToAddress,
                    m.CcAddress AS MessageCcAddress,
                    m.Subject AS MessageSubject,
                    m.BodyPreview AS MessageBodyPreview,
                    m.HtmlBody AS MessageHtmlBody,
                    m.TextBody AS MessageTextBody,
                    m.InternalDate AS MessageInternalDate,
                    m.Size AS MessageSize,
                    mb.Id AS MailboxId,
                    mb.Guid AS MailboxGuid,
                    mb.UserId AS MailboxUserId,
                    mb.Name AS MailboxName,
                    mb.UidValidity AS MailboxUidValidity,
                    mb.NextUid AS MailboxNextUid,
                    mb.SpecialUse AS MailboxSpecialUse
                FROM MailboxMessages AS mm
                INNER JOIN Messages AS m ON m.Id = mm.MessageId
                INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
                WHERE mb.UserId = @UserId AND mm.MailboxId = @MailboxId
                ORDER BY mm.InternalDate DESC
                LIMIT @PageSize OFFSET @Offset;
                """,
                new
                {
                    UserId = userId,
                    MailboxId = mailboxId.Value,
                    PageSize = pageSize,
                    Offset = offset
                });
            return new MailboxMessagePage(totalCount, rows.Select(static row => ToMailboxMessage(row, includeMailbox: true)).ToList());
        }

        var inboxCount = await connection.QuerySingleAsync<int>(
            """
            SELECT COUNT(*)
            FROM MailboxMessages AS mm
            INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
            WHERE mb.UserId = @UserId AND mb.SpecialUse = @SpecialUse;
            """,
            new
            {
                UserId = userId,
                SpecialUse = (int)SpecialFolder.Inbox
            });

        var inboxRows = await connection.QueryAsync<MailboxMessageRow>(
            """
            SELECT
                mm.Id AS MailboxMessageId,
                mm.MailboxId AS MailboxMessageMailboxId,
                mm.MessageId AS MailboxMessageMessageId,
                mm.Uid AS MailboxMessageUid,
                mm.Flags AS MailboxMessageFlags,
                mm.InternalDate AS MailboxMessageInternalDate,
                m.Id AS MessageId,
                m.Guid AS MessageGuid,
                m.RawContent AS MessageRawContent,
                m.MessageId AS MessageMessageId,
                m.FromAddress AS MessageFromAddress,
                m.ToAddress AS MessageToAddress,
                m.CcAddress AS MessageCcAddress,
                m.Subject AS MessageSubject,
                m.BodyPreview AS MessageBodyPreview,
                m.HtmlBody AS MessageHtmlBody,
                m.TextBody AS MessageTextBody,
                m.InternalDate AS MessageInternalDate,
                m.Size AS MessageSize,
                mb.Id AS MailboxId,
                mb.Guid AS MailboxGuid,
                mb.UserId AS MailboxUserId,
                mb.Name AS MailboxName,
                mb.UidValidity AS MailboxUidValidity,
                mb.NextUid AS MailboxNextUid,
                mb.SpecialUse AS MailboxSpecialUse
            FROM MailboxMessages AS mm
            INNER JOIN Messages AS m ON m.Id = mm.MessageId
            INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
            WHERE mb.UserId = @UserId AND mb.SpecialUse = @SpecialUse
            ORDER BY mm.InternalDate DESC
            LIMIT @PageSize OFFSET @Offset;
            """,
            new
            {
                UserId = userId,
                SpecialUse = (int)SpecialFolder.Inbox,
                PageSize = pageSize,
                Offset = offset
            });
        return new MailboxMessagePage(inboxCount, inboxRows.Select(static row => ToMailboxMessage(row, includeMailbox: true)).ToList());
    }

    public async Task<MailboxMessage?> GetMailboxMessageByMessageIdAsync(
        int messageId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MailboxMessageRow>(
            """
            SELECT
                mm.Id AS MailboxMessageId,
                mm.MailboxId AS MailboxMessageMailboxId,
                mm.MessageId AS MailboxMessageMessageId,
                mm.Uid AS MailboxMessageUid,
                mm.Flags AS MailboxMessageFlags,
                mm.InternalDate AS MailboxMessageInternalDate,
                m.Id AS MessageId,
                m.Guid AS MessageGuid,
                m.RawContent AS MessageRawContent,
                m.MessageId AS MessageMessageId,
                m.FromAddress AS MessageFromAddress,
                m.ToAddress AS MessageToAddress,
                m.CcAddress AS MessageCcAddress,
                m.Subject AS MessageSubject,
                m.BodyPreview AS MessageBodyPreview,
                m.HtmlBody AS MessageHtmlBody,
                m.TextBody AS MessageTextBody,
                m.InternalDate AS MessageInternalDate,
                m.Size AS MessageSize,
                mb.Id AS MailboxId,
                mb.Guid AS MailboxGuid,
                mb.UserId AS MailboxUserId,
                mb.Name AS MailboxName,
                mb.UidValidity AS MailboxUidValidity,
                mb.NextUid AS MailboxNextUid,
                mb.SpecialUse AS MailboxSpecialUse
            FROM MailboxMessages AS mm
            INNER JOIN Messages AS m ON m.Id = mm.MessageId
            INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
            WHERE mm.MessageId = @MessageId AND mb.UserId = @UserId
            LIMIT 1;
            """,
            new
            {
                MessageId = messageId,
                UserId = userId
            });
        return row is null ? null : ToMailboxMessage(row, includeMailbox: true);
    }

    public async Task<List<MailboxMessage>> GetMailboxMessagesAsync(int mailboxId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MailboxMessageRow>(
            """
            SELECT
                mm.Id AS MailboxMessageId,
                mm.MailboxId AS MailboxMessageMailboxId,
                mm.MessageId AS MailboxMessageMessageId,
                mm.Uid AS MailboxMessageUid,
                mm.Flags AS MailboxMessageFlags,
                mm.InternalDate AS MailboxMessageInternalDate,
                m.Id AS MessageId,
                m.Guid AS MessageGuid,
                m.RawContent AS MessageRawContent,
                m.MessageId AS MessageMessageId,
                m.FromAddress AS MessageFromAddress,
                m.ToAddress AS MessageToAddress,
                m.CcAddress AS MessageCcAddress,
                m.Subject AS MessageSubject,
                m.BodyPreview AS MessageBodyPreview,
                m.HtmlBody AS MessageHtmlBody,
                m.TextBody AS MessageTextBody,
                m.InternalDate AS MessageInternalDate,
                m.Size AS MessageSize,
                mb.Id AS MailboxId,
                mb.Guid AS MailboxGuid,
                mb.UserId AS MailboxUserId,
                mb.Name AS MailboxName,
                mb.UidValidity AS MailboxUidValidity,
                mb.NextUid AS MailboxNextUid,
                mb.SpecialUse AS MailboxSpecialUse
            FROM MailboxMessages AS mm
            INNER JOIN Messages AS m ON m.Id = mm.MessageId
            INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
            WHERE mm.MailboxId = @MailboxId
            ORDER BY mm.Uid;
            """,
            new
            {
                MailboxId = mailboxId
            });
        return rows.Select(static row => ToMailboxMessage(row, includeMailbox: false)).ToList();
    }

    public async Task<List<MailboxMessage>> GetInboxMessagesAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<MailboxMessageRow>(
            """
            SELECT
                mm.Id AS MailboxMessageId,
                mm.MailboxId AS MailboxMessageMailboxId,
                mm.MessageId AS MailboxMessageMessageId,
                mm.Uid AS MailboxMessageUid,
                mm.Flags AS MailboxMessageFlags,
                mm.InternalDate AS MailboxMessageInternalDate,
                m.Id AS MessageId,
                m.Guid AS MessageGuid,
                m.RawContent AS MessageRawContent,
                m.MessageId AS MessageMessageId,
                m.FromAddress AS MessageFromAddress,
                m.ToAddress AS MessageToAddress,
                m.CcAddress AS MessageCcAddress,
                m.Subject AS MessageSubject,
                m.BodyPreview AS MessageBodyPreview,
                m.HtmlBody AS MessageHtmlBody,
                m.TextBody AS MessageTextBody,
                m.InternalDate AS MessageInternalDate,
                m.Size AS MessageSize,
                mb.Id AS MailboxId,
                mb.Guid AS MailboxGuid,
                mb.UserId AS MailboxUserId,
                mb.Name AS MailboxName,
                mb.UidValidity AS MailboxUidValidity,
                mb.NextUid AS MailboxNextUid,
                mb.SpecialUse AS MailboxSpecialUse
            FROM MailboxMessages AS mm
            INNER JOIN Messages AS m ON m.Id = mm.MessageId
            INNER JOIN Mailboxes AS mb ON mb.Id = mm.MailboxId
            WHERE
                mb.UserId = @UserId
                AND mb.SpecialUse = @SpecialUse
                AND (mm.Flags & @DeletedFlag) = 0
            ORDER BY mm.Uid;
            """,
            new
            {
                UserId = userId,
                SpecialUse = (int)SpecialFolder.Inbox,
                DeletedFlag = (int)MessageFlags.Deleted
            });
        return rows.Select(static row => ToMailboxMessage(row, includeMailbox: false)).ToList();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(this.connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=quail.db";
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            ForeignKeys = true
        };

        return builder.ToString();
    }

    private static User ToUser(UserRow row)
    {
        return new User
        {
            Id = row.Id,
            Guid = Guid.Parse(row.Guid),
            Username = row.Username,
            Email = row.Email,
            PasswordHash = row.PasswordHash,
            CreatedAt = row.CreatedAt
        };
    }

    private static Mailbox ToMailbox(MailboxRow row)
    {
        return new Mailbox
        {
            Id = row.Id,
            Guid = Guid.Parse(row.Guid),
            UserId = row.UserId,
            Name = row.Name,
            UidValidity = row.UidValidity,
            NextUid = row.NextUid,
            SpecialUse = ToSpecialUse(row.SpecialUse)
        };
    }

    private static Mailbox ToMailbox(MailboxMessageRow row)
    {
        return new Mailbox
        {
            Id = row.MailboxId,
            Guid = Guid.Parse(row.MailboxGuid),
            UserId = row.MailboxUserId,
            Name = row.MailboxName,
            UidValidity = row.MailboxUidValidity,
            NextUid = row.MailboxNextUid,
            SpecialUse = ToSpecialUse(row.MailboxSpecialUse)
        };
    }

    private static Message ToMessage(MailboxMessageRow row)
    {
        return new Message
        {
            Id = row.MessageId,
            Guid = Guid.Parse(row.MessageGuid),
            RawContent = row.MessageRawContent,
            MessageId = row.MessageMessageId,
            FromAddress = row.MessageFromAddress,
            ToAddress = row.MessageToAddress,
            CcAddress = row.MessageCcAddress,
            Subject = row.MessageSubject,
            BodyPreview = row.MessageBodyPreview,
            HtmlBody = row.MessageHtmlBody,
            TextBody = row.MessageTextBody,
            InternalDate = row.MessageInternalDate,
            Size = row.MessageSize
        };
    }

    private static MailboxMessage ToMailboxMessage(MailboxMessageRow row, bool includeMailbox)
    {
        var mailboxMessage = new MailboxMessage
        {
            Id = row.MailboxMessageId,
            MailboxId = row.MailboxMessageMailboxId,
            MessageId = row.MailboxMessageMessageId,
            Uid = row.MailboxMessageUid,
            Flags = (MessageFlags)row.MailboxMessageFlags,
            InternalDate = row.MailboxMessageInternalDate,
            Message = ToMessage(row)
        };

        if (includeMailbox)
        {
            mailboxMessage.Mailbox = ToMailbox(row);
        }

        return mailboxMessage;
    }

    private static MailboxStats ToMailboxStats(MailboxStatsRow row)
    {
        return new MailboxStats(row.Id, row.Name, ToSpecialUse(row.SpecialUse), row.MessageCount, row.UnreadCount);
    }

    private static SpecialFolder? ToSpecialUse(int? value)
    {
        if (value is null)
        {
            return null;
        }

        return (SpecialFolder)value.Value;
    }

    private static int? ToDbSpecialUse(SpecialFolder? value)
    {
        if (value is null)
        {
            return null;
        }

        return (int)value.Value;
    }

    internal sealed class UserRow
    {
        public int Id { get; set; }

        public string Guid { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }

    internal sealed class MailboxRow
    {
        public int Id { get; set; }

        public string Guid { get; set; } = string.Empty;

        public int UserId { get; set; }

        public string Name { get; set; } = string.Empty;

        public long UidValidity { get; set; }

        public long NextUid { get; set; }

        public int? SpecialUse { get; set; }
    }

    internal sealed class MailboxStatsRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public int? SpecialUse { get; set; }

        public int MessageCount { get; set; }

        public int UnreadCount { get; set; }
    }

    internal sealed class MailboxMessageRow
    {
        public int MailboxMessageId { get; set; }

        public int MailboxMessageMailboxId { get; set; }

        public int MailboxMessageMessageId { get; set; }

        public long MailboxMessageUid { get; set; }

        public int MailboxMessageFlags { get; set; }

        public DateTime MailboxMessageInternalDate { get; set; }

        public int MessageId { get; set; }

        public string MessageGuid { get; set; } = string.Empty;

        public string MessageRawContent { get; set; } = string.Empty;

        public string MessageMessageId { get; set; } = string.Empty;

        public string MessageFromAddress { get; set; } = string.Empty;

        public string MessageToAddress { get; set; } = string.Empty;

        public string? MessageCcAddress { get; set; }

        public string MessageSubject { get; set; } = string.Empty;

        public string? MessageBodyPreview { get; set; }

        public string? MessageHtmlBody { get; set; }

        public string? MessageTextBody { get; set; }

        public DateTime MessageInternalDate { get; set; }

        public long MessageSize { get; set; }

        public int MailboxId { get; set; }

        public string MailboxGuid { get; set; } = string.Empty;

        public int MailboxUserId { get; set; }

        public string MailboxName { get; set; } = string.Empty;

        public long MailboxUidValidity { get; set; }

        public long MailboxNextUid { get; set; }

        public int? MailboxSpecialUse { get; set; }
    }
}

public sealed record MailboxStats(int Id, string Name, SpecialFolder? SpecialUse, int MessageCount, int UnreadCount);

public sealed record MailboxMessagePage(int TotalCount, List<MailboxMessage> Items);
