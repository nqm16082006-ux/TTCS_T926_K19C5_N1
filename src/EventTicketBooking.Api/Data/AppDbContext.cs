using EventTicketBooking.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace EventTicketBooking.Api.Data
{
    public class AppDbContext : DbContext
    {

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }

        public DbSet<Event> Events { get; set; } = null!;
        public DbSet<Showtime> Showtimes { get; set; } = null!;
        public DbSet<Seat> Seats { get; set; } = null!;
        public DbSet<SeatCategory> SeatCategories { get; set; } = null!;
        public DbSet<SeatHolds> SeatHold { get; set; } = null!;
        public DbSet<Order> Orders { get; set; } = null!;
        public DbSet<OrderItem> OrderItems { get; set; } = null!;
        public DbSet<PaymentTransaction> PaymentTransactions { get; set; } = null!;
        public DbSet<PaymentEvent> PaymentEvents { get; set; } = null!;
        public DbSet<Ticket> Tickets { get; set; } = null!;
        public DbSet<Role> Roles { get; set; } = null!;
        public DbSet<User> Users { get; set; } = null!;
        public DbSet<UserRole> UserRoles { get; set; } = null!;
        public DbSet<AuditLog> AuditLogs { get; set; } = null!;
        public DbSet<TicketReadmission> TicketReadmissions { get; set; } = null!;
        public DbSet<EmailFailureLog> EmailFailureLogs { get; set; } = null!;
        public DbSet<OrderAuditLog> OrderAuditLogs { get; set; } = null!;
        public DbSet<OfflineCheckInRecord> OfflineCheckInRecords { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<TicketReadmission>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.StaffName).HasMaxLength(200).IsRequired();
                entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
                entity.Property(x => x.Gate).HasMaxLength(100).IsRequired();
                entity.HasOne(x => x.OrderItem).WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
                entity.HasOne<User>().WithMany().HasForeignKey(x => x.StaffUserId).OnDelete(DeleteBehavior.Restrict);
            });

            if (Database.IsNpgsql())
            {
            }

            modelBuilder.Entity<Event>(entity =>
            {
                entity.ToTable("Events");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Title).IsRequired().HasMaxLength(250);
                entity.Property(e => e.Location).IsRequired().HasMaxLength(500);
                entity.Property(e => e.ImageUrl).HasColumnType("text").HasMaxLength(2000);
                entity.Property(e => e.TotalSeats).IsRequired();
                entity.Property(e => e.MaxTicketsPerUser).IsRequired().HasDefaultValue(10);
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
                entity.Property(s => s.MaxTicketsPerUser).IsRequired().HasDefaultValue(10);
                entity.Property(s => s.Status)
                      .HasConversion(
                          v => v.ToString(),
                          v => ParseShowtimeStatus(v));

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
                entity.Property(sc => sc.Price).HasColumnType("integer").IsRequired(false);
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

                entity.HasIndex(sh => sh.SeatId)
                    .IsUnique()
                    .HasFilter("\"Status\" = 'ACTIVE'")
                    .HasDatabaseName("IX_seat_holds_SeatId_Active");
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

            // Cấu hình bảng AuditLog (S-28): chỉ lưu thay đổi phân quyền
            modelBuilder.Entity<AuditLog>(entity =>
            {
                entity.ToTable("AuditLogs");
                entity.HasKey(a => a.Id);
                entity.Property(a => a.ActorUsername).IsRequired().HasMaxLength(50);
                entity.Property(a => a.OldRoles).IsRequired().HasMaxLength(500);
                entity.Property(a => a.NewRoles).IsRequired().HasMaxLength(500);
                entity.Property(a => a.Action).IsRequired().HasMaxLength(50);
                entity.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.HasIndex(a => a.TargetUserId);
            });

            // Cấu hình bảng Orders
            modelBuilder.Entity<Order>(entity =>
            {
                entity.ToTable("orders");
                entity.HasKey(o => o.Id);

                entity.Property(o => o.TotalAmount).IsRequired();
                entity.Property(o => o.ExpiresAt).IsRequired();
                entity.Property(o => o.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(o => o.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.Property(o => o.Status)
                      .HasConversion(
                          v => v.ToString(),
                          v => ParseOrderStatus(v));

                entity.HasOne(o => o.User)
                      .WithMany()
                      .HasForeignKey(o => o.UserId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(o => o.Showtime)
                      .WithMany()
                      .HasForeignKey(o => o.ShowtimeId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Chống bấm đúp bằng ràng buộc "một đơn chờ cho mỗi người mỗi suất"
                entity.HasIndex(o => new { o.UserId, o.ShowtimeId })
                      .IsUnique()
                      .HasFilter(Database.IsNpgsql() ? "\"Status\" = 'Pending'" : "\"Status\" = 'Pending'")
                      .HasDatabaseName("IX_orders_UserId_ShowtimeId_Pending");
            });

            // Cấu hình bảng OrderItems
            modelBuilder.Entity<OrderItem>(entity =>
            {
                entity.ToTable("order_items");
                entity.HasKey(oi => oi.Id);

                entity.Property(oi => oi.Price).IsRequired();

                entity.HasOne(oi => oi.Order)
                      .WithMany(o => o.OrderItems)
                      .HasForeignKey(oi => oi.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(oi => oi.Seat)
                      .WithMany()
                      .HasForeignKey(oi => oi.SeatId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(oi => new { oi.OrderId, oi.SeatId }).IsUnique();
            });

            // Cấu hình bảng PaymentTransactions (Task T-41)
            modelBuilder.Entity<PaymentTransaction>(entity =>
            {
                entity.ToTable("payment_transactions");
                entity.HasKey(pt => pt.Id);

                entity.Property(pt => pt.OrderCode).IsRequired();
                entity.Property(pt => pt.Amount).IsRequired();
                entity.Property(pt => pt.Status).IsRequired().HasMaxLength(20);
                entity.Property(pt => pt.PaymentUrl).HasMaxLength(1000);
                entity.Property(pt => pt.TransactionId).HasMaxLength(100);
                entity.Property(pt => pt.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(pt => pt.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(pt => pt.Order)
                      .WithOne(o => o.PaymentTransaction)
                      .HasForeignKey<PaymentTransaction>(pt => pt.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Chống duplicate: 1 Order chỉ có duy nhất 1 giao dịch thanh toán
                entity.HasIndex(pt => pt.OrderId).IsUnique().HasDatabaseName("IX_payment_transactions_OrderId_Unique");
                entity.HasIndex(pt => pt.OrderCode).IsUnique().HasDatabaseName("IX_payment_transactions_OrderCode_Unique");
            });

            // Cấu hình bảng PaymentEvents (Task T-45)
            modelBuilder.Entity<PaymentEvent>(entity =>
            {
                entity.ToTable("payment_events");
                entity.HasKey(pe => pe.Id);

                entity.Property(pe => pe.TransactionId).IsRequired().HasMaxLength(100);
                entity.Property(pe => pe.RawPayload).IsRequired();
                entity.Property(pe => pe.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasIndex(pe => pe.TransactionId)
                      .IsUnique()
                      .HasDatabaseName("IX_payment_events_TransactionId_Unique");
            });

            // Cấu hình bảng EmailFailureLogs (Task S-27)
            modelBuilder.Entity<EmailFailureLog>(entity =>
            {
                entity.ToTable("EmailFailureLogs");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.TargetEmail).IsRequired().HasMaxLength(100);
                entity.Property(e => e.ErrorMessage).IsRequired();
                entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(e => e.Attempts).IsRequired().HasDefaultValue(0);
                entity.Property(e => e.Resolved).HasDefaultValue(false);
                entity.HasIndex(e => e.OrderId);
            });

            // Cấu hình bảng tickets (Story S-25)
            modelBuilder.Entity<Ticket>(entity =>
            {
                entity.ToTable("tickets");
                entity.HasKey(t => t.Id);

                entity.Property(t => t.TicketCode)
                      .IsRequired()
                      .HasMaxLength(64);

                entity.Property(t => t.CreatedAt)
                      .HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(t => t.OrderItem)
                      .WithOne(oi => oi.Ticket)
                      .HasForeignKey<Ticket>(t => t.OrderItemId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(t => t.OrderItemId)
                      .IsUnique()
                      .HasDatabaseName("IX_tickets_OrderItemId_Unique");

                entity.HasIndex(t => t.TicketCode)
                      .IsUnique()
                      .HasDatabaseName("IX_tickets_TicketCode_Unique");
            });

            // Cấu hình bảng nhật ký thao tác đơn hàng và vé (Story S-49)
            modelBuilder.Entity<OrderAuditLog>(entity =>
            {
                entity.ToTable("order_audit_logs");
                entity.HasKey(l => l.Id);
                entity.Property(l => l.EntityType).IsRequired().HasMaxLength(50);
                entity.Property(l => l.EntityId).HasMaxLength(100);
                entity.Property(l => l.Action).IsRequired().HasMaxLength(100);
                entity.Property(l => l.OldStatus).HasMaxLength(50);
                entity.Property(l => l.NewStatus).IsRequired().HasMaxLength(50);
                entity.Property(l => l.ActorType).IsRequired().HasMaxLength(50);
                entity.Property(l => l.Actor).IsRequired().HasMaxLength(150);
                entity.Property(l => l.Timestamp).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(l => l.Note).HasMaxLength(1000);

                entity.HasOne(l => l.Order)
                      .WithMany()
                      .HasForeignKey(l => l.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(l => l.OrderId).HasDatabaseName("IX_order_audit_logs_OrderId");
                entity.HasIndex(l => l.Timestamp).HasDatabaseName("IX_order_audit_logs_Timestamp");
                entity.HasIndex(l => l.EntityType).HasDatabaseName("IX_order_audit_logs_EntityType");
            });

            // Cấu hình bảng OfflineCheckInRecords (Story S-35)
            modelBuilder.Entity<OfflineCheckInRecord>(entity =>
            {
                entity.ToTable("OfflineCheckInRecords");
                entity.HasKey(x => x.Id);

                entity.Property(x => x.OfflineScanId)
                      .IsRequired()
                      .HasMaxLength(100);

                entity.Property(x => x.TicketCode)
                      .IsRequired()
                      .HasMaxLength(64);

                entity.Property(x => x.GateName)
                      .IsRequired()
                      .HasMaxLength(100);

                entity.Property(x => x.Status)
                      .IsRequired()
                      .HasMaxLength(20);

                entity.Property(x => x.ConflictReason)
                      .HasMaxLength(100);

                entity.Property(x => x.ExistingCheckInGate)
                      .HasMaxLength(100);

                entity.Property(x => x.ReceivedAtServer)
                      .HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasIndex(x => x.OfflineScanId)
                      .IsUnique()
                      .HasDatabaseName("UX_OfflineCheckInRecords_OfflineScanId");

                entity.HasIndex(x => x.TicketCode);
                entity.HasIndex(x => x.ShowtimeId);

                entity.HasOne(x => x.OrderItem)
                      .WithMany()
                      .HasForeignKey(x => x.OrderItemId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.Showtime)
                      .WithMany()
                      .HasForeignKey(x => x.ShowtimeId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.SyncedByUser)
                      .WithMany()
                      .HasForeignKey(x => x.SyncedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);
            });
        }

        public static ShowtimeStatus ParseShowtimeStatus(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ShowtimeStatus.Draft;
            var normalized = value.Trim().ToLowerInvariant().Replace("_", "");
            return normalized switch
            {
                "onsale" => ShowtimeStatus.OnSale,
                "closed" => ShowtimeStatus.Closed,
                "draft" => ShowtimeStatus.Draft,
                _ => Enum.TryParse<ShowtimeStatus>(value, true, out var result) ? result : ShowtimeStatus.Draft
            };
        }

        public static OrderStatus ParseOrderStatus(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return OrderStatus.Pending;
            var normalized = value.Trim().ToLowerInvariant().Replace("_", "");
            return normalized switch
            {
                "pending" => OrderStatus.Pending,
                "paid" => OrderStatus.Paid,
                "cancelled" => OrderStatus.Cancelled,
                "expired" => OrderStatus.Expired,
                "needsattention" => OrderStatus.NeedsAttention,
                _ => Enum.TryParse<OrderStatus>(value, true, out var result) ? result : OrderStatus.Pending
            };
        }
    }
}
