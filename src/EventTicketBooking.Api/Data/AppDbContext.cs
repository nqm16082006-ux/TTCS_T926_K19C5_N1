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
        public DbSet<RefundRequest> RefundRequests { get; set; } = null!;
        public DbSet<Refund> Refunds { get; set; } = null!;
        public DbSet<SystemAlert> SystemAlerts { get; set; } = null!;
        public DbSet<DiscountCode> DiscountCodes { get; set; } = null!;
        public DbSet<DataDeletionRequest> DataDeletionRequests { get; set; } = null!;
        public DbSet<ReconciliationSession> ReconciliationSessions { get; set; } = null!;
        public DbSet<TicketTransfer> TicketTransfers { get; set; } = null!;
        public DbSet<ShowtimeWaitlist> ShowtimeWaitlists { get; set; } = null!;
        public DbSet<RequestTelemetry> RequestTelemetries { get; set; } = null!;
        public DbSet<MonitoringEvent> MonitoringEvents { get; set; } = null!;
        public DbSet<MonitoringAlert> MonitoringAlerts { get; set; } = null!;

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
                entity.Property(e => e.AllowTicketTransfer).IsRequired().HasDefaultValue(true);
                entity.Property(e => e.TransferCutoffHours).IsRequired().HasDefaultValue(2);
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

                // Story S-45: lý do huỷ đơn (PAYMENT_CANCELLED / CUSTOMER_REFUND)
                entity.Property(o => o.CancelReason).HasMaxLength(30);

                // Story S-56: Mã giảm giá
                entity.Property(o => o.OriginalAmount).IsRequired().HasDefaultValue(0);
                entity.Property(o => o.DiscountAmount).IsRequired().HasDefaultValue(0);
                entity.Property(o => o.DiscountCodeSnapshot).HasMaxLength(500);

                entity.HasOne(o => o.DiscountCode)
                      .WithMany()
                      .HasForeignKey(o => o.DiscountCodeId)
                      .OnDelete(DeleteBehavior.SetNull);

                // Story S-61: thời điểm gửi email nhắc nhở 24h trước giờ diễn
                entity.Property(o => o.ReminderSentAt);

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

            // Cấu hình bảng DiscountCodes (Story S-56)
            modelBuilder.Entity<DiscountCode>(entity =>
            {
                entity.ToTable("DiscountCodes");
                entity.HasKey(d => d.Id);
                entity.Property(d => d.Code).IsRequired().HasMaxLength(50);
                entity.Property(d => d.DiscountType).HasConversion<string>().HasMaxLength(20);
                entity.Property(d => d.DiscountValue).IsRequired();
                entity.Property(d => d.UsageLimit).IsRequired();
                entity.Property(d => d.UsedCount).IsRequired().HasDefaultValue(0);
                entity.Property(d => d.ExpiresAt).IsRequired();
                entity.Property(d => d.IsActive).IsRequired().HasDefaultValue(true);
                entity.Property(d => d.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(d => d.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(d => d.Event)
                      .WithMany()
                      .HasForeignKey(d => d.EventId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(d => d.CreatedByUser)
                      .WithMany()
                      .HasForeignKey(d => d.CreatedByUserId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(d => new { d.EventId, d.Code }).IsUnique();
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

            // Cấu hình bảng refund_requests (Jira Story S-48)
            modelBuilder.Entity<RefundRequest>(entity =>
            {
                entity.ToTable("refund_requests");
                entity.HasKey(r => r.Id);

                entity.Property(r => r.Amount).IsRequired();
                entity.Property(r => r.Currency).IsRequired().HasMaxLength(10).HasDefaultValue("VND");
                entity.Property(r => r.IdempotencyKey).IsRequired().HasMaxLength(100);
                entity.Property(r => r.Reason).HasMaxLength(500);
                entity.Property(r => r.Status)
                      .HasConversion(
                          v => v.ToString(),
                          v => Enum.Parse<RefundStatus>(v));
                entity.Property(r => r.ErrorCode).HasMaxLength(100);
                entity.Property(r => r.ErrorMessage).HasMaxLength(2000);
                entity.Property(r => r.TransactionId).HasMaxLength(100);
                entity.Property(r => r.RefundTransactionId).HasMaxLength(100);
                entity.Property(r => r.AccountingNotificationError).HasMaxLength(2000);
                entity.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(r => r.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(r => r.Order)
                      .WithMany()
                      .HasForeignKey(r => r.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(r => r.OrderId);
                entity.HasIndex(r => r.IdempotencyKey).IsUnique().HasDatabaseName("IX_refund_requests_IdempotencyKey_Unique");
                entity.HasIndex(r => r.Status);
                entity.HasIndex(r => r.NextRetryAt);
            });

            // Cấu hình bảng refunds (Story S-45)
            modelBuilder.Entity<Refund>(entity =>
            {
                entity.ToTable("refunds");
                entity.HasKey(r => r.Id);

                entity.Property(r => r.Amount).IsRequired();
                entity.Property(r => r.Status).IsRequired().HasMaxLength(20);
                entity.Property(r => r.FailureReason).HasMaxLength(500);
                entity.Property(r => r.ConfirmedByName).HasMaxLength(200);
                entity.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
                entity.Property(r => r.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(r => r.Order)
                      .WithMany(o => o.Refunds)
                      .HasForeignKey(r => r.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                // Story S-45: mỗi đơn chỉ có tối đa một yêu cầu hoàn tiền (chống bấm lặp)
                entity.HasIndex(r => r.OrderId)
                      .IsUnique()
                      .HasDatabaseName("IX_refunds_OrderId_Unique");
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

                // Story S-57: Người sở hữu hiện tại và số lần chuyển nhượng
                entity.Property(t => t.TransferCount).HasDefaultValue(0);

                entity.HasOne(t => t.CurrentUser)
                      .WithMany()
                      .HasForeignKey(t => t.CurrentUserId)
                      .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(t => t.CurrentUserId);
            });

            // Cấu hình bảng chuyển nhượng vé (Story S-57)
            modelBuilder.Entity<TicketTransfer>(entity =>
            {
                entity.ToTable("ticket_transfers");
                entity.HasKey(tt => tt.Id);

                entity.Property(tt => tt.FromUserEmail).IsRequired().HasMaxLength(150);
                entity.Property(tt => tt.ToUserEmail).IsRequired().HasMaxLength(150);
                entity.Property(tt => tt.OldTicketCode).IsRequired().HasMaxLength(64);
                entity.Property(tt => tt.NewTicketCode).IsRequired().HasMaxLength(64);
                entity.Property(tt => tt.Status)
                      .HasConversion(v => v.ToString(), v => Enum.Parse<TransferStatus>(v))
                      .HasMaxLength(20);
                entity.Property(tt => tt.Note).HasMaxLength(500);
                entity.Property(tt => tt.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(tt => tt.Ticket)
                      .WithMany()
                      .HasForeignKey(tt => tt.TicketId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(tt => tt.FromUser)
                      .WithMany()
                      .HasForeignKey(tt => tt.FromUserId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(tt => tt.ToUser)
                      .WithMany()
                      .HasForeignKey(tt => tt.ToUserId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(tt => tt.TicketId);
                entity.HasIndex(tt => tt.FromUserId);
                entity.HasIndex(tt => tt.ToUserId);
                entity.HasIndex(tt => tt.OldTicketCode);
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

            // S-43: Cấu hình bảng SystemAlerts lưu lịch sử cảnh báo hệ thống
            modelBuilder.Entity<SystemAlert>(entity =>
            {
                entity.ToTable("SystemAlerts");
                entity.HasKey(a => a.Id);
                entity.Property(a => a.Fingerprint).IsRequired().HasMaxLength(128);
                entity.Property(a => a.AlertName).IsRequired().HasMaxLength(200);
                entity.Property(a => a.Severity).IsRequired().HasMaxLength(50);
                entity.Property(a => a.Summary).IsRequired().HasMaxLength(1000);
                entity.Property(a => a.Status).IsRequired().HasMaxLength(50);
                entity.Property(a => a.StartedAt).IsRequired();
                entity.Property(a => a.ResolvedAt).IsRequired(false);
                entity.Property(a => a.CreatedAt).IsRequired();
                entity.Property(a => a.LastUpdatedAt).IsRequired();

                // Partial unique index: Chống duplicate bản ghi cùng Fingerprint đang ở trạng thái 'firing'
                entity.HasIndex(a => a.Fingerprint)
                    .IsUnique()
                    .HasFilter("\"Status\" = 'firing'")
                    .HasDatabaseName("IX_SystemAlerts_Fingerprint_Firing");

                entity.HasIndex(a => a.Status)
                    .HasDatabaseName("IX_SystemAlerts_Status");

                entity.HasIndex(a => a.LastUpdatedAt)
                    .HasDatabaseName("IX_SystemAlerts_LastUpdatedAt");
            });

            // Cấu hình bảng yêu cầu xoá dữ liệu cá nhân theo NĐ 13/2023/NĐ-CP (Story S-53)
            modelBuilder.Entity<DataDeletionRequest>(entity =>
            {
                entity.ToTable("data_deletion_requests");
                entity.HasKey(r => r.Id);
                entity.Property(r => r.RequesterEmail).IsRequired().HasMaxLength(255);
                entity.Property(r => r.RequesterName).HasMaxLength(255);
                entity.Property(r => r.Reason).HasMaxLength(1000);
                entity.Property(r => r.AdminNote).HasMaxLength(1000);
                entity.Property(r => r.Status)
                    .HasConversion<string>()
                    .HasMaxLength(50);
                entity.Property(r => r.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(r => r.User)
                    .WithMany(u => u.DataDeletionRequests)
                    .HasForeignKey(r => r.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(r => r.ReviewedByUser)
                    .WithMany()
                    .HasForeignKey(r => r.ReviewedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<ReconciliationSession>(entity =>
            {
                entity.ToTable("reconciliation_sessions");
                entity.HasKey(x => x.Id);
                entity.Property(x => x.SessionCode).IsRequired().HasMaxLength(100);
                entity.Property(x => x.SourceType).IsRequired().HasMaxLength(50);
                entity.Property(x => x.Status).IsRequired().HasMaxLength(50);
                entity.Property(x => x.ExecutedBy).HasMaxLength(200);
                entity.HasIndex(x => x.SessionCode);
                entity.HasIndex(x => x.ExecutedAt);
            });

            modelBuilder.Entity<ShowtimeWaitlist>(entity =>
            {
                entity.ToTable("ShowtimeWaitlists");
                entity.HasKey(w => w.Id);
                entity.Property(w => w.Email).IsRequired().HasMaxLength(255);
                entity.Property(w => w.FullName).HasMaxLength(255);
                entity.Property(w => w.PhoneNumber).HasMaxLength(50);
                entity.Property(w => w.Status).IsRequired().HasMaxLength(50).HasDefaultValue("WAITING");
                entity.Property(w => w.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

                entity.HasOne(w => w.Showtime)
                      .WithMany()
                      .HasForeignKey(w => w.ShowtimeId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(w => w.User)
                      .WithMany()
                      .HasForeignKey(w => w.UserId)
                      .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(w => new { w.ShowtimeId, w.Status, w.CreatedAt });
                entity.HasIndex(w => new { w.ShowtimeId, w.Email });
            });

            // S-43: Bảng lưu vết lưu lượng HTTP Request thực tế vào Shared PostgreSQL
            modelBuilder.Entity<RequestTelemetry>(entity =>
            {
                entity.ToTable("RequestTelemetries");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Method).IsRequired().HasMaxLength(10);
                entity.Property(e => e.Route).IsRequired().HasMaxLength(300);
                entity.Property(e => e.Environment).IsRequired().HasMaxLength(50);
                entity.Property(e => e.InstanceId).HasMaxLength(100);
                entity.HasIndex(e => e.OccurredAtUtc);
                entity.HasIndex(e => new { e.OccurredAtUtc, e.StatusCode });
            });

            // S-43: Bảng lưu vết sự kiện nghiệp vụ (SeatConflict, WebhookRejected) vào Shared PostgreSQL
            modelBuilder.Entity<MonitoringEvent>(entity =>
            {
                entity.ToTable("MonitoringEvents");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Type).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Category).HasMaxLength(50);
                entity.Property(e => e.ReferenceId).HasMaxLength(100);
                entity.Property(e => e.Environment).IsRequired().HasMaxLength(50);
                entity.HasIndex(e => e.OccurredAtUtc);
                entity.HasIndex(e => new { e.OccurredAtUtc, e.Type });
            });

            // S-43: Bảng lưu cảnh báo hệ thống được đánh giá tự động dựa trên metrics
            modelBuilder.Entity<MonitoringAlert>(entity =>
            {
                entity.ToTable("MonitoringAlerts");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Metric).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Severity).IsRequired().HasMaxLength(20);
                entity.HasIndex(e => e.IsActive);
                entity.HasIndex(e => new { e.IsActive, e.Metric });
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
                "cancelled" => ShowtimeStatus.Cancelled,
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
