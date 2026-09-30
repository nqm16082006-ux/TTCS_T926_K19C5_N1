using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Reflection;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Middlewares;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Test Suite toàn diện cho Task T-50 (API trạng thái đơn hàng để polling nhẹ)
    /// và Task T-51 (Trang kết quả thanh toán chờ xác nhận từ máy chủ).
    /// </summary>
    public class OrderStatusApiTests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly OrdersController _controller;
        private readonly Guid _ownerUserId = Guid.NewGuid();
        private readonly Guid _otherUserId = Guid.NewGuid();

        public OrderStatusApiTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            _context = new AppDbContext(options);
            _controller = new OrdersController(_context, NullLogger<OrdersController>.Instance);

            // Giả lập HttpContext mặc định với user là owner
            SetCurrentUser(_ownerUserId);
        }

        private void SetCurrentUser(Guid? userId)
        {
            var httpContext = new DefaultHttpContext();
            if (userId.HasValue)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString())
                };
                var identity = new ClaimsIdentity(claims, "TestAuth");
                httpContext.User = new ClaimsPrincipal(identity);
            }
            else
            {
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
        }

        private Order CreateTestOrder(Guid userId, OrderStatus status = OrderStatus.Pending)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ShowtimeId = Guid.NewGuid(),
                Status = status,
                TotalAmount = 250000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _context.Orders.Add(order);
            _context.SaveChanges();
            return order;
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        #region T-50: Test API trạng thái đơn hàng (GET /api/v1/orders/{orderId}/status)

        [Fact]
        public async Task T50_Owner_Returns200WithCurrentStatus()
        {
            // Arrange: Tạo đơn Pending thuộc về owner
            var order = CreateTestOrder(_ownerUserId, OrderStatus.Pending);

            // Act
            var actionResult = await _controller.GetOrderStatus(order.Id);

            // Assert: Trả về 200 OK với đúng status "Pending"
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);

            var apiResponse = Assert.IsType<ApiResponse<OrderStatusResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.NotNull(apiResponse.Data);
            Assert.Equal("Pending", apiResponse.Data.Status);
        }

        [Fact]
        public async Task T50_OtherUser_Returns403Forbidden()
        {
            // Arrange: Đơn thuộc về _ownerUserId nhưng người gọi là _otherUserId
            var order = CreateTestOrder(_ownerUserId, OrderStatus.Pending);
            SetCurrentUser(_otherUserId);

            // Act
            var actionResult = await _controller.GetOrderStatus(order.Id);

            // Assert: Phải trả về 403 Forbidden theo AC
            var objectResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Fact]
        public async Task T50_Unauthenticated_Returns401Unauthorized()
        {
            // Arrange: Đơn tồn tại nhưng người gọi không có token/claims
            var order = CreateTestOrder(_ownerUserId, OrderStatus.Pending);
            SetCurrentUser(null);

            // Act
            var actionResult = await _controller.GetOrderStatus(order.Id);

            // Assert: Phải trả về 401 Unauthorized
            var unauthResult = Assert.IsType<UnauthorizedObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);
        }

        [Fact]
        public async Task T50_OrderNotFound_Returns404NotFound()
        {
            // Arrange: GUID không tồn tại trong DB
            var nonExistentId = Guid.NewGuid();

            // Act
            var actionResult = await _controller.GetOrderStatus(nonExistentId);

            // Assert: Phải trả về 404 NotFound
            var notFoundResult = Assert.IsType<NotFoundObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);
        }

        [Fact]
        public async Task T50_DbStatusChange_ReflectsImmediately_NoCache()
        {
            // Arrange: Đơn ban đầu là Pending
            var order = CreateTestOrder(_ownerUserId, OrderStatus.Pending);

            // Lần 1: Gọi kiểm tra trạng thái ban đầu
            var result1 = await _controller.GetOrderStatus(order.Id) as OkObjectResult;
            var data1 = Assert.IsType<ApiResponse<OrderStatusResponseDto>>(result1!.Value).Data;
            Assert.Equal("Pending", data1!.Status);

            // Giả lập webhook T-46 cập nhật trạng thái đơn thành Paid
            order.Status = OrderStatus.Paid;
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await _context.SaveChangesAsync();

            // Lần 2: Gọi lại endpoint T-50
            var result2 = await _controller.GetOrderStatus(order.Id) as OkObjectResult;
            var data2 = Assert.IsType<ApiResponse<OrderStatusResponseDto>>(result2!.Value).Data;

            // Assert: Lần 2 phải thấy trạng thái Paid ngay lập tức (không bị lưu cache cũ)
            Assert.Equal("Paid", data2!.Status);

            // Kiểm tra header no-cache đã được gắn vào Response
            var cacheControl = _controller.Response.Headers["Cache-Control"].ToString();
            var pragma = _controller.Response.Headers["Pragma"].ToString();
            Assert.Contains("no-cache", cacheControl);
            Assert.Contains("no-store", cacheControl);
            Assert.Equal("no-cache", pragma);
        }

        [Fact]
        public async Task T50_ExecutionBenchmark_Under100ms()
        {
            // Arrange: Chuẩn bị đơn
            var order = CreateTestOrder(_ownerUserId, OrderStatus.Pending);

            // Warmup JIT compilation & EF Core query tree compilation
            await _controller.GetOrderStatus(order.Id);

            // Act: Đo thời gian thực thi sau khi đã warmup JIT
            var sw = Stopwatch.StartNew();
            var result = await _controller.GetOrderStatus(order.Id);
            sw.Stop();

            // Assert: Phản hồi dưới 100ms
            Assert.NotNull(result);
            Assert.True(sw.ElapsedMilliseconds < 100, $"Thời gian phản hồi {sw.ElapsedMilliseconds}ms vượt quá ngưỡng 100ms!");
        }

        [Fact]
        public async Task T50_EndToEnd_RealSqliteDatabase_FullFlow()
        {
            // 1. Tìm đường dẫn file SQLite thật eventticket_dev.db
            string? currentDir = AppContext.BaseDirectory;
            string? dbPath = null;
            while (!string.IsNullOrEmpty(currentDir))
            {
                var candidate = Path.Combine(currentDir, "src", "EventTicketBooking.Api", "eventticket_dev.db");
                if (File.Exists(candidate))
                {
                    dbPath = candidate;
                    break;
                }
                var parent = Directory.GetParent(currentDir);
                if (parent == null || parent.FullName == currentDir)
                    break;
                currentDir = parent.FullName;
            }

            if (dbPath == null || !File.Exists(dbPath))
            {
                // Nếu không tìm thấy file db local thì bỏ qua
                return;
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var sqliteDb = new AppDbContext(options);

            // Tìm hoặc tạo một User và Showtime trong DB thật
            var owner = await sqliteDb.Users.FirstOrDefaultAsync();
            var showtime = await sqliteDb.Showtimes.FirstOrDefaultAsync();

            if (owner == null || showtime == null)
            {
                return;
            }

            // Tạo một Order thật trong bảng orders
            var testOrder = new Order
            {
                Id = Guid.NewGuid(),
                UserId = owner.Id,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Pending,
                TotalAmount = 150000,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            sqliteDb.Orders.Add(testOrder);
            await sqliteDb.SaveChangesAsync();

            try
            {
                var controller = new OrdersController(sqliteDb, NullLogger<OrdersController>.Instance);

                // A. Owner -> 200 OK với status "Pending"
                var ownerHttpContext = new DefaultHttpContext();
                ownerHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, owner.Id.ToString())
                }, "TestAuth"));
                controller.ControllerContext = new ControllerContext { HttpContext = ownerHttpContext };

                var okResult = await controller.GetOrderStatus(testOrder.Id) as OkObjectResult;
                Assert.NotNull(okResult);
                Assert.Equal(StatusCodes.Status200OK, okResult.StatusCode);
                var body = Assert.IsType<ApiResponse<OrderStatusResponseDto>>(okResult.Value);
                Assert.Equal("Pending", body.Data?.Status);

                // B. Other user -> 403 Forbidden
                var otherUserHttpContext = new DefaultHttpContext();
                otherUserHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
                }, "TestAuth"));
                controller.ControllerContext = new ControllerContext { HttpContext = otherUserHttpContext };

                var forbiddenResult = await controller.GetOrderStatus(testOrder.Id) as ObjectResult;
                Assert.NotNull(forbiddenResult);
                Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);

                // C. No auth -> 401 Unauthorized
                var noAuthHttpContext = new DefaultHttpContext();
                noAuthHttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
                controller.ControllerContext = new ControllerContext { HttpContext = noAuthHttpContext };

                var unauthResult = await controller.GetOrderStatus(testOrder.Id) as UnauthorizedObjectResult;
                Assert.NotNull(unauthResult);
                Assert.Equal(StatusCodes.Status401Unauthorized, unauthResult.StatusCode);

                // D. Not found -> 404 NotFound
                controller.ControllerContext = new ControllerContext { HttpContext = ownerHttpContext };
                var notFoundResult = await controller.GetOrderStatus(Guid.NewGuid()) as NotFoundObjectResult;
                Assert.NotNull(notFoundResult);
                Assert.Equal(StatusCodes.Status404NotFound, notFoundResult.StatusCode);

                // E. Database chuyển sang Paid -> lần gọi tiếp theo trả về Paid ngay
                testOrder.Status = OrderStatus.Paid;
                testOrder.UpdatedAt = DateTimeOffset.UtcNow;
                await sqliteDb.SaveChangesAsync();

                var paidResult = await controller.GetOrderStatus(testOrder.Id) as OkObjectResult;
                Assert.NotNull(paidResult);
                var paidBody = Assert.IsType<ApiResponse<OrderStatusResponseDto>>(paidResult.Value);
                Assert.Equal("Paid", paidBody.Data?.Status);
            }
            finally
            {
                // Dọn dẹp Order test khỏi database thật
                sqliteDb.Orders.Remove(testOrder);
                await sqliteDb.SaveChangesAsync();
            }
        }

        [Fact]
        public void T50_HasRequireRoleAttribute_InsteadOfDefaultAuthorize()
        {
            var method = typeof(OrdersController).GetMethod(nameof(OrdersController.GetOrderStatus));
            Assert.NotNull(method);

            var requireRoleAttr = method.GetCustomAttribute<RequireRoleAttribute>();
            Assert.NotNull(requireRoleAttr);
        }

        #endregion

        #region T-51: Test file và logic trang payment-result.html

        [Fact]
        public void T51_PaymentResultHtml_ExistsAndContainsRequiredElements()
        {
            string? currentDir = AppContext.BaseDirectory;
            string? htmlPath = null;
            while (!string.IsNullOrEmpty(currentDir))
            {
                var candidate1 = Path.Combine(currentDir, "src", "EventTicketBooking.Api", "wwwroot", "payment-result.html");
                if (File.Exists(candidate1))
                {
                    htmlPath = candidate1;
                    break;
                }

                var candidate2 = Path.Combine(currentDir, "EventTicketBooking.Api", "wwwroot", "payment-result.html");
                if (File.Exists(candidate2))
                {
                    htmlPath = candidate2;
                    break;
                }

                var parent = Directory.GetParent(currentDir);
                if (parent == null || parent.FullName == currentDir)
                    break;
                currentDir = parent.FullName;
            }

            Assert.True(htmlPath != null && File.Exists(htmlPath), "File payment-result.html phải tồn tại trong wwwroot.");

            var content = File.ReadAllText(htmlPath);

            // 1. Trạng thái ban đầu: "Đang xác nhận thanh toán"
            Assert.Contains("Đang xác nhận thanh toán", content);

            // 2. Chỉ đọc orderId từ URL, không dùng query status để quyết định kết quả
            Assert.Contains("urlParams.get('orderId')", content);
            Assert.DoesNotContain("urlParams.get('status')", content);

            // 3. Polling endpoint T-50
            Assert.Contains("/api/v1/orders/", content);
            Assert.Contains("/status", content);

            // 4. Polling 2 giây và Timeout tối đa 60 giây (60000ms)
            Assert.Contains("2000", content);
            Assert.Contains("60000", content);

            // 5. Quản lý dọn dẹp khi rời trang
            Assert.Contains("pagehide", content);
            Assert.Contains("beforeunload", content);
            Assert.Contains("AbortController", content);

            // 6. Sau 60s: thông báo chưa nhận được xác nhận, hướng dẫn kiểm tra lịch sử đơn, KHÔNG được kết luận thất bại
            Assert.Contains("Chưa nhận được xác nhận", content);
            Assert.Contains("lịch sử đơn hàng", content);
        }

        #endregion
    }
}
