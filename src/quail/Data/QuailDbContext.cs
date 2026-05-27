using Microsoft.EntityFrameworkCore;
using Quail.Models;

namespace Quail.Data;

public class QuailDbContext : DbContext
{
    public QuailDbContext(DbContextOptions<QuailDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MailboxMessage> MailboxMessages => Set<MailboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Guid).IsUnique();
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Mailbox>(e =>
        {
            e.HasIndex(m => m.Guid).IsUnique();
            e.HasIndex(m => new { m.UserId, m.Name }).IsUnique();
            e.HasOne(m => m.User).WithMany(u => u.Mailboxes).HasForeignKey(m => m.UserId);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.HasIndex(m => m.Guid).IsUnique();
            e.HasIndex(m => m.MessageId);
        });

        modelBuilder.Entity<MailboxMessage>(e =>
        {
            e.HasIndex(mm => new { mm.MailboxId, mm.Uid }).IsUnique();
            e.HasIndex(mm => new { mm.MailboxId, mm.Flags });
            e.HasOne(mm => mm.Mailbox).WithMany(m => m.Messages).HasForeignKey(mm => mm.MailboxId);
            e.HasOne(mm => mm.Message).WithMany(m => m.MailboxMessages).HasForeignKey(mm => mm.MessageId);
        });
    }
}
