using AgentServer.Models;
using Microsoft.EntityFrameworkCore;

namespace AgentServer.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomParticipant> RoomParticipants => Set<RoomParticipant>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.UserId).IsUnique();
        });

        modelBuilder.Entity<Room>(e =>
        {
            e.HasIndex(r => r.RoomId).IsUnique();
            e.HasMany(r => r.Participants)
             .WithOne(p => p.Room)
             .HasForeignKey(p => p.RoomId);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasIndex(m => m.RoomId);
            e.HasIndex(m => m.SentAt);
        });
    }
}
