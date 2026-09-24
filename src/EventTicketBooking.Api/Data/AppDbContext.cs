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
        public DbSet<Seat> Seats { get; set; } = null!;
        public DbSet<SeatCategory> SeatCategories { get; set; } = null!;

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

            modelBuilder.Entity<SeatCategory>(entity =>
            {
                entity.HasKey(sc => sc.Id);

                entity.Property(sc => sc.Name)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.HasOne(sc => sc.Showtime)
                    .WithMany()
                    .HasForeignKey(sc => sc.ShowtimeId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(sc => sc.ShowtimeId);
            });

            modelBuilder.Entity<Seat>(entity =>
            {
                entity.HasKey(s => s.Id);

                entity.Property(s => s.Row)
                    .IsRequired()
                    .HasMaxLength(10);

                entity.Property(s => s.SeatNumber).IsRequired();

                entity.HasOne(s => s.Showtime)
                    .WithMany()
                    .HasForeignKey(s => s.ShowtimeId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(s => s.SeatCategory)
                    .WithMany()
                    .HasForeignKey(s => s.SeatCategoryId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(s => s.ShowtimeId);
                entity.HasIndex(s => s.SeatCategoryId);
                entity.HasIndex(s => new { s.ShowtimeId, s.Row, s.SeatNumber })
                    .IsUnique();
            });
        }
    }
}
