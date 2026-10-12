using Microsoft.EntityFrameworkCore;

namespace PingPong.Api.Domain;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<RatingHistory> RatingHistory => Set<RatingHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Player>().HasIndex(p => p.Name).IsUnique();
        b.Entity<Player>().Property(p => p.Name).HasMaxLength(50);

        // Restrict: sletting av en spiller skal ikke slette kamphistorikken i det stille.
        b.Entity<Match>().HasOne(m => m.Player1).WithMany().HasForeignKey(m => m.Player1Id).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Match>().HasOne(m => m.Player2).WithMany().HasForeignKey(m => m.Player2Id).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Match>().HasOne(m => m.Winner).WithMany().HasForeignKey(m => m.WinnerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Match>().HasIndex(m => m.PlayedAt);

        b.Entity<RatingHistory>().HasOne<Player>().WithMany().HasForeignKey(r => r.PlayerId);
        b.Entity<RatingHistory>().HasOne<Match>().WithMany().HasForeignKey(r => r.MatchId);
    }
}
