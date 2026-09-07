using dotnetai.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Data;

/// <summary>
/// The DbContext is your session with the database. It does three jobs:
///   1. Describes the model (which classes map to which tables, via DbSet + OnModelCreating).
///   2. Tracks changes to entities you loaded, so SaveChangesAsync() knows what to UPDATE.
///   3. Wraps each SaveChangesAsync() in a transaction.
///
/// LIFETIME: AddDbContext registers this as SCOPED — one instance per HTTP request. That is
/// deliberate, not incidental. A DbContext is not thread-safe and it accumulates change-tracking
/// state, so sharing one across requests would leak entities between users and corrupt
/// concurrent saves. Per-request scoping gives every request a clean tracker, then disposes it.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>
    /// FLUENT CONFIGURATION. EF infers a lot by convention (Id becomes the primary key, the
    /// User navigation property becomes a foreign key). Everything the conventions cannot guess
    /// — column lengths, unique indexes, delete behavior — is declared here.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).IsRequired().HasMaxLength(256);

            // Database-level uniqueness. The database is the last line of defense and the only
            // one that holds under concurrency: two simultaneous registrations can both pass an
            // application-level "does this email exist?" check before either one saves.
            //
            // CAVEAT WORTH KNOWING: SQLite's default TEXT collation is BINARY, i.e. CASE-SENSITIVE.
            // This index alone will happily store 'Jeff@X.com' alongside 'jeff@x.com'. We
            // normalize to lowercase in application code before writing. (The alternative,
            // .UseCollation("NOCASE"), is provider-specific and would not survive a move to
            // PostgreSQL — normalizing in code is portable.)
            e.HasIndex(u => u.Email).IsUnique();

            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.Role).IsRequired().HasMaxLength(32);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(rt => rt.Id);

            e.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(64);  // 32 bytes as hex
            e.HasIndex(rt => rt.TokenHash).IsUnique();   // this is our lookup key on every refresh
            e.HasIndex(rt => rt.UserId);                 // for "revoke every token for this user"

            e.Property(rt => rt.RevokedReason).HasMaxLength(64);
            e.Property(rt => rt.ReplacedByTokenHash).HasMaxLength(64);

            e.HasOne(rt => rt.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(rt => rt.UserId)
             .OnDelete(DeleteBehavior.Cascade);   // deleting a user kills all their sessions
        });
    }
}
