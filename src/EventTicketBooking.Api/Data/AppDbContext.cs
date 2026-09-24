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
		public DbSet<Role> Roles { get; set; } = null!;
		public DbSet<User> Users { get; set; } = null!;
		public DbSet<UserRole> UserRoles { get; set; } = null!;
		public DbSet<Seat> Seats { get; set; } = null!;
		public DbSet<SeatHolds> SeatHold { get; set; } = null!;
		protected override void OnModelCreating(ModelBuilder modelBuilder)
		{
			base.OnModelCreating(modelBuilder);

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

				entity.HasOne(s => s.Event)
					.WithMany(e => e.Showtimes)
					.HasForeignKey(s => s.EventId)
					.OnDelete(DeleteBehavior.Cascade);
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
