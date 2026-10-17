using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử toàn diện cho Story S-51:
    /// Đối soát giao dịch hệ thống với sao kê cổng thanh toán do người dùng tự tải lên file CSV.
    /// </summary>
    public class ReconciliationS51Tests
    {
        private AppDbContext CreateSqliteDbContext(string dbPath)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var context = new AppDbContext(options);
            context.Database.EnsureCreated();
            return context;
        }

        private async Task<(User User, Event Event, Showtime Showtime, Order Order)> SeedOrderAsync(AppDbContext context, int amount = 100000)
        {
            var userId = Guid.NewGuid();
            var user = new User
            {
                Id = userId,
                Username = $"user_{Guid.NewGuid():N}",
                Email = $"user_{Guid.NewGuid():N}@test.com",
                PasswordHash = "hashed"
            };
            context.Users.Add(user);

            var evt = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Test Event",
                Location = "Hall A",
                TotalSeats = 100,
                OwnerId = userId
            };
            context.Events.Add(evt);

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = evt.Id,
                StartTime = DateTime.UtcNow.AddDays(1),
                EndTime = DateTime.UtcNow.AddDays(1).AddHours(2)
            };
            context.Showtimes.Add(showtime);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = showtime.Id,
                TotalAmount = amount,
                Status = OrderStatus.Paid,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
            };
            context.Orders.Add(order);
            await context.SaveChangesAsync();
            return (user, evt, showtime, order);
        }

        #region 1. CSV HỢP LỆ (Valid CSV Parsing)

        [Fact]
        public void S51_ParseCsv_ValidStandardCsv_ReturnsAllItemsCorrectly()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvContent = @"TransactionId,Amount,TransactionDate,Description
TX_CSV_01,250000,2026-10-15 10:00:00,Thanh toan ve rock
TX_CSV_02,500000,2026-10-15 11:30:00,Thanh toan VIP";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvContent);

            // Assert
            Assert.Empty(errors);
            Assert.Equal(2, items.Count);
            Assert.Equal("TX_CSV_01", items[0].TransactionId);
            Assert.Equal(250000, items[0].Amount);
            Assert.Equal("Thanh toan ve rock", items[0].Description);
            Assert.Equal("TX_CSV_02", items[1].TransactionId);
            Assert.Equal(500000, items[1].Amount);
        }

        [Fact]
        public void S51_ParseCsv_AlternativeSeparators_SemicolonAndTab_HandledCorrectly()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);

            // Semicolon separator
            string semicolonCsv = "TransactionId;Amount;TransactionDate;Description\nTX_SEMI_01;150000;2026-10-15 10:00:00;Semicolon test";
            var (semiItems, semiErrors) = service.ParseCsvStatement(semicolonCsv);
            Assert.Empty(semiErrors);
            Assert.Single(semiItems);
            Assert.Equal("TX_SEMI_01", semiItems[0].TransactionId);
            Assert.Equal(150000, semiItems[0].Amount);

            // Tab separator
            string tabCsv = "TransactionId\tAmount\tTransactionDate\tDescription\nTX_TAB_01\t300000\t2026-10-15 10:00:00\tTab test";
            var (tabItems, tabErrors) = service.ParseCsvStatement(tabCsv);
            Assert.Empty(tabErrors);
            Assert.Single(tabItems);
            Assert.Equal("TX_TAB_01", tabItems[0].TransactionId);
            Assert.Equal(300000, tabItems[0].Amount);
        }

        [Fact]
        public void S51_ParseCsv_QuotedFields_WithCommasAndQuotes_ParsesCorrectly()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvWithQuotes = @"TransactionId,Amount,TransactionDate,Description
