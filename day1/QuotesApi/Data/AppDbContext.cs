using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quote>(builder =>
        {
            builder.Property(q => q.Author).IsRequired().HasMaxLength(Quote.MaxAuthorLength);
            builder.Property(q => q.Text).IsRequired().HasMaxLength(Quote.MaxTextLength);
            builder.Property(q => q.IsDeleted).IsRequired();
            builder.Property(q => q.CreatedByUserId).IsRequired();
            builder.HasOne<User>().WithMany().HasForeignKey(q => q.CreatedByUserId);
            // GET /api/reports/authors groups by Author for every request -
            // without this, that GROUP BY (and any WHERE Author = ...) is a
            // full table scan.
            builder.HasIndex(q => q.Author);
        });

        modelBuilder.Entity<User>(builder =>
        {
            builder.Property(u => u.Email).IsRequired();
            builder.Property(u => u.PasswordHash).IsRequired();
            builder.Property(u => u.Scopes).IsRequired();
            builder.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.Property(t => t.TokenHash).IsRequired();
            builder.HasIndex(t => t.TokenHash).IsUnique();
            builder.Property(t => t.UserId).IsRequired();
            builder.Property(t => t.ExpiresAt).IsRequired();
            builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId);
        });

        modelBuilder.Entity<Collection>(builder =>
        {
            builder.HasKey(c => c.Id);
            builder.Property(c => c.Name).IsRequired().HasMaxLength(Collection.MaxNameLength);
            builder.Property(c => c.OwnerId).IsRequired();

            // Items is exposed only as IReadOnlyList with a private backing field;
            // EF must write through the field, not the (nonexistent) setter.
            builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

            builder.OwnsMany(c => c.Items, items =>
            {
                items.WithOwner().HasForeignKey("CollectionId");
                items.Property(i => i.QuoteId).IsRequired().ValueGeneratedNever();
                items.Property(i => i.AddedAt).IsRequired();
                items.HasKey("CollectionId", "QuoteId");
                items.ToTable("CollectionItems");
            });
        });

        modelBuilder.Entity<OutboxMessage>(builder =>
        {
            builder.HasKey(m => m.Id);
            builder.Property(m => m.Type).IsRequired();
            builder.Property(m => m.Payload).IsRequired();

            // SQLite's provider can't translate ORDER BY on a DateTimeOffset column
            // (it stores them as ISO-8601 text, not a form it knows how to compare) -
            // the relay's poll query orders by OccurredAt, so this stores it as raw
            // UTC ticks instead. Works identically against SQL Server's own
            // DateTimeOffset column, so this isn't a SQLite-only special case.
            builder.Property(m => m.OccurredAt)
                .IsRequired()
                .HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

            // The relay's poll query is always "WHERE ProcessedAt IS NULL ORDER BY
            // OccurredAt" - without this index that's a full table scan on every
            // tick as the table grows.
            builder.HasIndex(m => m.ProcessedAt);
        });

        modelBuilder.Entity<ProcessedMessage>(builder =>
        {
            builder.HasKey(m => m.Id);
            builder.Property(m => m.MessageId).IsRequired();
            builder.Property(m => m.Consumer).IsRequired();
            // A message can be redelivered to the same consumer many times, but must
            // only ever produce one row per (Consumer, MessageId) - this is the
            // constraint the dedupe check actually relies on, not just an index.
            builder.HasIndex(m => new { m.Consumer, m.MessageId }).IsUnique();
        });

        modelBuilder.Entity<AuditLogEntry>(builder =>
        {
            builder.HasKey(e => e.Id);
            builder.Property(e => e.EventType).IsRequired();
            builder.Property(e => e.Payload).IsRequired();
        });
    }
}
