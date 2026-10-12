using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs.Payment;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Options;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Implementations.Payment;
using EventTicketBooking.Api.Services.Interfaces;
using EventTicketBooking.Tests.Mocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử toàn diện cho Jira Story S-48:
    /// - Retry lệnh hoàn tiền thất bại tối đa 3 lần với exponential backoff
    /// - Chống hoàn tiền trùng lặp (Idempotency & Concurrency)
    /// - Đánh dấu xử lý thủ công và thông báo cho kế toán khi hết số lần retry
    /// </summary>
    public sealed class RefundRetryAndAccountingTests
    {
        private static AppDbContext CreateInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new AppDbContext(options);
        }

        private static async Task<Order> CreateTestOrderAsync(AppDbContext context, int totalAmount = 500_000)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "customer_test",
                Email = "customer@example.com",
                PasswordHash = "hash"
            };
            var ev = new Event
            {
                Id = Guid.NewGuid(),
                Title = "Live Concert Hà Nội",
                Location = "Trung tâm Hội nghị Quốc gia",
                TotalSeats = 100
            };
            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = DateTime.UtcNow.AddDays(2),
                EndTime = DateTime.UtcNow.AddDays(2).AddHours(3),
                AvailableSeats = 50
            };
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                User = user,
                ShowtimeId = showtime.Id,
                Showtime = showtime,
                TotalAmount = totalAmount,
                Status = OrderStatus.Cancelled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var paymentTx = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 1000000123L,
                Amount = totalAmount,
                Status = "PAID",
                TransactionId = "GATEWAY_TX_888",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            order.PaymentTransaction = paymentTx;

            context.Users.Add(user);
            context.Events.Add(ev);
            context.Showtimes.Add(showtime);
            context.Orders.Add(order);
            context.PaymentTransactions.Add(paymentTx);
            await context.SaveChangesAsync();

            return order;
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 1: Refund thành công ngay lần đầu: không retry
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario1_RefundSuccessOnFirstAttempt_DoesNotRetry_AndDoesNotNotifyAccounting()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = true,
                SimulatedRefundTransactionId = "REF_SUCCESS_001"
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object,
                accountingOptions,
                NullLogger<AccountingNotificationService>.Instance,
                context);

            var refundOptions = Options.Create(new RefundOptions
            {
                MaxRetries = 3,
                BackoffMinutes = new[] { 1, 2, 4 }
            });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);

            var refundService = new RefundService(
                context,
                fakeGateway,
                accountingService,
                auditService,
                refundOptions,
                NullLogger<RefundService>.Instance);

            // 1. Tạo yêu cầu hoàn tiền
            var refund = await refundService.CreateRefundRequestAsync(order.Id, order.TotalAmount, "ShowtimeCancelled");
            Assert.NotNull(refund);
            Assert.Equal(RefundStatus.Pending, refund.Status);
            Assert.Equal(0, refund.AttemptCount);
            Assert.Equal(0, refund.RetryCount);

            // 2. Thực thi xử lý hoàn tiền
            var result = await refundService.ProcessRefundAsync(refund.Id);

            // 3. Kiểm tra kết quả
            Assert.True(result.Success);
            Assert.Equal("REF_SUCCESS_001", result.RefundTransactionId);

            var updatedRefund = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(updatedRefund);
            Assert.Equal(RefundStatus.Succeeded, updatedRefund.Status);
            Assert.Equal(1, updatedRefund.AttemptCount);
            Assert.Equal(0, updatedRefund.RetryCount);
            Assert.Null(updatedRefund.NextRetryAt);
            Assert.False(updatedRefund.AccountingNotified);

            // Không gọi gateway lần nào nữa
            Assert.Single(fakeGateway.RecordedRefundRequests);
            mockEmailService.Verify(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 2: Refund lỗi tạm thời rồi thành công: retry và hoàn tất đúng
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario2_RefundTransientError_ThenSuccessOnRetry_CompletesSuccessfully()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            int callCount = 0;
            var fakeGateway = new FakePaymentGateway
            {
                CustomRefundHandler = req =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // Lần 1: Lỗi timeout mạng (retryable)
                        return Task.FromResult(RefundExecutionResult.CreateFailure(
                            "NETWORK_TIMEOUT", "Mất kết nối với cổng thanh toán.", isRetryable: true));
                    }
                    // Lần 2: Thành công
                    return Task.FromResult(RefundExecutionResult.CreateSuccess("REF_RETRY_OK_002"));
                }
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions
            {
                MaxRetries = 3,
                BackoffMinutes = new[] { 1, 2, 4 }
            });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Lần thử 1 (Initial attempt)
            var result1 = await refundService.ProcessRefundAsync(refund.Id);
            Assert.False(result1.Success);

            var stateAfter1 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(stateAfter1);
            Assert.Equal(RefundStatus.RetryScheduled, stateAfter1.Status);
            Assert.Equal(1, stateAfter1.AttemptCount);
            Assert.Equal(1, stateAfter1.RetryCount);
            Assert.NotNull(stateAfter1.NextRetryAt);
            Assert.True(stateAfter1.IsRetryable);

            // Lần thử 2 (Retry 1 thành công)
            var result2 = await refundService.ProcessRefundAsync(refund.Id);
            Assert.True(result2.Success);
            Assert.Equal("REF_RETRY_OK_002", result2.RefundTransactionId);

            var stateAfter2 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(stateAfter2);
            Assert.Equal(RefundStatus.Succeeded, stateAfter2.Status);
            Assert.Equal(2, stateAfter2.AttemptCount);
            Assert.Equal(1, stateAfter2.RetryCount);
            Assert.Null(stateAfter2.NextRetryAt);

            Assert.Equal(2, fakeGateway.RecordedRefundRequests.Count);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 3: Refund thất bại liên tục: dừng sau tối đa 3 lần retry bổ sung
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario3_RefundContinuousFailures_StopsAfterMax3Retries()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = false,
                SimulatedRefundErrorCode = "SERVICE_UNAVAILABLE",
                SimulatedRefundErrorMessage = "Hệ thống ngân hàng đang bảo trì.",
                SimulatedRefundIsRetryable = true
            };

            var mockEmailService = new Mock<IEmailService>();
            mockEmailService.Setup(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions
            {
                MaxRetries = 3,
                BackoffMinutes = new[] { 1, 2, 4 }
            });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Attempt 1 (Initial): AttemptCount=1, RetryCount=1 -> RetryScheduled
            await refundService.ProcessRefundAsync(refund.Id);
            var r1 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.RetryScheduled, r1!.Status);
            Assert.Equal(1, r1.AttemptCount);
            Assert.Equal(1, r1.RetryCount);

            // Attempt 2 (Retry 1): AttemptCount=2, RetryCount=2 -> RetryScheduled
            await refundService.ProcessRefundAsync(refund.Id);
            var r2 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.RetryScheduled, r2!.Status);
            Assert.Equal(2, r2.AttemptCount);
            Assert.Equal(2, r2.RetryCount);

            // Attempt 3 (Retry 2): AttemptCount=3, RetryCount=3 -> RetryScheduled
            await refundService.ProcessRefundAsync(refund.Id);
            var r3 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.RetryScheduled, r3!.Status);
            Assert.Equal(3, r3.AttemptCount);
            Assert.Equal(3, r3.RetryCount);

            // Attempt 4 (Retry 3 - Lần retry cuối cùng): Hết số lần retry -> Chuyển ManualReviewRequired
            await refundService.ProcessRefundAsync(refund.Id);
            var r4 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.ManualReviewRequired, r4!.Status);
            Assert.Equal(4, r4.AttemptCount);
            Assert.Equal(3, r4.RetryCount);
            Assert.Null(r4.NextRetryAt);

            // Tổng số lần gọi gateway đúng bằng: 1 lần ban đầu + 3 lần retry = 4
            Assert.Equal(4, fakeGateway.RecordedRefundRequests.Count);

            // Gọi thêm lần nữa không được tăng số lần gọi gateway vì đã ở terminal state
            await refundService.ProcessRefundAsync(refund.Id);
            Assert.Equal(4, fakeGateway.RecordedRefundRequests.Count);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 4: Backoff tăng dần đúng cấu hình (1 phút, 2 phút, 4 phút)
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario4_BackoffDelays_IncreaseExponentiallyAccordingToConfig()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = false,
                SimulatedRefundErrorCode = "GATEWAY_TIMEOUT",
                SimulatedRefundErrorMessage = "Timeout kết nối.",
                SimulatedRefundIsRetryable = true
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions
            {
                MaxRetries = 3,
                BackoffMinutes = new[] { 1, 2, 4 }
            });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Retry 1: delay 1 phút
            var beforeAttempt1 = DateTimeOffset.UtcNow;
            await refundService.ProcessRefundAsync(refund.Id);
            var r1 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(r1!.NextRetryAt);
            var delay1 = r1.NextRetryAt.Value - beforeAttempt1;
            Assert.True(delay1 >= TimeSpan.FromSeconds(55) && delay1 <= TimeSpan.FromSeconds(65),
                $"Delay 1 mong đợi ~1 phút, thực tế {delay1.TotalSeconds}s");

            // Retry 2: delay 2 phút
            var beforeAttempt2 = DateTimeOffset.UtcNow;
            await refundService.ProcessRefundAsync(refund.Id);
            var r2 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(r2!.NextRetryAt);
            var delay2 = r2.NextRetryAt.Value - beforeAttempt2;
            Assert.True(delay2 >= TimeSpan.FromSeconds(115) && delay2 <= TimeSpan.FromSeconds(125),
                $"Delay 2 mong đợi ~2 phút, thực tế {delay2.TotalSeconds}s");

            // Retry 3: delay 4 phút
            var beforeAttempt3 = DateTimeOffset.UtcNow;
            await refundService.ProcessRefundAsync(refund.Id);
            var r3 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(r3!.NextRetryAt);
            var delay3 = r3.NextRetryAt.Value - beforeAttempt3;
            Assert.True(delay3 >= TimeSpan.FromSeconds(235) && delay3 <= TimeSpan.FromSeconds(245),
                $"Delay 3 mong đợi ~4 phút, thực tế {delay3.TotalSeconds}s");
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 5: Lỗi không retryable: không tiếp tục retry
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario5_NonRetryableError_DoesNotRetry_AndTransitionsImmediately()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = false,
                SimulatedRefundErrorCode = "ACCOUNT_FROZEN",
                SimulatedRefundErrorMessage = "Tài khoản nhận tiền của khách hàng đã bị đóng băng.",
                SimulatedRefundIsRetryable = false
            };

            var mockEmailService = new Mock<IEmailService>();
            mockEmailService.Setup(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions
            {
                MaxRetries = 3,
                BackoffMinutes = new[] { 1, 2, 4 }
            });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            var result = await refundService.ProcessRefundAsync(refund.Id);
            Assert.False(result.Success);

            var finalState = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(finalState);
            Assert.Equal(RefundStatus.ManualReviewRequired, finalState.Status);
            Assert.Equal(1, finalState.AttemptCount);
            Assert.Equal(0, finalState.RetryCount); // Không có lần retry nào!
            Assert.Null(finalState.NextRetryAt);
            Assert.False(finalState.IsRetryable);

            // Chỉ gọi gateway đúng 1 lần duy nhất
            Assert.Single(fakeGateway.RecordedRefundRequests);

            // Kế toán được thông báo ngay lập tức
            mockEmailService.Verify(e => e.SendAccountingAlertEmailAsync("accounting@test.vn", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 6: Timeout với kết quả chưa rõ: kiểm tra trước, không tạo refund trùng
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario6_TimeoutWithUnclearResult_VerifiesStatusBeforeResending_NoDoubleRefund()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            bool queryStatusCalled = false;
            var fakeGateway = new FakePaymentGateway
            {
                // Lần 1: Timeout
                CustomRefundHandler = req => Task.FromResult(RefundExecutionResult.CreateFailure(
                    "NETWORK_TIMEOUT", "Mất kết nối với cổng thanh toán.", isRetryable: true)),

                // Khi đối soát trạng thái trước lần 2: Cổng xác nhận giao dịch ĐÃ thành công
                CustomQueryRefundHandler = (refundTxId, orderCode) =>
                {
                    queryStatusCalled = true;
                    return Task.FromResult<RefundStatusDto?>(new RefundStatusDto
                    {
                        Success = true,
                        Status = "SUCCESS",
                        RefundTransactionId = "REF_CONFIRMED_ON_GATEWAY_999"
                    });
                }
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3, BackoffMinutes = new[] { 1, 2, 4 } });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Lần 1: Bị Timeout
            var result1 = await refundService.ProcessRefundAsync(refund.Id);
            Assert.False(result1.Success);

            // Lần 2: Trước khi gửi lệnh hoàn tiền mới, kiểm tra trạng thái trên cổng
            var result2 = await refundService.ProcessRefundAsync(refund.Id);
            Assert.True(result2.Success);
            Assert.True(queryStatusCalled, "Hệ thống phải truy vấn trạng thái từ cổng trước khi gọi lại refund.");
            Assert.Equal("REF_CONFIRMED_ON_GATEWAY_999", result2.RefundTransactionId);

            var finalRefund = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.Succeeded, finalRefund!.Status);

            // KHÔNG tạo lệnh hoàn tiền lần 2 qua ExecuteRefundAsync
            Assert.Single(fakeGateway.RecordedRefundRequests);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 7: Nhiều worker hoặc xử lý lặp cùng refund: không hoàn tiền hai lần
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario7_ConcurrentWorkers_DoNotExecuteRefundTwice()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = true,
                SimulatedRefundTransactionId = "REF_CONCURRENT_SAFE"
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3, LockLeaseSeconds = 60 });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Giả lập Worker 1 đang xử lý và giữ lease lock
            refund.Status = RefundStatus.Processing;
            refund.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(2);
            await context.SaveChangesAsync();

            // Worker 2 cùng vào xử lý
            var resultWorker2 = await refundService.ProcessRefundAsync(refund.Id);

            // Worker 2 bị từ chối vì lock đang hoạt động
            Assert.False(resultWorker2.Success);
            Assert.Equal("CONCURRENT_LOCK", resultWorker2.ErrorCode);

            // Gateway hoàn toàn chưa bị gọi bởi Worker 2
            Assert.Empty(fakeGateway.RecordedRefundRequests);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 8: Hết retry: đánh dấu cần xử lý thủ công và tạo yêu cầu thông báo kế toán
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario8_ExhaustedRetries_MarksManualReview_AndNotifiesAccountingWithChecklist()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context, 750_000);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = false,
                SimulatedRefundErrorCode = "BANK_SYSTEM_ERROR",
                SimulatedRefundErrorMessage = "Ngân hàng báo lỗi thẻ: Card 4111-2222-3333-4444 secret=sec_abc123.",
                SimulatedRefundIsRetryable = true
            };

            string? sentSubject = null;
            string? sentBody = null;
            var mockEmailService = new Mock<IEmailService>();
            mockEmailService.Setup(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string>((to, sub, body) =>
                {
                    sentSubject = sub;
                    sentBody = body;
                })
                .ReturnsAsync(true);

            var accountingOptions = Options.Create(new AccountingOptions
            {
                Email = "ketoan@eventpulse.vn",
                DepartmentName = "Ban Kế toán EventPulse"
            });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3 });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Chạy qua 4 lần gọi (1 lần đầu + 3 lần retry)
            for (int i = 0; i < 4; i++)
            {
                await refundService.ProcessRefundAsync(refund.Id);
            }

            var finalRefund = await context.RefundRequests.FindAsync(refund.Id);
            Assert.NotNull(finalRefund);
            Assert.Equal(RefundStatus.ManualReviewRequired, finalRefund.Status);
            Assert.True(finalRefund.AccountingNotified);
            Assert.NotNull(finalRefund.AccountingNotifiedAt);

            // Xác nhận nội dung thông báo cho kế toán
            Assert.NotNull(sentSubject);
            Assert.Contains("KHẨN CẤP", sentSubject);
            Assert.Contains(order.PaymentTransaction!.OrderCode.ToString(), sentSubject);

            Assert.NotNull(sentBody);
            Assert.Contains(finalRefund.Id.ToString(), sentBody);
            Assert.Contains(order.Id.ToString(), sentBody);
            Assert.Contains("750,000", sentBody);
            Assert.Contains("HƯỚNG DẪN XỬ LÝ THỦ CÔNG DÀNH CHO KẾ TOÁN", sentBody);

            // Kiểm tra bảo mật: Không chứa số thẻ thực hoặc secret trong email
            Assert.DoesNotContain("4111-2222-3333-4444", sentBody);
            Assert.Contains("****-****-****-****", sentBody);
            Assert.DoesNotContain("sec_abc123", sentBody);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 9: Gửi thông báo thất bại: có thể thử gửi lại mà không tạo thông báo trùng
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario9_AccountingNotificationFailure_CanRetryWithoutDuplicateNotification()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var refund = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 123456789L,
                TransactionId = "TXN_FAIL_TEST",
                Amount = 300_000,
                Status = RefundStatus.ManualReviewRequired,
                AccountingNotified = false,
                FailedAt = DateTimeOffset.UtcNow
            };
            context.RefundRequests.Add(refund);
            await context.SaveChangesAsync();

            int emailAttempts = 0;
            var mockEmailService = new Mock<IEmailService>();
            mockEmailService.Setup(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(() =>
                {
                    emailAttempts++;
                    if (emailAttempts == 1)
                    {
                        // Lần 1: Lỗi gửi email
                        throw new InvalidOperationException("SMTP connection timeout");
                    }
                    // Lần 2: Gửi thành công
                    return Task.FromResult(true);
                });

            var accountingOptions = Options.Create(new AccountingOptions { Email = "ketoan@eventpulse.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            // Lần 1: Gửi thất bại -> Lưu trạng thái lỗi để gửi lại
            bool firstAttempt = await accountingService.NotifyFailedRefundAsync(refund);
            Assert.False(firstAttempt);

            var state1 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.False(state1!.AccountingNotified);
            Assert.NotNull(state1.AccountingNotificationError);
            Assert.Contains("SMTP connection timeout", state1.AccountingNotificationError);

            // Lần 2: Thử gửi lại -> Thành công
            bool secondAttempt = await accountingService.NotifyFailedRefundAsync(refund);
            Assert.True(secondAttempt);

            var state2 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.True(state2!.AccountingNotified);
            Assert.Null(state2.AccountingNotificationError);
            Assert.NotNull(state2.AccountingNotifiedAt);

            // Lần 3: Gọi lại lần nữa -> Nhận diện đã gửi thành công trước đó (Idempotent) -> Không gửi lại
            bool thirdAttempt = await accountingService.NotifyFailedRefundAsync(refund);
            Assert.True(thirdAttempt);
            Assert.Equal(2, emailAttempts); // Không bị tăng lên 3
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 10: Refund đã thành công không bị retry lại sau restart hoặc chạy lại job
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario10_SucceededRefund_IsNotRetriedAfterRestartOrRescan()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var succeededRefund = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 88888888L,
                TransactionId = "TXN_OLD",
                RefundTransactionId = "REF_OLD_SUCCESS",
                Amount = 500_000,
                Status = RefundStatus.Succeeded,
                AttemptCount = 1,
                RetryCount = 0,
                NextRetryAt = null
            };
            context.RefundRequests.Add(succeededRefund);
            await context.SaveChangesAsync();

            var fakeGateway = new FakePaymentGateway();
            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3 });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            // 1. Quét hàng loạt các refund đến hạn (mô phỏng worker chạy lại)
            int processedCount = await refundService.ProcessPendingAndRetryableRefundsAsync();
            Assert.Equal(0, processedCount); // Không có bản ghi nào bị quét nhầm

            // 2. Cố tình gọi trực tiếp ProcessRefundAsync
            var directResult = await refundService.ProcessRefundAsync(succeededRefund.Id);
            Assert.True(directResult.Success);
            Assert.Equal("REF_OLD_SUCCESS", directResult.RefundTransactionId);

            // Cổng thanh toán hoàn toàn không bị gọi lại
            Assert.Empty(fakeGateway.RecordedRefundRequests);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 11: Cổng trả về trạng thái PENDING khi đối soát: lên lịch chờ, không gọi hoàn tiền trùng
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario11_GatewayStatusPending_ReschedulesBackoff_DoesNotExecuteDuplicateRefund()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                CustomRefundHandler = req => Task.FromResult(RefundExecutionResult.CreateFailure(
                    "NETWORK_TIMEOUT", "Mất kết nối với cổng.", isRetryable: true)),

                CustomQueryRefundHandler = (refundTxId, orderCode) => Task.FromResult<RefundStatusDto?>(new RefundStatusDto
                {
                    Success = false,
                    Status = "PENDING",
                    RefundTransactionId = "REF_PENDING_ON_GATEWAY"
                })
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3, BackoffMinutes = new[] { 1, 2, 4 } });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            // Lần 1: Gọi bị timeout
            await refundService.ProcessRefundAsync(refund.Id);
            Assert.Single(fakeGateway.RecordedRefundRequests);

            // Lần 2: Query cổng thấy PENDING -> Không gọi ExecuteRefundAsync lần 2, tiếp tục lên lịch backoff
            var result2 = await refundService.ProcessRefundAsync(refund.Id);
            Assert.False(result2.Success);
            Assert.Equal("GATEWAY_PENDING", result2.ErrorCode);

            var r2 = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.RetryScheduled, r2!.Status);
            Assert.NotNull(r2.NextRetryAt);

            // Tuyệt đối không gọi ExecuteRefundAsync lần thứ 2 khi giao dịch trước vẫn PENDING
            Assert.Single(fakeGateway.RecordedRefundRequests);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 12: Worker bị crash giữa chừng: tự phục hồi sau khi hết hạn lease lock
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario12_CrashedWorker_RecoversAfterLeaseExpiry()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var fakeGateway = new FakePaymentGateway
            {
                RefundShouldSucceed = true,
                SimulatedRefundTransactionId = "REF_RECOVERED_AFTER_CRASH"
            };

            var mockEmailService = new Mock<IEmailService>();
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3 });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, fakeGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            // Giả lập worker cũ bị crash trong khi đang ở trạng thái Processing và lock đã hết hạn
            var crashedRefund = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 9999999L,
                Amount = 500_000,
                Status = RefundStatus.Processing,
                LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-5), // Đã hết hạn lease 5 phút trước
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            context.RefundRequests.Add(crashedRefund);
            await context.SaveChangesAsync();

            // Background worker chạy lại và quét
            int processedCount = await refundService.ProcessPendingAndRetryableRefundsAsync();
            Assert.Equal(1, processedCount);

            var recovered = await context.RefundRequests.FindAsync(crashedRefund.Id);
            Assert.Equal(RefundStatus.Succeeded, recovered!.Status);
            Assert.Equal("REF_RECOVERED_AFTER_CRASH", recovered.RefundTransactionId);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 13: Chưa cấu hình Email kế toán: ghi nhận PENDING_CONFIGURATION, không giả vờ đã gửi
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario13_UnconfiguredAccountingEmail_LogsPendingConfiguration_DoesNotPretendSent()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var refund = new RefundRequest
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                OrderCode = 55555L,
                Amount = 200_000,
                Status = RefundStatus.ManualReviewRequired,
                AccountingNotified = false
            };
            context.RefundRequests.Add(refund);
            await context.SaveChangesAsync();

            var mockEmailService = new Mock<IEmailService>();
            // Cấu hình email rỗng
            var accountingOptions = Options.Create(new AccountingOptions { Email = "" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            bool notifyResult = await accountingService.NotifyFailedRefundAsync(refund);
            Assert.False(notifyResult);

            var updatedRefund = await context.RefundRequests.FindAsync(refund.Id);
            Assert.False(updatedRefund!.AccountingNotified);
            Assert.NotNull(updatedRefund.AccountingNotificationError);
            Assert.Contains("đang để trống", updatedRefund.AccountingNotificationError);

            // Không gọi phương thức gửi email
            mockEmailService.Verify(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Kịch bản 14: Adapter PayOS: trả về PAYOS_NOT_SUPPORTED không retry, chuyển sang đối soát kế toán
        // ──────────────────────────────────────────────────────────────────────────
        [Fact]
        public async Task Scenario14_PayOSGateway_ReturnsPayOSNotSupported_ClassifiedAsNonRetryable()
        {
            await using var context = CreateInMemoryDbContext();
            var order = await CreateTestOrderAsync(context);

            var payosOptions = Options.Create(new PayOSOptions
            {
                ClientId = "test_client",
                ApiKey = "test_key",
                ChecksumKey = "test_checksum"
            });
            var payosGateway = new PayOSPaymentGateway(
                new HttpClient(),
                payosOptions,
                NullLogger<PayOSPaymentGateway>.Instance);

            var mockEmailService = new Mock<IEmailService>();
            mockEmailService.Setup(e => e.SendAccountingAlertEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);
            var accountingOptions = Options.Create(new AccountingOptions { Email = "accounting@test.vn" });
            var accountingService = new AccountingNotificationService(
                mockEmailService.Object, accountingOptions, NullLogger<AccountingNotificationService>.Instance, context);

            var refundOptions = Options.Create(new RefundOptions { MaxRetries = 3 });
            var auditService = new OrderAuditService(context, NullLogger<OrderAuditService>.Instance);
            var refundService = new RefundService(
                context, payosGateway, accountingService, auditService, refundOptions, NullLogger<RefundService>.Instance);

            var refund = await refundService.CreateRefundRequestAsync(order.Id);

            var result = await refundService.ProcessRefundAsync(refund.Id);
            Assert.False(result.Success);
            Assert.Equal("PAYOS_NOT_SUPPORTED", result.ErrorCode);
            Assert.False(result.IsRetryable);

            var finalRefund = await context.RefundRequests.FindAsync(refund.Id);
            Assert.Equal(RefundStatus.ManualReviewRequired, finalRefund!.Status);
            Assert.Equal(1, finalRefund.AttemptCount);
            Assert.Equal(0, finalRefund.RetryCount); // Không retry vì là lỗi non-retryable
            Assert.Null(finalRefund.NextRetryAt);
        }
    }
}