""TX_QUOTE_01"",200000,2026-10-15 10:00:00,""Thanh toan, ghe A1, """"VIP""""""";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvWithQuotes);

            // Assert
            Assert.Empty(errors);
            Assert.Single(items);
            Assert.Equal("TX_QUOTE_01", items[0].TransactionId);
            Assert.Equal(200000, items[0].Amount);
            Assert.Equal("Thanh toan, ghe A1, \"VIP\"", items[0].Description);
        }

        [Fact]
        public void S51_ParseCsv_VariousAmountFormats_DotsCommasVnd_ParsesIntegerCorrectly()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvAmounts = @"TransactionId,Amount,TransactionDate
TX_AMT_01,100.000,2026-10-15 10:00:00
TX_AMT_02,""200,000"",2026-10-15 10:00:00
TX_AMT_03,350000 VND,2026-10-15 10:00:00
TX_AMT_04,450000 đ,2026-10-15 10:00:00";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvAmounts);

            // Assert
            Assert.Empty(errors);
            Assert.Equal(4, items.Count);
            Assert.Equal(100000, items[0].Amount);
            Assert.Equal(200000, items[1].Amount);
            Assert.Equal(350000, items[2].Amount);
            Assert.Equal(450000, items[3].Amount);
        }

        [Fact]
        public void S51_ParseCsv_VietnameseHeaders_DetectedAutomatically()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string vnCsv = @"Mã giao dịch,Số tiền,Ngày giao dịch,Ghi chú,Mã đơn hàng
TX_VN_01,150000,2026-10-15 10:00:00,Don hang mua ve,889901
TX_VN_02,300000,2026-10-15 11:00:00,Thanh toan live concert,889902";

            // Act
            var (items, errors) = service.ParseCsvStatement(vnCsv);

            // Assert
            Assert.Empty(errors);
            Assert.Equal(2, items.Count);
            Assert.Equal("TX_VN_01", items[0].TransactionId);
            Assert.Equal(150000, items[0].Amount);
            Assert.Equal(889901, items[0].OrderCode);
            Assert.Equal("TX_VN_02", items[1].TransactionId);
            Assert.Equal(300000, items[1].Amount);
            Assert.Equal(889902, items[1].OrderCode);
        }

        [Fact]
        public void S51_ParseCsv_RealisticMerchantStatement_WithHeadersMetadataAndFormattedAmounts_ParsesAccurately()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string realCsv = @"# ==============================================================================
# CỔNG THANH TOÁN TRỰC TUYẾN PAYOS - BÁO CÁO SAO KÊ ĐỐI SOÁT GIAO DỊCH (MERCHANT STATEMENT)
# Đơn vị thụ hưởng: CÔNG TY CỔ PHẦN CÔNG NGHỆ VÀ VÉ SỰ KIỆN EVENTPULSE
# Mã ĐVCNT (Merchant ID): MER-EVENTPULSE-88392
# Kỳ sao kê: 01/10/2026 - 17/10/2026
# ==============================================================================
Mã giao dịch cổng,Mã đơn hàng,Số tiền,Thời gian giao dịch,Nội dung thanh toán,Kênh thanh toán,Trạng thái
PAYOS20261015001,100001,""200,000"",2026-10-15 09:15:20,""Thanh toan don hang #100001 ve concert Anh Trai Say Hi"",VietQR - MBBank,Thành công
PAYOS20261015002,100002,""250,000"",2026-10-15 10:22:45,""Thanh toan don hang #100002 ve Festival Am Nhac"",VietQR - Vietcombank,Thành công
PAYOS20261015003,100003,""500,000"",2026-10-15 14:05:10,""Thanh toan don hang #100003 2 ve VIP Khu Vuc A"",The noi dia Napas,Thành công
PAYOS20261016099,999999,""350,000"",2026-10-16 16:40:00,""Thanh toan truc tiep qua cong PayOS (Khong co tren he thong)"",The quoc te Visa,Thành công";

            // Act
            var (items, errors) = service.ParseCsvStatement(realCsv);

            // Assert
            Assert.Empty(errors);
            Assert.Equal(4, items.Count);
            Assert.Equal("PAYOS20261015001", items[0].TransactionId);
            Assert.Equal(100001, items[0].OrderCode);
            Assert.Equal(200000, items[0].Amount);
            Assert.Equal("PAYOS20261016099", items[3].TransactionId);
            Assert.Equal(350000, items[3].Amount);
        }

        #endregion

        #region 2. CSV LỖI (Invalid CSV & Error Handling)

        [Fact]
        public void S51_ParseCsv_EmptyOrWhitespaceCsv_ReturnsEmptyWithWarning()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);

            // Act
            var (itemsEmpty, errorsEmpty) = service.ParseCsvStatement(string.Empty);
            var (itemsWhite, errorsWhite) = service.ParseCsvStatement("   \r\n\t  ");

            // Assert
            Assert.Empty(itemsEmpty);
            Assert.Contains(errorsEmpty, e => e.Contains("Tệp sao kê rỗng"));
            Assert.Empty(itemsWhite);
            Assert.Contains(errorsWhite, e => e.Contains("Tệp sao kê rỗng"));
        }

        [Fact]
        public void S51_ParseCsv_MissingRequiredColumns_ReturnsError()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvOneCol = "TransactionId\nTX_ONLY_ONE_COL";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvOneCol);

            // Assert
            Assert.Empty(items);
            Assert.Contains(errors, e => e.Contains("Không đủ số cột tối thiểu"));
        }

        [Fact]
        public void S51_ParseCsv_EmptyTransactionId_ReturnsLineError()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvEmptyTx = "TransactionId,Amount\n,250000";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvEmptyTx);

            // Assert
            Assert.Empty(items);
            Assert.Contains(errors, e => e.Contains("Mã giao dịch (TransactionId) bị trống"));
        }

        [Fact]
        public void S51_ParseCsv_InvalidAmountString_ReturnsLineError()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            string csvInvalidAmt = "TransactionId,Amount\nTX_ERR_01,khong_phai_so";

            // Act
            var (items, errors) = service.ParseCsvStatement(csvInvalidAmt);

            // Assert
            Assert.Empty(items);
            Assert.Contains(errors, e => e.Contains("Số tiền 'khong_phai_so' không hợp lệ"));
        }

        [Fact]
        public async Task S51_Reconcile_DuplicateTransactionIdInCsv_PreservesFirstAndWarns()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_csv_dup_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;

                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_DUP_01",
                    RawPayload = "{\"data\": {\"amount\": 200000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // CSV gửi 2 dòng có cùng mã giao dịch TX_DUP_01 với số tiền khác nhau
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TX_DUP_01", Amount = 200000, TransactionDate = now },
                    new() { TransactionId = "TX_DUP_01", Amount = 999999, TransactionDate = now.AddMinutes(5) }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Phải có cảnh báo về việc trùng lặp mã giao dịch phía cổng và giữ bản ghi đầu tiên
                Assert.Contains(summary.Warnings, w => w.Contains("Sao kê cổng có mã giao dịch trùng lặp: 'TX_DUP_01'"));
                Assert.Equal(1, summary.MatchedCount);
                Assert.Equal(200000, summary.MatchedGatewayAmount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 3. ĐỦ 4 NHÓM KẾT QUẢ (All 4 Result Groups: Matched, SystemOnly, GatewayOnly, AmountMismatch)

        [Fact]
        public async Task S51_Reconcile_AllFourGroups_IdentifiedCorrectly()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_four_groups_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;

                // 1. Giao dịch khớp: có ở cả 2 bên với cùng số tiền 200,000 đ
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_MATCHED_01",
                    RawPayload = "{\"data\": {\"amount\": 200000}}",
                    CreatedAt = now.AddHours(-3)
                });

                // 2. Giao dịch chỉ có ở hệ thống: có trong DB, không có trong sao kê cổng
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_SYSTEM_ONLY_02",
                    RawPayload = "{\"data\": {\"amount\": 150000}}",
                    CreatedAt = now.AddHours(-2)
                });

                // 3. Giao dịch lệch số tiền: hệ thống 300,000 đ, sao kê cổng 350,000 đ
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_MISMATCH_03",
                    RawPayload = "{\"data\": {\"amount\": 300000}}",
                    CreatedAt = now.AddHours(-1)
                });

                await context.SaveChangesAsync();

                // Dữ liệu sao kê người dùng tải lên:
                // - TX_MATCHED_01: 200,000 (khớp)
                // - TX_MISMATCH_03: 350,000 (lệch tiền +50k)
                // - TX_GATEWAY_ONLY_04: 100,000 (chỉ có ở cổng)
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TX_MATCHED_01", Amount = 200000, TransactionDate = now.AddHours(-3) },
                    new() { TransactionId = "TX_MISMATCH_03", Amount = 350000, TransactionDate = now.AddHours(-1) },
                    new() { TransactionId = "TX_GATEWAY_ONLY_04", Amount = 100000, TransactionDate = now }
                };

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Act
                var summary = await service.ReconcileAsync(
                    now.AddDays(-1),
                    now.AddDays(1),
                    gatewayItems);

                // Assert
                Assert.NotNull(summary);
                Assert.Equal(3, summary.TotalInternalCount);
                Assert.Equal(3, summary.TotalGatewayCount);

                // 🟢 Nhóm 1: Khớp hoàn toàn
                Assert.Equal(1, summary.MatchedCount);
                var matched = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_MATCHED_01");
                Assert.NotNull(matched);
                Assert.Equal(ReconciliationGroups.Matched, matched.Group);
                Assert.Equal(200000, matched.InternalAmount);
                Assert.Equal(200000, matched.GatewayAmount);
                Assert.Equal(0, matched.Difference);

                // 🔴 Nhóm 2: Chỉ có ở hệ thống
                Assert.Equal(1, summary.SystemOnlyCount);
                var sysOnly = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_SYSTEM_ONLY_02");
                Assert.NotNull(sysOnly);
                Assert.Equal(ReconciliationGroups.SystemOnly, sysOnly.Group);
                Assert.Equal(150000, sysOnly.InternalAmount);
                Assert.Null(sysOnly.GatewayAmount);
                Assert.Equal(-150000, sysOnly.Difference);

                // 🟠 Nhóm 3: Chỉ có ở cổng
                Assert.Equal(1, summary.GatewayOnlyCount);
                var gwOnly = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_GATEWAY_ONLY_04");
                Assert.NotNull(gwOnly);
                Assert.Equal(ReconciliationGroups.GatewayOnly, gwOnly.Group);
                Assert.Null(gwOnly.InternalAmount);
                Assert.Equal(100000, gwOnly.GatewayAmount);
                Assert.Equal(100000, gwOnly.Difference);

                // 🟡 Nhóm 4: Lệch số tiền
                Assert.Equal(1, summary.AmountMismatchCount);
                var mismatch = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_MISMATCH_03");
                Assert.NotNull(mismatch);
                Assert.Equal(ReconciliationGroups.AmountMismatch, mismatch.Group);
                Assert.Equal(300000, mismatch.InternalAmount);
                Assert.Equal(350000, mismatch.GatewayAmount);
                Assert.Equal(50000, mismatch.Difference);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconcile_SummaryMetrics_TotalsMatchComponentSums()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_metrics_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;

                context.PaymentEvents.Add(new PaymentEvent { Id = Guid.NewGuid(), TransactionId = "TX_M1", RawPayload = "{\"data\":{\"amount\":100000}}", CreatedAt = now });
                context.PaymentEvents.Add(new PaymentEvent { Id = Guid.NewGuid(), TransactionId = "TX_M2", RawPayload = "{\"data\":{\"amount\":200000}}", CreatedAt = now });
                context.PaymentEvents.Add(new PaymentEvent { Id = Guid.NewGuid(), TransactionId = "TX_SYS_ONLY", RawPayload = "{\"data\":{\"amount\":150000}}", CreatedAt = now });
                context.PaymentEvents.Add(new PaymentEvent { Id = Guid.NewGuid(), TransactionId = "TX_MISMATCH", RawPayload = "{\"data\":{\"amount\":400000}}", CreatedAt = now });
                await context.SaveChangesAsync();

                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TX_M1", Amount = 100000, TransactionDate = now },
                    new() { TransactionId = "TX_M2", Amount = 200000, TransactionDate = now },
                    new() { TransactionId = "TX_MISMATCH", Amount = 450000, TransactionDate = now }, // Lệch +50k
                    new() { TransactionId = "TX_GW_ONLY", Amount = 300000, TransactionDate = now }
                };

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert
                Assert.Equal(4, summary.TotalInternalCount);
                Assert.Equal(4, summary.TotalGatewayCount);
                Assert.Equal(2, summary.MatchedCount);
                Assert.Equal(1, summary.SystemOnlyCount);
                Assert.Equal(1, summary.GatewayOnlyCount);
                Assert.Equal(1, summary.AmountMismatchCount);

                Assert.Equal(100000 + 200000 + 150000 + 400000, summary.TotalInternalAmount);
                Assert.Equal(100000 + 200000 + 450000 + 300000, summary.TotalGatewayAmount);

                Assert.Equal(300000, summary.MatchedInternalAmount);
                Assert.Equal(300000, summary.MatchedGatewayAmount);
                Assert.Equal(150000, summary.SystemOnlyAmount);
                Assert.Equal(300000, summary.GatewayOnlyAmount);
                Assert.Equal(400000, summary.AmountMismatchInternalAmount);
                Assert.Equal(450000, summary.AmountMismatchGatewayAmount);
                Assert.Equal(50000, summary.AmountMismatchNetDifference);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 4. ĐỐI SOÁT THEO MÃ GIAO DỊCH & KHÔNG TỰ GHÉP THEO SỐ TIỀN

        [Fact]
        public async Task S51_Reconcile_SameAmountDifferentTxId_DoesNotAutoMatchByAmount_ClassifiedAsSystemOnlyAndGatewayOnly()
        {
            // Yêu cầu 2: Dữ liệu mã giao dịch không rõ ràng hoặc khác nhau KHÔNG được tự ghép theo số tiền
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_no_amount_guess_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;

                // Hệ thống có giao dịch TX_INTERNAL_999 với số tiền 250,000 đ
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_INTERNAL_999",
                    RawPayload = "{\"data\": {\"amount\": 250000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                // Sao kê cổng có giao dịch TX_GATEWAY_888 CŨNG CÓ SỐ TIỀN 250,000 đ nhưng mã giao dịch hoàn toàn khác
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new()
                    {
                        TransactionId = "TX_GATEWAY_888",
                        Amount = 250000, // Cùng số tiền nhưng khác mã hoàn toàn
                        TransactionDate = now
                    }
                };

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Tuyệt đối KHÔNG được tự động ghép theo số tiền!
                // Phải được phân loại là 1 SystemOnly và 1 GatewayOnly, MatchedCount phải = 0
                Assert.Equal(0, summary.MatchedCount);
                Assert.Equal(1, summary.SystemOnlyCount);
                Assert.Equal(1, summary.GatewayOnlyCount);
                Assert.Equal(0, summary.AmountMismatchCount);

                var sysItem = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_INTERNAL_999");
                Assert.NotNull(sysItem);
                Assert.Equal(ReconciliationGroups.SystemOnly, sysItem.Group);

                var gwItem = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_GATEWAY_888");
                Assert.NotNull(gwItem);
                Assert.Equal(ReconciliationGroups.GatewayOnly, gwItem.Group);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconcile_RealGatewayCsv_MatchesViaOrderCodeFallback()
        {
            // Arrange: Giả lập DB có giao dịch nội bộ với TransactionId khác mã tham chiếu sao kê ngân hàng/VNPay
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_fallback_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var orderId = Guid.NewGuid();
                var userId = Guid.NewGuid();

                var user = new User
                {
                    Id = userId,
                    Username = $"user_{Guid.NewGuid():N}",
                    Email = $"user_{Guid.NewGuid():N}@test.com",
                    PasswordHash = "hashed"
                };
                context.Users.Add(user);

                var evt = new Event
                {
                    Id = Guid.NewGuid(),
                    Title = "Test Event Real Gateway",
                    Location = "Hall B",
                    TotalSeats = 50,
                    OwnerId = userId
                };
                context.Events.Add(evt);

                var showtime = new Showtime
                {
                    Id = Guid.NewGuid(),
                    EventId = evt.Id,
                    StartTime = DateTime.UtcNow.AddDays(1),
                    EndTime = DateTime.UtcNow.AddDays(1).AddHours(2)
                };
                context.Showtimes.Add(showtime);

                var order = new Order
                {
                    Id = orderId,
                    UserId = userId,
                    ShowtimeId = showtime.Id,
                    TotalAmount = 180000,
                    Status = OrderStatus.Paid,
                    CreatedAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2),
                    ExpiresAt = now.AddHours(2)
                };
                context.Orders.Add(order);

                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    TransactionId = "INT_TXN_BANK_QR_01",
                    OrderCode = 998877,
                    Amount = 180000,
                    Status = "PAID",
                    CreatedAt = now.AddHours(-2),
                    UpdatedAt = now.AddHours(-2)
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Sao kê ngân hàng/cổng thanh toán thực tế ghi mã đơn hàng vào cột TransactionId
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new()
                    {
                        TransactionId = "998877", // Mã đơn hàng trong sao kê thực tế
                        Amount = 180000,
                        TransactionDate = now.AddHours(-2),
                        Description = "Chuyen khoan thanh toan don hang 998877"
                    }
                };

                // Act: Chạy đối soát
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Hệ thống tự động nhận diện và ghép thành công nhóm Matched mà không bị tách thành SystemOnly + GatewayOnly
                Assert.Equal(1, summary.MatchedCount);
                Assert.Equal(0, summary.SystemOnlyCount);
                Assert.Equal(0, summary.GatewayOnlyCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconcile_MultipleInternalAttempts_DoesNotAutoMatch_AddsWarning()
        {
            // Kiểm tra: Đơn hàng có nhiều lần thanh toán thử với TransactionId khác nhau -> Cảnh báo mơ hồ, không tự ghép
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_ambig_internal_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_ATTEMPT_1",
                    RawPayload = "{\"data\": {\"orderCode\": 776655, \"amount\": 200000}}",
                    CreatedAt = now.AddMinutes(-10)
                });

                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_ATTEMPT_2",
                    RawPayload = "{\"data\": {\"orderCode\": 776655, \"amount\": 200000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Sao kê cổng tham chiếu đến mã đơn 776655 nhưng mã TransactionId là mã khác
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new()
                    {
                        TransactionId = "GW_REF_776655",
                        OrderCode = 776655,
                        Amount = 200000,
                        TransactionDate = now
                    }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Phải có cảnh báo về tính mơ hồ, không tự động ghép
                Assert.Contains(summary.Warnings, w => w.Contains("có 2 giao dịch/lần thử nội bộ"));
                Assert.Equal(0, summary.MatchedCount);
                Assert.Equal(2, summary.SystemOnlyCount);
                Assert.Equal(1, summary.GatewayOnlyCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconcile_MultipleGatewayLinesForSameOrder_DoesNotAutoMatch_AddsWarning()
        {
            // Kiểm tra: Sao kê cổng có nhiều dòng cùng tham chiếu 1 mã đơn hàng -> Cảnh báo mơ hồ, không tự ghép
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_ambig_gw_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var (_, _, _, order) = await SeedOrderAsync(context, 150000);
                var orderId = order.Id;

                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    TransactionId = "INT_TX_SINGLE",
                    OrderCode = 334455,
                    Amount = 150000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Sao kê cổng có 2 dòng khác nhau cùng tham chiếu mã đơn 334455
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "GW_LINE_A", OrderCode = 334455, Amount = 150000, TransactionDate = now },
                    new() { TransactionId = "GW_LINE_B", OrderCode = 334455, Amount = 150000, TransactionDate = now }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Phải có cảnh báo về tính mơ hồ phía cổng
                Assert.Contains(summary.Warnings, w => w.Contains("có 2 dòng giao dịch cùng tham chiếu đơn hàng #334455"));
                Assert.Equal(0, summary.MatchedCount);
                Assert.Equal(1, summary.SystemOnlyCount);
                Assert.Equal(2, summary.GatewayOnlyCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconcile_MultipleAttempts_MatchesExactTransactionId_WithoutAmbiguity()
        {
            // Kiểm tra: Đơn hàng có nhiều lần thử, nhưng sao kê cổng có mã TransactionId trùng khớp chính xác 1 lần thử
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_exact_match_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_ATTEMPT_FAILED",
                    RawPayload = "{\"data\": {\"orderCode\": 889977, \"amount\": 250000}}",
                    CreatedAt = now.AddMinutes(-5)
                });

                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_ATTEMPT_SUCCESS",
                    RawPayload = "{\"data\": {\"orderCode\": 889977, \"amount\": 250000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Sao kê cổng có đúng mã TX_ATTEMPT_SUCCESS
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new()
                    {
                        TransactionId = "TX_ATTEMPT_SUCCESS",
                        OrderCode = 889977,
                        Amount = 250000,
                        TransactionDate = now
                    }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Phải ghép thành công TX_ATTEMPT_SUCCESS vào nhóm Matched
                Assert.Equal(1, summary.MatchedCount);
                var matched = summary.Items.FirstOrDefault(i => i.TransactionId == "TX_ATTEMPT_SUCCESS");
                Assert.NotNull(matched);
                Assert.Equal(ReconciliationGroups.Matched, matched.Group);

                // Lần thử còn lại không có trên cổng thanh toán nên được ghi nhận vào SystemOnly
                Assert.Equal(1, summary.SystemOnlyCount);
                Assert.Equal(0, summary.GatewayOnlyCount);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Reconciliation_IneligibleTransactions_ExcludedFromMismatchGroups()
        {
            // Yêu cầu 6: Giao dịch đang chờ (PENDING), thất bại (FAILED, CANCELLED) hoặc thiếu định danh
            // được đưa vào nhóm Ineligible và không làm sai lệch SystemOnly hay AmountMismatch
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_ineligible_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var userId = Guid.NewGuid();
                var user = new User { Id = userId, Username = "u_inel", Email = "inel@test.com", PasswordHash = "h" };
                context.Users.Add(user);
                var evt = new Event { Id = Guid.NewGuid(), Title = "E", Location = "L", TotalSeats = 10, OwnerId = userId };
                context.Events.Add(evt);
                var showtime = new Showtime { Id = Guid.NewGuid(), EventId = evt.Id, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
                context.Showtimes.Add(showtime);

                // 1. Giao dịch PENDING trong PaymentTransactions
                var orderPending = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 100000, Status = OrderStatus.Pending, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(orderPending);
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderPending.Id,
                    OrderCode = 5001,
                    TransactionId = "TX_STATUS_PENDING",
                    Amount = 100000,
                    Status = "PENDING",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                // 2. Giao dịch FAILED trong PaymentTransactions
                var orderFailed = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 200000, Status = OrderStatus.Cancelled, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(orderFailed);
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderFailed.Id,
                    OrderCode = 5002,
                    TransactionId = "TX_STATUS_FAILED",
                    Amount = 200000,
                    Status = "FAILED",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                // 3. Giao dịch CANCELLED
                var orderCancelled = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 300000, Status = OrderStatus.Cancelled, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(orderCancelled);
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderCancelled.Id,
                    OrderCode = 5003,
                    TransactionId = "TX_STATUS_CANCELLED",
                    Amount = 300000,
                    Status = "CANCELLED",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                // 4. Giao dịch có số tiền âm không hợp lệ (< 0 đ)
                var orderNegative = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 50000, Status = OrderStatus.Paid, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(orderNegative);
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderNegative.Id,
                    OrderCode = 5004,
                    TransactionId = "TX_STATUS_NEGATIVE_AMT",
                    Amount = -50000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                // 5. Giao dịch thành công bình thường (để kiểm tra khớp)
                var orderPaid = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 500000, Status = OrderStatus.Paid, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(orderPaid);
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderPaid.Id,
                    OrderCode = 5005,
                    TransactionId = "TX_VALID_PAID_05",
                    Amount = 500000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Sao kê cổng chỉ có giao dịch thành công TX_VALID_PAID_05
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TX_VALID_PAID_05", Amount = 500000, TransactionDate = now }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert
                Assert.NotNull(summary);
                Assert.Equal(1, summary.MatchedCount);
                Assert.Equal(0, summary.SystemOnlyCount);       // 3 giao dịch pending/failed/cancelled KHÔNG bị tính vào SystemOnly
                Assert.Equal(0, summary.AmountMismatchCount);   // Không bị tính vào AmountMismatch
                Assert.Equal(0, summary.GatewayOnlyCount);
                Assert.Equal(4, summary.IneligibleCount);        // 4 giao dịch chưa đủ điều kiện

                // Kiểm tra các item Ineligible
                var ineligItems = summary.Items.Where(i => i.Group == ReconciliationGroups.Ineligible).ToList();
                Assert.Equal(4, ineligItems.Count);
                Assert.All(ineligItems, item => Assert.False(string.IsNullOrWhiteSpace(item.Notes)));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Deduplication_And_MergeSources_PrioritizesPaymentTransactionAmount()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_dedup_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var (_, _, _, order) = await SeedOrderAsync(context, 120000);
                var orderId = order.Id;

                // 1. PaymentEvent có amount = 100,000 đ
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_DUAL_SOURCE_01",
                    RawPayload = "{\"data\": {\"amount\": 100000, \"orderCode\": 12345}}",
                    CreatedAt = now.AddHours(-1)
                });

                // 2. PaymentTransaction cùng TransactionId nhưng có số tiền chuẩn 120,000 đ
                context.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderId,
                    OrderCode = 12345,
                    TransactionId = "TX_DUAL_SOURCE_01",
                    Amount = 120000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                });

                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                // Act
                var map = await service.GetInternalTransactionsAsync(now.AddDays(-1), now.AddDays(1));

                // Assert
                Assert.Single(map);
                var record = map["TX_DUAL_SOURCE_01"];
                Assert.Equal(120000, record.Amount); // Ưu tiên số tiền từ PaymentTransaction
                Assert.Equal("Both", record.Source);
                Assert.Equal(orderId, record.OrderId);
                Assert.Equal(12345, record.OrderCode);
                Assert.False(record.HasAmountWarning);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_MissingAmountInPayload_MarksWarning_DoesNotGuessZero()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_missing_amt_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;

                // Event không có trường amount trong payload
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_NO_AMOUNT",
                    RawPayload = "{\"code\": \"00\", \"desc\": \"success\"}",
                    CreatedAt = now
                });

                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TX_NO_AMOUNT", Amount = 150000, TransactionDate = now }
                };

                // Act
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert: Phân vào AmountMismatch và có cảnh báo thiếu số tiền nội bộ, không tự gán 0
                Assert.Equal(1, summary.AmountMismatchCount);
                var item = summary.Items.First(i => i.TransactionId == "TX_NO_AMOUNT");
                Assert.Null(item.InternalAmount);
                Assert.Contains("chưa xác định được số tiền nội bộ", item.Notes, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 5. CHỈ ĐỌC DỮ LIỆU NỘI BỘ (Read-only Integrity: No Business DB Mutation)

        [Fact]
        public async Task S51_BusinessDatabase_Untouched_NoDataPollution()
        {
            // Yêu cầu 5: Chỉ đọc dữ liệu giao dịch nội bộ để đối soát.
            // Tuyệt đối KHÔNG thay đổi trạng thái đơn hàng, thanh toán, ghế, vé hoặc hoàn tiền.
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_pure_db_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var userId = Guid.NewGuid();
                var user = new User { Id = userId, Username = "u_pure", Email = "pure@test.com", PasswordHash = "h" };
                context.Users.Add(user);
                var evt = new Event { Id = Guid.NewGuid(), Title = "Pure Event", Location = "Loc", TotalSeats = 10, OwnerId = userId };
                context.Events.Add(evt);
                var showtime = new Showtime { Id = Guid.NewGuid(), EventId = evt.Id, StartTime = DateTime.UtcNow.AddDays(1), EndTime = DateTime.UtcNow.AddDays(1).AddHours(2) };
                context.Showtimes.Add(showtime);

                var order = new Order { Id = Guid.NewGuid(), UserId = userId, ShowtimeId = showtime.Id, TotalAmount = 250000, Status = OrderStatus.Paid, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(2) };
                context.Orders.Add(order);

                var pt = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    OrderCode = 998877,
                    TransactionId = "TX_PURE_TEST_01",
                    Amount = 250000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                context.PaymentTransactions.Add(pt);

                var pe = new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_PURE_TEST_01",
                    RawPayload = "{\"data\": {\"amount\": 250000}}",
                    CreatedAt = now
                };
                context.PaymentEvents.Add(pe);
                await context.SaveChangesAsync();

                // Lưu lại snapshot số lượng ban đầu của các bảng nghiệp vụ
                int countOrdersBefore = await context.Orders.CountAsync();
                int countTransactionsBefore = await context.PaymentTransactions.CountAsync();
                int countEventsBefore = await context.PaymentEvents.CountAsync();
                int countTicketsBefore = await context.Tickets.CountAsync();
                int countSeatsBefore = await context.Seats.CountAsync();
                int countRefundsBefore = await context.Refunds.CountAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                string csvContent = "TransactionId,Amount,TransactionDate,Description\nTX_PURE_TEST_01,250000,2026-10-15 10:00:00,Thanh toan";

                // Act 1: Parse CSV
                var (items, errors) = service.ParseCsvStatement(csvContent);
                Assert.Empty(errors);

                // Act 2: Chạy đối soát
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), items);
                Assert.Equal(1, summary.MatchedCount);

                // Act 3: Lưu phiên đối soát
                var session = await service.SaveReconciliationSessionAsync(now.AddDays(-1), now.AddDays(1), "USER_FILE_CSV", csvContent, summary, "auditor_user");
                Assert.NotNull(session);

                // Assert: Tuyệt đối KHÔNG một bảng nghiệp vụ nào bị thay đổi số lượng bản ghi
                Assert.Equal(countOrdersBefore, await context.Orders.CountAsync());
                Assert.Equal(countTransactionsBefore, await context.PaymentTransactions.CountAsync());
                Assert.Equal(countEventsBefore, await context.PaymentEvents.CountAsync());
                Assert.Equal(countTicketsBefore, await context.Tickets.CountAsync());
                Assert.Equal(countSeatsBefore, await context.Seats.CountAsync());
                Assert.Equal(countRefundsBefore, await context.Refunds.CountAsync());

                // Dữ liệu ban đầu không bị biến đổi trạng thái hoặc số tiền
                var ptAfter = await context.PaymentTransactions.FindAsync(pt.Id);
                Assert.NotNull(ptAfter);
                Assert.Equal(250000, ptAfter.Amount);
                Assert.Equal("PAID", ptAfter.Status);
                Assert.Equal("TX_PURE_TEST_01", ptAfter.TransactionId);

                // Chỉ bảng lịch sử đối soát reconciliation_sessions được thêm bản ghi
                Assert.True(await context.ReconciliationSessions.AnyAsync());
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 6. LỊCH SỬ ĐỐI SOÁT & CHI TIẾT PHIÊN (Session History & Details)

        [Fact]
        public async Task S51_SaveSession_And_GetRecentSessions_RecordsSessionHistory()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_session_history_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var now = DateTimeOffset.UtcNow;

                var summary = new ReconciliationSummaryDto
                {
                    FromDate = now.AddDays(-7),
                    ToDate = now,
                    ReconciledAt = now,
                    TotalInternalCount = 5,
                    TotalInternalAmount = 1000000,
                    TotalGatewayCount = 5,
                    TotalGatewayAmount = 1000000,
                    MatchedCount = 5,
                    MatchedInternalAmount = 1000000,
                    MatchedGatewayAmount = 1000000
                };

                // Act: Lưu phiên với nguồn USER_FILE_CSV
                var session = await service.SaveReconciliationSessionAsync(
                    summary.FromDate,
                    summary.ToDate,
                    "USER_FILE_CSV",
                    "TransactionId,Amount\nTX1,200000",
                    summary,
                    "auditor_test");

                // Assert
                Assert.NotNull(session);
                Assert.StartsWith("REC_", session.SessionCode);
                Assert.Equal("USER_FILE_CSV", session.SourceType);
                Assert.Equal("COMPLETED", session.Status);
                Assert.Equal(5, session.MatchedCount);

                // Lấy danh sách phiên gần đây
                var recentSessions = await service.GetRecentSessionsAsync(10);
                Assert.Single(recentSessions);
                Assert.Equal(session.SessionCode, recentSessions[0].SessionCode);
                Assert.Equal("USER_FILE_CSV", recentSessions[0].SourceType);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_GetSessionById_ReturnsSessionDetail_WithRawCsvAndSummary()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_session_detail_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var now = DateTimeOffset.UtcNow;

                var summary = new ReconciliationSummaryDto
                {
                    FromDate = now.AddDays(-3),
                    ToDate = now,
                    ReconciledAt = now,
                    TotalInternalCount = 2,
                    TotalGatewayCount = 2,
                    MatchedCount = 1,
                    AmountMismatchCount = 1,
                    Items = new List<ReconciliationItemDto>
                    {
                        new() { TransactionId = "TX_01", Group = ReconciliationGroups.Matched, InternalAmount = 100000, GatewayAmount = 100000 },
                        new() { TransactionId = "TX_02", Group = ReconciliationGroups.AmountMismatch, InternalAmount = 100000, GatewayAmount = 120000, Difference = 20000 }
                    }
                };

                string rawCsv = "TransactionId,Amount\nTX_01,100000\nTX_02,120000";

                var session = await service.SaveReconciliationSessionAsync(
                    summary.FromDate,
                    summary.ToDate,
                    "USER_TEXT_CSV",
                    rawCsv,
                    summary,
                    "admin_user");

                // Act
                var fetched = await service.GetSessionByIdAsync(session.Id);

                // Assert
                Assert.NotNull(fetched);
                Assert.Equal(session.Id, fetched.Id);
                Assert.Equal(session.SessionCode, fetched.SessionCode);
                Assert.Equal(rawCsv, fetched.RawCsv);
                Assert.Equal("USER_TEXT_CSV", fetched.SourceType);
                Assert.Contains("\"MatchedCount\":1", fetched.SummaryJson);
                Assert.Contains("\"AmountMismatchCount\":1", fetched.SummaryJson);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Audit_SummaryJson_Consistency_AndEntityCounters()
        {
            // Kiểm tra: Tính nhất quán giữa SummaryJson, các bộ đếm lưu trong session và kết quả khi mở lại phiên
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_summary_json_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var now = DateTimeOffset.UtcNow;

                var summary = new ReconciliationSummaryDto
                {
                    FromDate = now.AddDays(-2),
                    ToDate = now,
                    ReconciledAt = now,
                    TotalInternalCount = 10,
                    TotalInternalAmount = 5000000,
                    TotalGatewayCount = 8,
                    TotalGatewayAmount = 4500000,
                    MatchedCount = 6,
                    MatchedInternalAmount = 3500000,
                    MatchedGatewayAmount = 3500000,
                    SystemOnlyCount = 2,
                    SystemOnlyAmount = 1000000,
                    GatewayOnlyCount = 1,
                    GatewayOnlyAmount = 500000,
                    AmountMismatchCount = 1,
                    AmountMismatchInternalAmount = 500000,
                    AmountMismatchGatewayAmount = 500000,
                    AmountMismatchNetDifference = 0,
                    IneligibleCount = 1,
                    Items = new List<ReconciliationItemDto>
                    {
                        new() { TransactionId = "TX_01", Group = ReconciliationGroups.Matched, InternalAmount = 500000, GatewayAmount = 500000 }
                    }
                };

                var session = await service.SaveReconciliationSessionAsync(
                    summary.FromDate,
                    summary.ToDate,
                    "USER_FILE_CSV",
                    "dummy,csv",
                    summary,
                    "auditor_user");

                // 1. Kiểm tra các bộ đếm trên entity khớp chính xác
                Assert.Equal(summary.TotalInternalCount, session.TotalInternalCount);
                Assert.Equal(summary.TotalInternalAmount, session.TotalInternalAmount);
                Assert.Equal(summary.TotalGatewayCount, session.TotalGatewayCount);
                Assert.Equal(summary.TotalGatewayAmount, session.TotalGatewayAmount);
                Assert.Equal(summary.MatchedCount, session.MatchedCount);
                Assert.Equal(summary.SystemOnlyCount, session.SystemOnlyCount);
                Assert.Equal(summary.GatewayOnlyCount, session.GatewayOnlyCount);
                Assert.Equal(summary.AmountMismatchCount, session.AmountMismatchCount);
                Assert.Equal(summary.IneligibleCount, session.IneligibleCount);

                // 2. Kiểm tra chuỗi SummaryJson được lưu trữ đầy đủ các giá trị
                Assert.Contains("\"MatchedCount\":6", session.SummaryJson);
                Assert.Contains("\"SystemOnlyCount\":2", session.SummaryJson);
                Assert.Contains("\"GatewayOnlyCount\":1", session.SummaryJson);
                Assert.Contains("\"SessionId\":", session.SummaryJson);
                Assert.Contains("\"SessionCode\":", session.SummaryJson);

                // 3. Mở lại phiên qua GetSessionByIdAsync
                var fetched = await service.GetSessionByIdAsync(session.Id);
                Assert.NotNull(fetched);
                Assert.Equal(session.SessionCode, fetched.SessionCode);

                var deserialized = System.Text.Json.JsonSerializer.Deserialize<ReconciliationSummaryDto>(fetched.SummaryJson, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                Assert.NotNull(deserialized);
                Assert.Equal(6, deserialized.MatchedCount);
                Assert.Equal(2, deserialized.SystemOnlyCount);
                Assert.Equal(1, deserialized.GatewayOnlyCount);
                Assert.Equal(session.SessionCode, deserialized.SessionCode);
                Assert.Equal(session.Id, deserialized.SessionId);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 7. XUẤT BÁO CÁO CSV (CSV Report Export)

        [Fact]
        public void S51_ExportSummaryToCsv_GeneratesValidCsv_WithMetadataAndAllColumns()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            var now = DateTimeOffset.UtcNow;

            var summary = new ReconciliationSummaryDto
            {
                FromDate = now.AddDays(-5),
                ToDate = now,
                ReconciledAt = now,
                SessionCode = "REC_TEST_EXPORT_01",
                TotalInternalCount = 4,
                TotalInternalAmount = 700000,
                TotalGatewayCount = 3,
                TotalGatewayAmount = 650000,
                MatchedCount = 1,
                SystemOnlyCount = 1,
                GatewayOnlyCount = 1,
                AmountMismatchCount = 1,
                IneligibleCount = 0,
                Items = new List<ReconciliationItemDto>
                {
                    new()
                    {
                        TransactionId = "TX_01",
                        OrderCode = 1001,
                        Group = ReconciliationGroups.Matched,
                        InternalAmount = 200000,
                        GatewayAmount = 200000,
                        Difference = 0,
                        InternalDate = now,
                        GatewayDate = now,
                        Status = "PAID",
                        Notes = "Khớp hoàn toàn"
                    },
                    new()
                    {
                        TransactionId = "TX_02",
                        OrderCode = 1002,
                        Group = ReconciliationGroups.AmountMismatch,
                        InternalAmount = 300000,
                        GatewayAmount = 350000,
                        Difference = 50000,
                        InternalDate = now,
                        GatewayDate = now,
                        Status = "NEEDS_ATTENTION",
                        Notes = "Lệch tiền +50k"
                    }
                }
            };

            // Act
            string csvOutput = service.ExportSummaryToCsv(summary);

            // Assert
            Assert.Contains("# BÁO CÁO ĐỐI SOÁT GIAO DỊCH VỚI SAO KÊ CỔNG THANH TOÁN", csvOutput);
            Assert.Contains("REC_TEST_EXPORT_01", csvOutput);
            Assert.Contains("Mã Giao Dịch,Mã Đơn Hàng,Nhóm Đối Soát,Số Tiền Hệ Thống,Số Tiền Cổng,Chênh Lệch,Ngày Hệ Thống,Ngày Cổng,Trạng Thái,Ghi Chú", csvOutput);
            Assert.Contains("TX_01,1001,Khớp hoàn toàn,200000,200000,0,", csvOutput);
            Assert.Contains("TX_02,1002,Lệch số tiền,300000,350000,50000,", csvOutput);
        }

        [Fact]
        public async Task S51_Audit_StatementFile_RemainsUnmodified_InputNeverMutatedOrOverwritten()
        {
            // Yêu cầu 1 & 6: CSV đầu vào là sao kê do cổng xuất ra. Hệ thống chỉ đọc và phân tích, không sửa, không ghi đè, không thêm kết luận vào file gốc.
            var tempDir = Path.Combine(Path.GetTempPath(), $"s51_file_immutable_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "statement_original_gateway.csv");

            string originalCsvContent = "Mã giao dịch cổng,Mã đơn hàng,Số tiền,Thời gian giao dịch,Ghi chú\n" +
                                        "PAYOS_ORIG_01,101,200000,2026-10-15 09:00:00,Thanh toan ve test 1\n" +
                                        "PAYOS_ORIG_02,102,500000,2026-10-15 10:00:00,Thanh toan ve test 2\n";

            await File.WriteAllTextAsync(filePath, originalCsvContent, Encoding.UTF8);

            byte[] originalHash;
            using (var sha = SHA256.Create())
            {
                originalHash = sha.ComputeHash(await File.ReadAllBytesAsync(filePath));
            }

            var dbPath = Path.Combine(tempDir, "test.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "PAYOS_ORIG_01",
                    RawPayload = "{\"data\": {\"amount\": 200000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var controller = new ReconciliationController(service, NullLogger<ReconciliationController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };

                // Giả lập người dùng upload file gốc lên controller
                await using var fileStream = File.OpenRead(filePath);
                var formFile = new FormFile(fileStream, 0, fileStream.Length, "file", "statement_original_gateway.csv")
                {
                    Headers = new HeaderDictionary(),
                    ContentType = "text/csv"
                };

                var form = new ReconciliationController.ReconcileRequestForm
                {
                    FromDate = now.AddDays(-1),
                    ToDate = now.AddDays(1),
                    File = formFile
                };

                var actionResult = await controller.RunReconciliation(form);
                var okResult = Assert.IsType<OkObjectResult>(actionResult);
                var apiResponse = Assert.IsAssignableFrom<ApiResponse<ReconciliationSummaryDto>>(okResult.Value);
                Assert.True(apiResponse.Success);

                // Kiểm tra file trên đĩa sau khi hoàn thành đối soát:
                Assert.True(File.Exists(filePath), "File sao kê gốc vẫn phải tồn tại.");

                byte[] postHash;
                using (var sha = SHA256.Create())
                {
                    postHash = sha.ComputeHash(await File.ReadAllBytesAsync(filePath));
                }

                Assert.Equal(originalHash, postHash); // Hash byte-for-byte hoàn toàn giống hệt 100%

                string postContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
                Assert.Equal(originalCsvContent, postContent);
                Assert.DoesNotContain("Khớp", postContent);
                Assert.DoesNotContain("Lệch", postContent);
                Assert.DoesNotContain("Chỉ có ở", postContent);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir)) try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        [Fact]
        public async Task S51_Audit_ReconciliationResults_ComputedFromInternalAndStatement_IndependentFromStorage()
        {
            // Yêu cầu 2, 6 & 7: Kết quả tính toán từ so sánh 2 nguồn; không sửa đổi bảng đơn hàng, thanh toán, ghế, vé, hoàn tiền.
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_data_integrity_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var now = DateTimeOffset.UtcNow;
                var (user, evt, showtime, order) = await SeedOrderAsync(context, 200000);
                var (_, _, _, orderPending) = await SeedOrderAsync(context, 150000);

                var orderInitialStatus = order.Status;
                var orderInitialAmount = order.TotalAmount;

                var paymentTx = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    TransactionId = "TXN_INT_CLEAN_01",
                    OrderCode = 112233,
                    Amount = 200000,
                    Status = "PAID",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                context.PaymentTransactions.Add(paymentTx);

                // Một giao dịch chưa đủ điều kiện (Pending/Failed)
                var pendingTx = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    OrderId = orderPending.Id,
                    TransactionId = "TXN_INT_PENDING_02",
                    OrderCode = 112234,
                    Amount = 150000,
                    Status = "PENDING",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                context.PaymentTransactions.Add(pendingTx);

                await context.SaveChangesAsync();

                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);

                var gatewayItems = new List<GatewayStatementItemDto>
                {
                    new() { TransactionId = "TXN_INT_CLEAN_01", Amount = 200000, TransactionDate = now },
                    new() { TransactionId = "TXN_GATEWAY_EXTRA_03", Amount = 90000, TransactionDate = now }
                };

                // Act: Thực hiện đối soát
                var summary = await service.ReconcileAsync(now.AddDays(-1), now.AddDays(1), gatewayItems);

                // Assert 4 nhóm:
                Assert.Equal(1, summary.MatchedCount);
                Assert.Equal(0, summary.SystemOnlyCount);
                Assert.Equal(1, summary.GatewayOnlyCount);
                Assert.Equal(0, summary.AmountMismatchCount);
                Assert.Equal(1, summary.IneligibleCount); // Giao dịch Pending được cảnh báo riêng
                Assert.Contains(summary.Warnings, w => w.Contains("TXN_INT_PENDING_02") && w.Contains("Chưa đủ điều kiện"));

                // Kiểm tra tính toàn vẹn tuyệt đối của các bảng dữ liệu gốc:
                var postOrder = await context.Orders.FindAsync(order.Id);
                Assert.NotNull(postOrder);
                Assert.Equal(orderInitialStatus, postOrder.Status);
                Assert.Equal(orderInitialAmount, postOrder.TotalAmount);

                var postPaymentTx = await context.PaymentTransactions.FindAsync(paymentTx.Id);
                Assert.NotNull(postPaymentTx);
                Assert.Equal("PAID", postPaymentTx.Status);
                Assert.Equal(200000, postPaymentTx.Amount);

                var postPendingTx = await context.PaymentTransactions.FindAsync(pendingTx.Id);
                Assert.NotNull(postPendingTx);
                Assert.Equal("PENDING", postPendingTx.Status);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public void S51_Audit_ExportedCsv_IsIndependentReport_DistinctFromInputStatement()
        {
            // Yêu cầu 4 & 6: File xuất là báo cáo độc lập, có kết quả phân loại, số tiền hai bên, chênh lệch và cảnh báo
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            var now = DateTimeOffset.UtcNow;

            var summary = new ReconciliationSummaryDto
            {
                FromDate = now.AddDays(-7),
                ToDate = now,
                ReconciledAt = now,
                SessionCode = "REC_INDEP_REPORT_01",
                TotalInternalCount = 2,
                TotalInternalAmount = 350000,
                TotalGatewayCount = 2,
                TotalGatewayAmount = 400000,
                MatchedCount = 1,
                SystemOnlyCount = 0,
                GatewayOnlyCount = 0,
                AmountMismatchCount = 1,
                IneligibleCount = 1,
                Warnings = new List<string>
                {
                    "[Chưa đủ điều kiện] Giao dịch TX_PENDING_01: Giao dịch ở trạng thái 'PENDING'.",
                    "Sao kê cổng có mã giao dịch trùng lặp: 'TX_DUP_88'."
                },
                Items = new List<ReconciliationItemDto>
                {
                    new()
                    {
                        TransactionId = "TX_M_01",
                        OrderCode = 5001,
                        Group = ReconciliationGroups.Matched,
                        InternalAmount = 200000,
                        GatewayAmount = 200000,
                        Difference = 0,
                        InternalDate = now,
                        GatewayDate = now,
                        Status = "PAID",
                        Notes = "Khớp hoàn toàn"
                    },
                    new()
                    {
                        TransactionId = "TX_DIFF_02",
                        OrderCode = 5002,
                        Group = ReconciliationGroups.AmountMismatch,
                        InternalAmount = 150000,
                        GatewayAmount = 200000,
                        Difference = 50000,
                        InternalDate = now,
                        GatewayDate = now,
                        Status = "NEEDS_ATTENTION",
                        Notes = "Lệch số tiền +50,000 đ"
                    },
                    new()
                    {
                        TransactionId = "TX_PENDING_01",
                        OrderCode = 5003,
                        Group = ReconciliationGroups.Ineligible,
                        InternalAmount = 100000,
                        GatewayAmount = null,
                        Difference = null,
                        InternalDate = now,
                        Status = "PENDING",
                        Notes = "Giao dịch chưa đủ điều kiện đối soát."
                    }
                }
            };

            // Act
            string csvReport = service.ExportSummaryToCsv(summary);

            // Assert: Tiêu đề báo cáo và metadata
            Assert.Contains("# BÁO CÁO ĐỐI SOÁT GIAO DỊCH VỚI SAO KÊ CỔNG THANH TOÁN (STORY S-51)", csvReport);
            Assert.Contains("# Mã phiên đối soát: REC_INDEP_REPORT_01", csvReport);
            Assert.Contains("# Kỳ đối soát:", csvReport);
            Assert.Contains("# Khớp hoàn toàn: 1 | Chỉ có ở hệ thống: 0 | Chỉ có ở cổng: 0 | Lệch tiền: 1 | Chưa đủ điều kiện: 1", csvReport);

            // Assert: Chứa cảnh báo phát hiện
            Assert.Contains("# Cảnh báo phát hiện (2 cảnh báo):", csvReport);
            Assert.Contains("# - [Chưa đủ điều kiện] Giao dịch TX_PENDING_01: Giao dịch ở trạng thái 'PENDING'.", csvReport);
            Assert.Contains("# - Sao kê cổng có mã giao dịch trùng lặp: 'TX_DUP_88'.", csvReport);

            // Assert: Các cột dữ liệu báo cáo độc lập (phân loại, 2 bên số tiền, chênh lệch)
            Assert.Contains("Mã Giao Dịch,Mã Đơn Hàng,Nhóm Đối Soát,Số Tiền Hệ Thống,Số Tiền Cổng,Chênh Lệch,Ngày Hệ Thống,Ngày Cổng,Trạng Thái,Ghi Chú", csvReport);
            Assert.Contains("TX_M_01,5001,Khớp hoàn toàn,200000,200000,0,", csvReport);
            Assert.Contains("TX_DIFF_02,5002,Lệch số tiền,150000,200000,50000,", csvReport);
            Assert.Contains("TX_PENDING_01,5003,Chưa đủ điều kiện,100000,,,", csvReport);

            // Báo cáo kết quả hoàn toàn khác cấu trúc so với file sao kê gốc đầu vào
            string sampleInputStatementHeader = "Mã giao dịch cổng,Mã đơn hàng,Số tiền,Thời gian giao dịch,Nội dung thanh toán,Kênh thanh toán,Trạng thái";
            Assert.DoesNotContain(sampleInputStatementHeader, csvReport);
        }

        #endregion

        #region 8. PHÂN QUYỀN RBAC (Admin/Auditor Authorized, Others Forbidden)

        [Fact]
        public void S51_RBAC_ReconciliationController_PermitsAdminAndAuditor_RejectsOthers()
        {
            // Kiểm tra phân quyền RBAC trên ReconciliationController: chỉ Admin và Auditor được phép
            var controllerType = typeof(ReconciliationController);
            var requireRoleAttrs = controllerType.GetCustomAttributes(typeof(RequireRoleAttribute), true)
                .Cast<RequireRoleAttribute>()
                .ToList();

            Assert.NotEmpty(requireRoleAttrs);
            var roles = requireRoleAttrs[0].Roles;

            // Cả Admin và Auditor đều nằm trong danh sách vai trò được phép
            Assert.Contains(SystemRoles.Admin, roles);
            Assert.Contains(SystemRoles.Auditor, roles);

            // Các vai trò khác tuyệt đối KHÔNG được cấp quyền
            Assert.DoesNotContain(SystemRoles.Customer, roles);
            Assert.DoesNotContain(SystemRoles.Organizer, roles);
            Assert.DoesNotContain(SystemRoles.Staff, roles);
        }

        [Fact]
        public async Task S51_Audit_RBAC_RoleAuthorizationMiddleware_PermitsAuditorAndAdmin_RejectsOthers()
        {
            // Kiểm tra phân quyền thực tế qua middleware: Auditor & Admin được phép (200), role khác 403, chưa đăng nhập 401
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_rbac_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var config = new ConfigurationBuilder().Build();

                var roleAdmin = new Role { Id = Guid.NewGuid(), Name = SystemRoles.Admin };
                var roleAuditor = new Role { Id = Guid.NewGuid(), Name = SystemRoles.Auditor };
                var roleOrganizer = new Role { Id = Guid.NewGuid(), Name = SystemRoles.Organizer };
                var roleCustomer = new Role { Id = Guid.NewGuid(), Name = SystemRoles.Customer };
                var roleStaff = new Role { Id = Guid.NewGuid(), Name = SystemRoles.Staff };

                context.Roles.AddRange(roleAdmin, roleAuditor, roleOrganizer, roleCustomer, roleStaff);

                var userAdmin = new User { Id = Guid.NewGuid(), Username = "admin_user", Email = "admin@test.com", FullName = "Admin User", IsActive = true, PasswordHash = "hash" };
                var userAuditor = new User { Id = Guid.NewGuid(), Username = "auditor_user", Email = "auditor@test.com", FullName = "Auditor User", IsActive = true, PasswordHash = "hash" };
                var userOrganizer = new User { Id = Guid.NewGuid(), Username = "org_user", Email = "org@test.com", FullName = "Org User", IsActive = true, PasswordHash = "hash" };
                var userCustomer = new User { Id = Guid.NewGuid(), Username = "cust_user", Email = "cust@test.com", FullName = "Customer User", IsActive = true, PasswordHash = "hash" };
                var userStaff = new User { Id = Guid.NewGuid(), Username = "staff_user", Email = "staff@test.com", FullName = "Staff User", IsActive = true, PasswordHash = "hash" };

                context.Users.AddRange(userAdmin, userAuditor, userOrganizer, userCustomer, userStaff);

                context.UserRoles.Add(new UserRole { UserId = userAdmin.Id, RoleId = roleAdmin.Id });
                context.UserRoles.Add(new UserRole { UserId = userAuditor.Id, RoleId = roleAuditor.Id });
                context.UserRoles.Add(new UserRole { UserId = userOrganizer.Id, RoleId = roleOrganizer.Id });
                context.UserRoles.Add(new UserRole { UserId = userCustomer.Id, RoleId = roleCustomer.Id });
                context.UserRoles.Add(new UserRole { UserId = userStaff.Id, RoleId = roleStaff.Id });

                await context.SaveChangesAsync();

                var endpointMetadata = new EndpointMetadataCollection(
                    new RequireRoleAttribute(SystemRoles.Admin, SystemRoles.Auditor));

                async Task<int> InvokeForUser(User? user, string? roleName)
                {
                    bool nextCalled = false;
                    var httpContext = new DefaultHttpContext();
                    httpContext.Response.Body = new MemoryStream();
                    httpContext.Request.Path = "/api/v1/reconciliation/run";
                    httpContext.Request.Method = "POST";
                    httpContext.SetEndpoint(new Endpoint(_ => { nextCalled = true; return Task.CompletedTask; }, endpointMetadata, "reconcile_run"));

                    if (user != null && roleName != null)
                    {
                        var identity = new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                            new Claim(ClaimTypes.Role, roleName)
                        }, "Bearer");
                        httpContext.User = new ClaimsPrincipal(identity);
                    }

                    var middleware = new RoleAuthorizationMiddleware(ctx =>
                    {
                        nextCalled = true;
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                        return Task.CompletedTask;
                    });

                    await middleware.InvokeAsync(httpContext, context, config);
                    return nextCalled ? StatusCodes.Status200OK : httpContext.Response.StatusCode;
                }

                // 1. Auditor -> 200 OK
                Assert.Equal(200, await InvokeForUser(userAuditor, SystemRoles.Auditor));

                // 2. Admin -> 200 OK
                Assert.Equal(200, await InvokeForUser(userAdmin, SystemRoles.Admin));

                // 3. Organizer -> 403 Forbidden
                Assert.Equal(403, await InvokeForUser(userOrganizer, SystemRoles.Organizer));

                // 4. Staff -> 403 Forbidden
                Assert.Equal(403, await InvokeForUser(userStaff, SystemRoles.Staff));

                // 5. Customer -> 403 Forbidden
                Assert.Equal(403, await InvokeForUser(userCustomer, SystemRoles.Customer));

                // 6. Anonymous (Unauthenticated) -> 401 Unauthorized
                Assert.Equal(401, await InvokeForUser(null, null));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion

        #region 9. CONTROLLER ENDPOINTS TÍCH HỢP (Run, Export, Sessions)

        [Fact]
        public async Task S51_Controller_Run_WithValidCsvText_ReturnsOk()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_ctrl_run_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var controller = new ReconciliationController(service, NullLogger<ReconciliationController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                };

                var now = DateTimeOffset.UtcNow;
                context.PaymentEvents.Add(new PaymentEvent
                {
                    Id = Guid.NewGuid(),
                    TransactionId = "TX_RUN_01",
                    RawPayload = "{\"data\": {\"amount\": 200000}}",
                    CreatedAt = now
                });
                await context.SaveChangesAsync();

                var form = new ReconciliationController.ReconcileRequestForm
                {
                    FromDate = now.AddDays(-1),
                    ToDate = now.AddDays(1),
                    CsvContent = "TransactionId,Amount\nTX_RUN_01,200000"
                };

                // Act
                var result = await controller.RunReconciliation(form);

                // Assert
                var okResult = Assert.IsType<OkObjectResult>(result);
                var apiResponse = Assert.IsAssignableFrom<ApiResponse<ReconciliationSummaryDto>>(okResult.Value);
                Assert.True(apiResponse.Success);
                Assert.NotNull(apiResponse.Data);
                Assert.Equal(1, apiResponse.Data.MatchedCount);
                Assert.NotNull(apiResponse.Data.SessionCode);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        [Fact]
        public async Task S51_Controller_Run_FromDateGreaterThanToDate_ReturnsBadRequest()
        {
            // Arrange
            var controller = new ReconciliationController(null!, NullLogger<ReconciliationController>.Instance);
            var now = DateTimeOffset.UtcNow;

            var form = new ReconciliationController.ReconcileRequestForm
            {
                FromDate = now.AddDays(1),
                ToDate = now.AddDays(-1),
                CsvContent = "TransactionId,Amount\nTX_01,100000"
            };

            // Act
            var result = await controller.RunReconciliation(form);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequest.Value);
        }

        [Fact]
        public async Task S51_Controller_Run_EmptyCsv_ReturnsBadRequest()
        {
            // Arrange
            var controller = new ReconciliationController(null!, NullLogger<ReconciliationController>.Instance);
            var now = DateTimeOffset.UtcNow;

            var form = new ReconciliationController.ReconcileRequestForm
            {
                FromDate = now.AddDays(-1),
                ToDate = now.AddDays(1),
                CsvContent = null,
                File = null
            };

            // Act
            var result = await controller.RunReconciliation(form);

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequest.Value);
        }

        [Fact]
        public void S51_Controller_ExportCsv_ReturnsFileContentResult()
        {
            // Arrange
            var service = new ReconciliationService(null!, NullLogger<ReconciliationService>.Instance);
            var controller = new ReconciliationController(service, NullLogger<ReconciliationController>.Instance);
            var now = DateTimeOffset.UtcNow;

            var summary = new ReconciliationSummaryDto
            {
                FromDate = now.AddDays(-1),
                ToDate = now,
                ReconciledAt = now,
                Items = new List<ReconciliationItemDto>()
            };

            // Act
            var result = controller.ExportCsvReport(summary);

            // Assert
            var fileResult = Assert.IsType<FileContentResult>(result);
            Assert.Equal("text/csv; charset=utf-8", fileResult.ContentType);
            Assert.Contains("Reconciliation_Report_", fileResult.FileDownloadName);
            Assert.True(fileResult.FileContents.Length > 0);
        }

        [Fact]
        public async Task S51_Controller_SessionsEndpoints_ReturnsListAndDetail()
        {
            // Arrange
            var dbPath = Path.Combine(Path.GetTempPath(), $"s51_ctrl_sessions_{Guid.NewGuid():N}.db");
            try
            {
                using var context = CreateSqliteDbContext(dbPath);
                var service = new ReconciliationService(context, NullLogger<ReconciliationService>.Instance);
                var controller = new ReconciliationController(service, NullLogger<ReconciliationController>.Instance);
                var now = DateTimeOffset.UtcNow;

                var summary = new ReconciliationSummaryDto
                {
                    FromDate = now.AddDays(-1),
                    ToDate = now,
                    ReconciledAt = now,
                    MatchedCount = 1,
                    Items = new List<ReconciliationItemDto>()
                };

                var session = await service.SaveReconciliationSessionAsync(
                    summary.FromDate,
                    summary.ToDate,
                    "USER_FILE_CSV",
                    "TransactionId,Amount\nTX1,100000",
                    summary,
                    "auditor_user");

                // Act 1: Get recent sessions
                var listResult = await controller.GetRecentSessions(10);
                var okList = Assert.IsType<OkObjectResult>(listResult);
                var apiList = Assert.IsAssignableFrom<ApiResponse<List<ReconciliationSessionDto>>>(okList.Value);
                Assert.True(apiList.Success);
                Assert.Single(apiList.Data);

                // Act 2: Get session by ID
                var detailResult = await controller.GetSessionById(session.Id);
                var okDetail = Assert.IsType<OkObjectResult>(detailResult);
                var apiDetail = Assert.IsAssignableFrom<ApiResponse<ReconciliationSessionDetailDto>>(okDetail.Value);
                Assert.True(apiDetail.Success);
                Assert.Equal(session.SessionCode, apiDetail.Data.SessionCode);
                Assert.Equal("USER_FILE_CSV", apiDetail.Data.SourceType);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (File.Exists(dbPath)) try { File.Delete(dbPath); } catch { }
            }
        }

        #endregion
    }
}
