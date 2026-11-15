using Microsoft.EntityFrameworkCore;

namespace Gt5pOnlineServer.Data;

// Postgres (Npgsql) persistence replacing the Python data.json flat file.
// Tables: users, sessions, rankings. Matchmaking rooms stay in-memory
// (ephemeral pools, same as grim_state.py).
public sealed class GrimDbContext(DbContextOptions<GrimDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<RankingEntry> Rankings => Set<RankingEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OnlineId).IsUnique();
            e.HasIndex(x => x.UserNumber).IsUnique();
            e.Property(x => x.OnlineId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Region).HasMaxLength(16);
            e.Property(x => x.Country).HasMaxLength(16);
            e.Property(x => x.Nickname).HasMaxLength(64);
        });
        b.Entity<SessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Token);
            e.Property(x => x.Token).HasMaxLength(64);
            e.Property(x => x.OnlineId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.OnlineId);
            e.HasIndex(x => x.ExpiresAt);
        });
        b.Entity<RankingEntry>(e =>
        {
            e.ToTable("rankings");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.BoardId, x.TimeMs });
            e.Property(x => x.BoardId).HasMaxLength(32).IsRequired();
            e.Property(x => x.OnlineId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Car).HasMaxLength(64);
            e.Property(x => x.Meta).HasMaxLength(256);
        });
    }
}

public sealed class UserEntity
{
    public int Id { get; set; }
    public string OnlineId { get; set; } = "";
    public int UserNumber { get; set; }
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public string Nickname { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
}

public sealed class SessionEntity
{
    public string Token { get; set; } = "";
    public string OnlineId { get; set; } = "";
    public int UserNumber { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class RankingEntry
{
    public int Id { get; set; }
    public string BoardId { get; set; } = "0";
    public string OnlineId { get; set; } = "";
    public long TimeMs { get; set; }
    public string Car { get; set; } = "";
    public string Meta { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.UtcNow;
}
