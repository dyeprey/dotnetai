using dotnetai.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace dotnetai.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).IsRequired().HasMaxLength(256);
            e.HasIndex(u => u.Email).IsUnique();

            e.Property(u => u.PasswordHash).IsRequired();
            e.Property(u => u.Role).IsRequired().HasMaxLength(32);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(rt => rt.Id);

            e.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(64);
            e.HasIndex(rt => rt.TokenHash).IsUnique();
            e.HasIndex(rt => rt.UserId);

            e.Property(rt => rt.RevokedReason).HasMaxLength(64);
            e.Property(rt => rt.ReplacedByTokenHash).HasMaxLength(64);

            e.HasOne(rt => rt.User)
             .WithMany(u => u.RefreshTokens)
             .HasForeignKey(rt => rt.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
