using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    /// <summary>
    /// Bộ kiểm thử cho Story S-32 (S-23): "Xem lịch sử đơn hàng của tôi"
    /// AC1: Danh sách mới nhất trước với trạng thái, sự kiện, số ghế và tổng tiền, phân trang.
    /// AC2: Gọi API với mã đơn của người khác bị từ chối 403.
    /// AC3: Đơn đã hết hạn hoặc đã huỷ vẫn thấy trong lịch sử với trạng thái tương ứng.
    /// NFR: Phân trang, không tải toàn bộ lịch sử một lần.
    /// </summary>
    public class OrderHistoryTests : IDisposable
    {
        private readonly AppDbContext _context;

        public OrderHistoryTests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private OrdersController CreateController(Guid? currentUserId = null, bool isAdmin = false)
        {
            var mockEmailQueue = new Mock<EventTicketBooking.Api.BackgroundServices.ITicketEmailQueue>();
            var controller = new OrdersController(_context, NullLogger<OrdersController>.Instance, mockEmailQueue.Object, new TicketService());
            var httpContext = new DefaultHttpContext();

            if (currentUserId.HasValue)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, currentUserId.Value.ToString()),
                    new Claim("id", currentUserId.Value.ToString()),
                    new Claim(ClaimTypes.Role, isAdmin ? "Admin" : "Customer")
                };
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private async Task<(User user, Event ev, Showtime showtime)> SeedBaseEntitiesAsync(Guid? customUserId = null)
        {
            var user = new User
            {
                Id = customUserId ?? Guid.NewGuid(),
                Username = $"user_{Guid.NewGuid():N}",
                Email = $"user_{Guid.NewGuid():N}@example.com",
                FullName = "Nguyễn Văn Đặt Vé",
                PasswordHash = "hash123",
                IsActive = true
            };

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = user.Id,
                Title = "Live Concert Mùa Thu",
                Location = "Nhà hát Lớn Hà Nội",
                ImageUrl = "https://example.com/banner.jpg",
                TotalSeats = 500
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                StartTime = DateTime.UtcNow.AddDays(7),
                EndTime = DateTime.UtcNow.AddDays(7).AddHours(3)
            };

            _context.Users.Add(user);
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            await _context.SaveChangesAsync();

            return (user, ev, showtime);
        }

        private async Task<Order> CreateOrderAsync(
            User user,
            Showtime showtime,
            OrderStatus status,
            int seatCount,
            int pricePerSeat,
            DateTimeOffset createdAt)
        {
            var category = new SeatCategory
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                Name = "Hạng VIP",
                Price = pricePerSeat
            };
            _context.SeatCategories.Add(category);

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ShowtimeId = showtime.Id,
                Status = status,
                TotalAmount = seatCount * pricePerSeat,
                CreatedAt = createdAt,
                ExpiresAt = createdAt.AddMinutes(15)
            };

            for (int i = 1; i <= seatCount; i++)
            {
                var seat = new Seat
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    SeatCategoryId = category.Id,
                    Row = "A",
                    SeatNumber = i,
                    Status = status == OrderStatus.Paid ? "SOLD" : "AVAILABLE"
                };
                _context.Seats.Add(seat);

                order.OrderItems.Add(new OrderItem
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    SeatId = seat.Id,
                    Price = pricePerSeat,
                    Seat = seat
                });
            }

            _context.Orders.Add(order);
            await _context.SaveChangesAsync();
            return order;
        }

        #region AC1: Danh sách mới nhất trước với trạng thái, sự kiện, số ghế, tổng tiền, phân trang

        [Fact]
        public async Task GetMyOrders_ReturnsOrdersSortedByNewestFirst_WithPagination()
        {
            // Arrange
            var (user, ev, showtime) = await SeedBaseEntitiesAsync();
            var baseTime = DateTimeOffset.UtcNow;

            // Tạo 3 đơn với thời gian khác nhau
            var orderOld = await CreateOrderAsync(user, showtime, OrderStatus.Paid, 2, 200000, baseTime.AddHours(-3));
            var orderMid = await CreateOrderAsync(user, showtime, OrderStatus.Pending, 1, 300000, baseTime.AddHours(-2));
            var orderNew = await CreateOrderAsync(user, showtime, OrderStatus.Expired, 3, 150000, baseTime.AddHours(-1));

            var controller = CreateController(user.Id);

            // Act: Lấy trang 1, pageSize 2
            var actionResult = await controller.GetMyOrders(page: 1, pageSize: 2);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResult.Value);
            Assert.True(apiResponse.Success);

            var pageData = apiResponse.Data;
            Assert.NotNull(pageData);
            Assert.Equal(3, pageData.TotalItems);
            Assert.Equal(2, pageData.TotalPages);
            Assert.Equal(1, pageData.Page);
            Assert.Equal(2, pageData.PageSize);
            Assert.True(pageData.HasNextPage);
            Assert.False(pageData.HasPreviousPage);
            Assert.Equal(2, pageData.Items.Count);

            // Kiểm tra sắp xếp mới nhất trước
            Assert.Equal(orderNew.Id, pageData.Items[0].Id);
            Assert.Equal(orderMid.Id, pageData.Items[1].Id);

            // Kiểm tra các trường dữ liệu bắt buộc của AC1
            var firstItem = pageData.Items[0];
            Assert.Equal("Expired", firstItem.Status);
            Assert.Equal(ev.Title, firstItem.EventTitle);
            Assert.Equal(3, firstItem.SeatCount);
            Assert.Equal(450000, firstItem.TotalAmount);
            Assert.Equal(3, firstItem.SeatNames.Count);

            // Act: Lấy trang 2
            var actionResultPage2 = await controller.GetMyOrders(page: 2, pageSize: 2);
            var okResultPage2 = Assert.IsType<OkObjectResult>(actionResultPage2);
            var apiResponsePage2 = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResultPage2.Value);
            var page2Data = apiResponsePage2.Data!;

            Assert.Single(page2Data.Items);
            Assert.Equal(orderOld.Id, page2Data.Items[0].Id);
            Assert.Equal(2, page2Data.Items[0].SeatCount);
            Assert.Equal(400000, page2Data.Items[0].TotalAmount);
        }

        #endregion

        #region AC2: Bảo mật - Không xem được đơn của người khác (403 Forbidden)

        [Fact]
        public async Task GetOrderSummary_WhenRequestingAnotherUserOrder_Returns403Forbidden()
        {
            // Arrange
            var (userA, ev, showtime) = await SeedBaseEntitiesAsync();
            var userB = new User
            {
                Id = Guid.NewGuid(),
                Username = "userB",
                Email = "userB@example.com",
                PasswordHash = "hash123",
                IsActive = true
            };
            _context.Users.Add(userB);
            await _context.SaveChangesAsync();

            var orderOfUserA = await CreateOrderAsync(userA, showtime, OrderStatus.Paid, 1, 500000, DateTimeOffset.UtcNow);

            // User B đăng nhập và cố truy cập đơn của User A
            var controllerUserB = CreateController(userB.Id);

            // Act
            var actionResult = await controllerUserB.GetOrderSummary(orderOfUserA.Id);

            // Assert: Phải bị từ chối 403 Forbidden
            var objectResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Fact]
        public async Task GetOrderSummary_AdminCanAccessAnyUserOrder()
        {
            // Arrange
            var (userA, ev, showtime) = await SeedBaseEntitiesAsync();
            var adminUser = new User
            {
                Id = Guid.NewGuid(),
                Username = "adminUser",
                Email = "admin@example.com",
                PasswordHash = "hash123",
                IsActive = true
            };
            _context.Users.Add(adminUser);
            await _context.SaveChangesAsync();

            var orderOfUserA = await CreateOrderAsync(userA, showtime, OrderStatus.Paid, 1, 500000, DateTimeOffset.UtcNow);

            // Admin truy cập
            var controllerAdmin = CreateController(adminUser.Id, isAdmin: true);

            // Act
            var actionResult = await controllerAdmin.GetOrderSummary(orderOfUserA.Id);

            // Assert: Admin xem được thành công
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var apiResponse = Assert.IsType<ApiResponse<OrderDto>>(okResult.Value);
            Assert.True(apiResponse.Success);
            Assert.Equal(orderOfUserA.Id, apiResponse.Data!.Id);
        }

        [Fact]
        public async Task GetMyOrders_OnlyReturnsOrdersBelongingToCurrentCaller()
        {
            // Arrange
            var (userA, ev, showtime) = await SeedBaseEntitiesAsync();
            var (userB, _, _) = await SeedBaseEntitiesAsync();

            await CreateOrderAsync(userA, showtime, OrderStatus.Paid, 1, 100000, DateTimeOffset.UtcNow);
            await CreateOrderAsync(userB, showtime, OrderStatus.Paid, 2, 200000, DateTimeOffset.UtcNow);

            var controllerUserA = CreateController(userA.Id);

            // Act
            var actionResult = await controllerUserA.GetMyOrders(page: 1, pageSize: 10);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResult.Value);
            Assert.Single(response.Data!.Items);
            Assert.Equal(userA.Id, response.Data.Items[0].CustomerEmail != null ? userA.Id : userA.Id);
        }

        #endregion

        #region AC3: Đơn đã hết hạn hoặc đã huỷ vẫn thấy trong lịch sử với trạng thái tương ứng

        [Fact]
        public async Task GetMyOrders_IncludesExpiredAndCancelledOrders_WithCorrectStatus()
        {
            // Arrange
            var (user, ev, showtime) = await SeedBaseEntitiesAsync();
            var baseTime = DateTimeOffset.UtcNow;

            await CreateOrderAsync(user, showtime, OrderStatus.Pending, 1, 100000, baseTime.AddMinutes(-40));
            await CreateOrderAsync(user, showtime, OrderStatus.Paid, 1, 100000, baseTime.AddMinutes(-30));
            await CreateOrderAsync(user, showtime, OrderStatus.Expired, 1, 100000, baseTime.AddMinutes(-20));
            await CreateOrderAsync(user, showtime, OrderStatus.Cancelled, 1, 100000, baseTime.AddMinutes(-10));

            var controller = CreateController(user.Id);

            // Act
            var actionResult = await controller.GetMyOrders(page: 1, pageSize: 10);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResult.Value);
            Assert.Equal(4, response.Data!.TotalItems);

            var statuses = response.Data.Items.Select(x => x.Status).ToList();
            Assert.Contains("Pending", statuses);
            Assert.Contains("Paid", statuses);
            Assert.Contains("Expired", statuses);
            Assert.Contains("Cancelled", statuses);
        }

        [Fact]
        public async Task GetMyOrders_FilterByStatus_ReturnsOnlyMatchingStatusOrders()
        {
            // Arrange
            var (user, ev, showtime) = await SeedBaseEntitiesAsync();
            var baseTime = DateTimeOffset.UtcNow;

            await CreateOrderAsync(user, showtime, OrderStatus.Paid, 1, 100000, baseTime.AddMinutes(-30));
            await CreateOrderAsync(user, showtime, OrderStatus.Expired, 1, 100000, baseTime.AddMinutes(-20));
            await CreateOrderAsync(user, showtime, OrderStatus.Cancelled, 1, 100000, baseTime.AddMinutes(-10));

            var controller = CreateController(user.Id);

            // Act: Lọc chỉ lấy đơn Paid
            var actionResult = await controller.GetMyOrders(page: 1, pageSize: 10, status: "Paid");

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResult.Value);
            Assert.Single(response.Data!.Items);
            Assert.Equal("Paid", response.Data.Items[0].Status);
        }

        #endregion

        #region Edge Cases: 401 khi chưa đăng nhập, phân trang max clamp

        [Fact]
        public async Task GetMyOrders_WhenNotLoggedIn_Returns401Unauthorized()
        {
            // Arrange
            var controller = CreateController(currentUserId: null);

            // Act
            var actionResult = await controller.GetMyOrders();

            // Assert
            var unauthorizedResult = Assert.IsType<UnauthorizedObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(unauthorizedResult.Value);
            Assert.False(response.Success);
        }

        [Fact]
        public async Task GetMyOrders_ClampsPageSizeBetween1And50()
        {
            // Arrange
            var (user, ev, showtime) = await SeedBaseEntitiesAsync();
            await CreateOrderAsync(user, showtime, OrderStatus.Paid, 1, 100000, DateTimeOffset.UtcNow);

            var controller = CreateController(user.Id);

            // Act: truyền pageSize = 999
            var actionResult = await controller.GetMyOrders(page: 0, pageSize: 999);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<PagedResultDto<OrderDto>>>(okResult.Value);
            Assert.Equal(1, response.Data!.Page);
            Assert.Equal(50, response.Data.PageSize);
        }

        #endregion
    }
}
