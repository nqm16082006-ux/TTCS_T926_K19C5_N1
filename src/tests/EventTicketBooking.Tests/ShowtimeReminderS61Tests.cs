using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.DTOs.Common;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class ShowtimeReminderS61Tests : IDisposable
    {
        private readonly AppDbContext _context;
        private readonly Mock<IEmailService> _emailServiceMock;
        private readonly Mock<IOrderAuditService> _auditServiceMock;
        private readonly ShowtimeReminderService _reminderService;
        private readonly Guid _organizerId = Guid.NewGuid();
        private readonly Guid _adminId = Guid.NewGuid();
        private readonly Guid _strangerId = Guid.NewGuid();

        public ShowtimeReminderS61Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;

            _context = new AppDbContext(options);
            _emailServiceMock = new Mock<IEmailService>();
            _auditServiceMock = new Mock<IOrderAuditService>();

            _reminderService = new ShowtimeReminderService(
                _context,
                _emailServiceMock.Object,
                NullLogger<ShowtimeReminderService>.Instance,
                _auditServiceMock.Object);
        }

        public void Dispose()
        {
            _context.Database.EnsureDeleted();
            _context.Dispose();
        }

        private EventController CreateController(Guid userId, string role = "Organizer")
        {
            var controller = new EventController(
                _context,
                _auditServiceMock.Object,
                null,
                null,
                null,
                reminderService: _reminderService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                            new Claim(ClaimTypes.Role, role)
                        }, "TestAuth"))
                    }
                }
            };

            return controller;
        }

        private async Task<(Event ev, Showtime showtime, Order paidOrder, User buyer)> SeedEventWithShowtimeAndOrderAsync(
            DateTime startTime,
            OrderStatus status = OrderStatus.Paid,
            DateTimeOffset? reminderSentAt = null,
            ShowtimeStatus showtimeStatus = ShowtimeStatus.OnSale)
        {
            var organizer = new User
            {
                Id = _organizerId,
                Username = "organizer",
                Email = "organizer@test.com",
                PasswordHash = "hash",
                IsActive = true
            };

            var buyer = new User
            {
                Id = Guid.NewGuid(),
                Username = "buyer1",
                FullName = "Nguyễn Văn Người Mua",
                Email = "buyer1@test.com",
                PasswordHash = "hash",
                IsActive = true
            };

            var ev = new Event
            {
                Id = Guid.NewGuid(),
                OwnerId = _organizerId,
                Owner = organizer,
                Title = "Live Concert Mùa Thu 2026",
                Location = "Trung tâm Hội nghị Quốc gia, Hà Nội",
                StartTime = startTime,
                EndTime = startTime.AddHours(3),
                TotalSeats = 100
            };

            var showtime = new Showtime
            {
                Id = Guid.NewGuid(),
                EventId = ev.Id,
                Event = ev,
                StartTime = startTime,
                EndTime = startTime.AddHours(3),
                AvailableSeats = 100
            };
            if (showtimeStatus == ShowtimeStatus.OnSale)
            {
                var category = new SeatCategory
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    Name = "VIP",
                    Price = 500000
                };
                showtime.SeatCategories.Add(category);
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
            }
            else if (showtimeStatus == ShowtimeStatus.Cancelled)
            {
                var category = new SeatCategory
                {
                    Id = Guid.NewGuid(),
                    ShowtimeId = showtime.Id,
                    Name = "VIP",
                    Price = 500000
                };
                showtime.SeatCategories.Add(category);
                showtime.ChangeStatus(ShowtimeStatus.OnSale);
                showtime.ChangeStatus(ShowtimeStatus.Cancelled);
            }

            var seat = new Seat
            {
                Id = Guid.NewGuid(),
                ShowtimeId = showtime.Id,
                SeatCategoryId = Guid.NewGuid(),
                Row = "A",
                SeatNumber = 12,
                Status = "SOLD"
            };

            var order = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyer.Id,
                User = buyer,
                ShowtimeId = showtime.Id,
                Showtime = showtime,
                Status = status,
                TotalAmount = 500000,
                ReminderSentAt = reminderSentAt,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            var item = new OrderItem
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                Order = order,
                SeatId = seat.Id,
                Seat = seat,
                Price = 500000
            };

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                OrderItemId = item.Id,
                OrderItem = item,
                TicketCode = "TCK-S61-001"
            };

            item.Ticket = ticket;
            order.OrderItems.Add(item);

            _context.Users.AddRange(organizer, buyer);
            _context.Events.Add(ev);
            _context.Showtimes.Add(showtime);
            _context.Seats.Add(seat);
            _context.Orders.Add(order);
            await _context.SaveChangesAsync();

            return (ev, showtime, order, buyer);
        }

        [Fact]
        public async Task Test01_SendUpcomingShowtimeReminders_SendsEmail_WhenStartsWithin24Hours()
        {
            // Showtime starts in 12 hours (< 24h, > 0)
            var startTime = DateTime.UtcNow.AddHours(12);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            _emailServiceMock
                .Setup(x => x.SendShowtimeReminderEmailAsync(
                    buyer.Email,
                    buyer.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.Is<IEnumerable<string>>(seats => seats.Contains("A12")),
                    It.Is<IEnumerable<string>>(tickets => tickets.Contains("TCK-S61-001")),
                    order.Id.ToString(),
                    It.IsAny<string>()))
                .ReturnsAsync(true);

            // Act
            int sentCount = await _reminderService.SendUpcomingShowtimeRemindersAsync();

            // Assert
            Assert.Equal(1, sentCount);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder!.ReminderSentAt);

            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    buyer.Email,
                    buyer.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<IEnumerable<string>>(),
                    order.Id.ToString(),
                    It.IsAny<string>()),
                Times.Once);

            _auditServiceMock.Verify(
                x => x.Record(
                    order.Id,
                    "ORDER",
                    order.Id.ToString(),
                    "EMAIL_REMINDER_SENT",
                    "Paid",
                    "Paid",
                    "SYSTEM",
                    "ShowtimeReminderWorker",
                    null,
                    It.IsAny<string>(),
                    null),
                Times.Once);
        }

        [Fact]
        public async Task Test02_SendUpcomingShowtimeReminders_DoesNotSend_WhenStartsInMoreThan24Hours()
        {
            // Showtime starts in 36 hours (> 24h)
            var startTime = DateTime.UtcNow.AddHours(36);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            int sentCount = await _reminderService.SendUpcomingShowtimeRemindersAsync();

            Assert.Equal(0, sentCount);
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.Null(updatedOrder!.ReminderSentAt);
            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Test03_SendUpcomingShowtimeReminders_DoesNotSend_WhenAlreadySent()
        {
            // Showtime starts in 8 hours, but reminder was already sent 2 hours ago
            var startTime = DateTime.UtcNow.AddHours(8);
            var alreadySentAt = DateTimeOffset.UtcNow.AddHours(-2);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime, reminderSentAt: alreadySentAt);

            int sentCount = await _reminderService.SendUpcomingShowtimeRemindersAsync();

            Assert.Equal(0, sentCount);
            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Test04_SendUpcomingShowtimeReminders_DoesNotSend_WhenOrderNotPaid()
        {
            // Order is Pending (unpaid)
            var startTime = DateTime.UtcNow.AddHours(10);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime, status: OrderStatus.Pending);

            int sentCount = await _reminderService.SendUpcomingShowtimeRemindersAsync();

            Assert.Equal(0, sentCount);
            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Test05_SendUpcomingShowtimeReminders_DoesNotSend_WhenShowtimeIsCancelled()
        {
            // Showtime is Cancelled
            var startTime = DateTime.UtcNow.AddHours(10);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime, showtimeStatus: ShowtimeStatus.Cancelled);

            int sentCount = await _reminderService.SendUpcomingShowtimeRemindersAsync();

            Assert.Equal(0, sentCount);
            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Test06_SendShowtimeRescheduledNotifications_SendsUpdateEmail_ToAllPaidBuyers()
        {
            var oldStart = DateTime.UtcNow.AddDays(2);
            var oldEnd = oldStart.AddHours(2);
            var (ev, showtime, order1, buyer1) = await SeedEventWithShowtimeAndOrderAsync(oldStart);

            // Add second buyer with paid order
            var buyer2 = new User
            {
                Id = Guid.NewGuid(),
                Username = "buyer2",
                FullName = "Trần Thị Khách Hàng",
                Email = "buyer2@test.com",
                PasswordHash = "hash",
                IsActive = true
            };
            var order2 = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyer2.Id,
                User = buyer2,
                ShowtimeId = showtime.Id,
                Showtime = showtime,
                Status = OrderStatus.Paid,
                TotalAmount = 500000
            };
            var seat2 = new Seat { Id = Guid.NewGuid(), ShowtimeId = showtime.Id, Row = "B", SeatNumber = 5, Status = "SOLD" };
            order2.OrderItems.Add(new OrderItem { Id = Guid.NewGuid(), OrderId = order2.Id, SeatId = seat2.Id, Seat = seat2, Price = 500000 });

            // Add third order that is CANCELLED (should NOT receive rescheduled email)
            var orderCancelled = new Order
            {
                Id = Guid.NewGuid(),
                UserId = buyer1.Id,
                ShowtimeId = showtime.Id,
                Status = OrderStatus.Cancelled
            };

            _context.Users.Add(buyer2);
            _context.Seats.Add(seat2);
            _context.Orders.AddRange(order2, orderCancelled);
            await _context.SaveChangesAsync();

            var newStart = oldStart.AddHours(3);
            var newEnd = oldEnd.AddHours(3);

            // Act
            int count = await _reminderService.SendShowtimeRescheduledNotificationsAsync(
                showtime.Id,
                oldStart,
                oldEnd,
                newStart,
                newEnd);

            // Assert
            Assert.Equal(2, count);

            _emailServiceMock.Verify(
                x => x.SendShowtimeRescheduledEmailAsync(
                    buyer1.Email,
                    buyer1.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.Is<IEnumerable<string>>(s => s.Contains("A12")),
                    order1.Id.ToString(),
                    It.IsAny<string>()),
                Times.Once);

            _emailServiceMock.Verify(
                x => x.SendShowtimeRescheduledEmailAsync(
                    buyer2.Email,
                    buyer2.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.Is<IEnumerable<string>>(s => s.Contains("B5")),
                    order2.Id.ToString(),
                    It.IsAny<string>()),
                Times.Once);

            _auditServiceMock.Verify(
                x => x.Record(
                    order1.Id,
                    "ORDER",
                    order1.Id.ToString(),
                    "SHOWTIME_RESCHEDULED",
                    "Paid",
                    "Paid",
                    "ORGANIZER",
                    "Organizer",
                    null,
                    It.IsAny<string>(),
                    null),
                Times.Once);
        }

        [Fact]
        public async Task Test07_SendShowtimeRescheduledNotifications_ResetsReminderSentAt_WhenNewTimeIsFarFuture()
        {
            var oldStart = DateTime.UtcNow.AddHours(10);
            var oldEnd = oldStart.AddHours(2);
            // Had already received reminder for tonight's showtime
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(oldStart, reminderSentAt: DateTimeOffset.UtcNow);

            // Event rescheduled to 10 days in the future (> 24h)
            var newStart = DateTime.UtcNow.AddDays(10);
            var newEnd = newStart.AddHours(2);

            int count = await _reminderService.SendShowtimeRescheduledNotificationsAsync(
                showtime.Id,
                oldStart,
                oldEnd,
                newStart,
                newEnd);

            Assert.Equal(1, count);
            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            // ReminderSentAt must be reset so they get reminded again 24h before the new date
            Assert.Null(updatedOrder!.ReminderSentAt);
        }

        [Fact]
        public async Task Test08_UpdateShowtime_Controller_TriggersRescheduledNotifications_WhenTimeChanges()
        {
            var oldStart = DateTime.UtcNow.AddDays(3);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(oldStart);

            var controller = CreateController(_organizerId);

            var updateDto = new UpdateShowtimeDto
            {
                StartTime = oldStart.AddHours(2), // Changed time
                EndTime = oldStart.AddHours(5),
                AvailableSeats = 100,
                MaxTicketsPerUser = 10
            };

            var result = await controller.UpdateShowtime(ev.Id, showtime.Id, updateDto);
            var okResult = Assert.IsType<OkObjectResult>(result);
            var apiResponse = Assert.IsType<ApiResponse<ShowtimeResponseDto>>(okResult.Value);
            Assert.True(apiResponse.Success);

            // Check that email service was called for buyer
            _emailServiceMock.Verify(
                x => x.SendShowtimeRescheduledEmailAsync(
                    buyer.Email,
                    buyer.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    order.Id.ToString(),
                    It.IsAny<string>()),
                Times.Once);
        }

        [Fact]
        public async Task Test09_UpdateShowtime_Controller_DoesNotTriggerReschedule_WhenTimeIsUnchanged()
        {
            var startTime = DateTime.UtcNow.AddDays(3);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            var controller = CreateController(_organizerId);

            var updateDto = new UpdateShowtimeDto
            {
                Name = "Updated Name",
                StartTime = showtime.StartTime, // SAME time
                EndTime = showtime.EndTime,     // SAME time
                AvailableSeats = 120,
                MaxTicketsPerUser = 10
            };

            var result = await controller.UpdateShowtime(ev.Id, showtime.Id, updateDto);
            var okResult = Assert.IsType<OkObjectResult>(result);

            _emailServiceMock.Verify(
                x => x.SendShowtimeRescheduledEmailAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(),
                    It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task Test10_SendShowtimeReminders_ControllerEndpoint_AllowsOrganizer()
        {
            var startTime = DateTime.UtcNow.AddHours(15);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            var controller = CreateController(_organizerId);

            var actionResult = await controller.SendShowtimeReminders(ev.Id, showtime.Id, force: false);
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(okResult.Value);
            Assert.True(response.Success);

            _emailServiceMock.Verify(
                x => x.SendShowtimeReminderEmailAsync(
                    buyer.Email,
                    buyer.FullName!,
                    ev.Title,
                    ev.Location,
                    It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<IEnumerable<string>>(),
                    order.Id.ToString(),
                    It.IsAny<string>()),
                Times.Once);
        }

        [Fact]
        public async Task Test11_SendShowtimeReminders_ControllerEndpoint_DeniesStrangerWith403()
        {
            var startTime = DateTime.UtcNow.AddHours(15);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            // Stranger tries to trigger reminders for organizer's event
            var controller = CreateController(_strangerId, "Organizer");

            var actionResult = await controller.SendShowtimeReminders(ev.Id, showtime.Id);
            var objectResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        }

        [Fact]
        public async Task Test12_TriggerAllUpcomingReminders_ControllerEndpoint_AllowsAdmin()
        {
            var startTime = DateTime.UtcNow.AddHours(8);
            var (ev, showtime, order, buyer) = await SeedEventWithShowtimeAndOrderAsync(startTime);

            var controller = CreateController(_adminId, "Admin");

            var actionResult = await controller.TriggerAllUpcomingReminders();
            var okResult = Assert.IsType<OkObjectResult>(actionResult);
            var response = Assert.IsType<ApiResponse<object>>(okResult.Value);
            Assert.True(response.Success);

            var updatedOrder = await _context.Orders.FindAsync(order.Id);
            Assert.NotNull(updatedOrder!.ReminderSentAt);
        }
    }
}
