using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Event> Events { get; set; } = null!;
        public DbSet<Showtime> Showtimes { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Event>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
                entity.Property(e => e.Location).IsRequired().HasMaxLength(500);
                entity.Property(e => e.TotalSeats).IsRequired();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            modelBuilder.Entity<Showtime>(entity =>
            {
                entity.HasKey(s => s.Id);

                entity.Property(s => s.StartTime).IsRequired();
                entity.Property(s => s.EndTime).IsRequired();
                entity.Property(s => s.AvailableSeats).IsRequired();

                entity.HasOne(s => s.Event)
                    .WithMany()
                    .HasForeignKey(s => s.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
