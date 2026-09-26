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
        public DbSet<SeatHolds> SeatHold { get; set; } = null!;
        public DbSet<Role> Roles { get; set; } = null!;
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UserRole> UserRoles { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.HasPostgresEnum<ShowtimeStatus>();

            modelBuilder.Entity<Event>(entity =>
            {
                entity.ToTable("Events");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
                entity.Property(e => e.Location).IsRequired().HasMaxLength(500);
                entity.Property(e => e.TotalSeats).IsRequired();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(e => e.Owner)
                      .WithMany(u => u.Events)
                      .HasForeignKey(e => e.OwnerId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<Showtime>(entity =>
            {
                entity.ToTable("Showtimes");
                entity.HasKey(s => s.Id);

                entity.Property(s => s.StartTime).IsRequired();
                entity.Property(s => s.EndTime).IsRequired();
                entity.Property(s => s.AvailableSeats).IsRequired();
                entity.Property(s => s.Status)
                      .HasColumnType("showtime_status")
                      .HasDefaultValue(ShowtimeStatus.Draft);

                entity.HasOne(s => s.Event)
                    .WithMany(e => e.Showtimes)
                    .HasForeignKey(s => s.EventId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SeatCategory>(entity =>
            {
                entity.ToTable("SeatCategories");
                entity.HasKey(sc => sc.Id);
                entity.Property(sc => sc.Name).IsRequired().HasMaxLength(100);
                entity.Property(sc => sc.Price).HasColumnType("numeric(18,2)").IsRequired();
                entity.HasOne(sc => sc.Showtime)
                      .WithMany(s => s.SeatCategories)
                      .HasForeignKey(sc => sc.ShowtimeId)
                      .OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(sc => sc.ShowtimeId);
            });

            modelBuilder.Entity<Seat>(entity =>
            {
                entity.ToTable("Seats");
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

            // Cấu hình bảng seat_holds (Task T-22)
            modelBuilder.Entity<SeatHolds>(entity =>
            {
                entity.ToTable("seat_holds");
                entity.HasKey(sh => sh.Id);

                entity.Property(sh => sh.Status)
                    .IsRequired()
                    .HasMaxLength(20)
                    .HasDefaultValue("ACTIVE");

                entity.Property(sh => sh.HeldAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.Property(sh => sh.ExpiresAt)
                    .IsRequired();

                entity.HasOne(sh => sh.Seat)
                    .WithMany()
                    .HasForeignKey(sh => sh.SeatId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(sh => sh.User)
                    .WithMany()
                    .HasForeignKey(sh => sh.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(sh => sh.ExpiresAt)
                    .HasDatabaseName("IX_seat_holds_expires_at");

                entity.HasIndex(sh => new { sh.Status, sh.ExpiresAt })
                    .HasDatabaseName("IX_seat_holds_status_expires_at");

                entity.HasIndex(sh => sh.SeatId);
                entity.HasIndex(sh => sh.UserId);
            });

            // Cấu hình bảng Roles
            modelBuilder.Entity<Role>(entity =>
            {
                entity.ToTable("Roles");
                entity.HasKey(r => r.Id);
                entity.Property(r => r.Name).IsRequired().HasMaxLength(50);
                entity.HasIndex(r => r.Name).IsUnique();
                entity.Property(r => r.Description).HasMaxLength(255);
                entity.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            });

            // Cấu hình bảng Users
            modelBuilder.Entity<User>(entity =>
            {
                entity.ToTable("Users");
                entity.HasKey(u => u.Id);
                entity.Property(u => u.Username).IsRequired().HasMaxLength(50);
                entity.HasIndex(u => u.Username).IsUnique();
                entity.Property(u => u.Email).IsRequired().HasMaxLength(100);
                entity.HasIndex(u => u.Email).IsUnique();
                entity.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);
                entity.Property(u => u.FullName).HasMaxLength(100);
                entity.Property(u => u.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(u => u.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(u => u.IsActive).HasDefaultValue(false);
            });

            // Cấu hình bảng trung gian UserRoles (Quan hệ N:N)
            modelBuilder.Entity<UserRole>(entity =>
            {
                entity.ToTable("UserRoles");
                entity.HasKey(ur => new { ur.UserId, ur.RoleId });
                entity.Property(ur => ur.AssignedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(ur => ur.User)
                      .WithMany(u => u.UserRoles)
                      .HasForeignKey(ur => ur.UserId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(ur => ur.Role)
                      .WithMany(r => r.UserRoles)
                      .HasForeignKey(ur => ur.RoleId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(ur => ur.RoleId);
            });
        }
    }
}
