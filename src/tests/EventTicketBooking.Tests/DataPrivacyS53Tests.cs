using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Middlewares;
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
    public class DataPrivacyS53Tests : IDisposable
    {
        private readonly AppDbContext _db;
        private readonly IPasswordHasher _passwordHasher;
        private readonly Mock<ITokenService> _tokenServiceMock;
        private readonly DataDeletionService _dataDeletionService;

        public DataPrivacyS53Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: $"DataPrivacyS53TestDb_{Guid.NewGuid()}")
                .Options;

            _db = new AppDbContext(options);
            _passwordHasher = new Argon2PasswordHasher();
            _tokenServiceMock = new Mock<ITokenService>();
            _tokenServiceMock
                .Setup(s => s.GenerateAccessToken(It.IsAny<User>(), It.IsAny<IEnumerable<string>>()))
                .Returns("mock_token");

            _dataDeletionService = new DataDeletionService(_db, NullLogger<DataDeletionService>.Instance);

            // Seed Role
            if (!_db.Roles.Any(r => r.Name == "Customer"))
            {
                _db.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Customer",
                    Description = "Khách hàng mua vé"
                });
            }
            if (!_db.Roles.Any(r => r.Name == "Admin"))
            {
                _db.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Admin",
                    Description = "Quản trị viên hệ thống"
                });
            }
            _db.SaveChanges();
        }

        public void Dispose()
        {
            _db.Dispose();
        }

        private DataPrivacyController CreateController(Guid? userId, string role = "Customer")
        {
            var controller = new DataPrivacyController(_dataDeletionService, NullLogger<DataPrivacyController>.Instance);
            var httpContext = new DefaultHttpContext();

            if (userId.HasValue)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                    new Claim(ClaimTypes.Role, role)
                };
                httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };

            return controller;
        }

        private User CreateTestUser(string email = "khachhang@example.com", string name = "Nguyễn Văn Khách")
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = email.Split('@')[0],
                Email = email,
                FullName = name,
                PasswordHash = _passwordHasher.Hash("MatKhau@123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
            _db.SaveChanges();
            return user;
        }

        private User CreateAdminUser(string email = "admin@eventpulse.vn")
        {
            var adminRole = _db.Roles.First(r => r.Name == "Admin");
            var admin = new User
            {
                Id = Guid.NewGuid(),
                Username = "admin_user",
                Email = email,
                FullName = "Quản trị viên DPO",
                PasswordHash = _passwordHasher.Hash("AdminPass@123"),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
            _db.Users.Add(admin);
            _db.SaveChanges();
            return admin;
        }

        [Fact]
        public async Task User_Can_Create_Data_Deletion_Request_Successfully()
        {
            // Arrange
            var user = CreateTestUser();
            var controller = CreateController(user.Id);

            var dto = new CreateDataDeletionRequestDto
            {
                Reason = "Tôi muốn xóa tài khoản và thông tin cá nhân theo Nghị định 13/2023/NĐ-CP."
            };

            // Act
            var actionResult = await controller.CreateDeletionRequest(dto);

            // Assert
            var createdResult = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);

            var resultDto = Assert.IsType<DataDeletionRequestDto>(createdResult.Value);
            Assert.Equal(user.Id, resultDto.UserId);
            Assert.Equal(user.Email, resultDto.RequesterEmail);
            Assert.Equal("Pending", resultDto.Status);
            Assert.Equal(dto.Reason, resultDto.Reason);

            var inDb = await _db.DataDeletionRequests.FirstOrDefaultAsync(r => r.UserId == user.Id);
            Assert.NotNull(inDb);
            Assert.Equal(DataDeletionRequestStatus.Pending, inDb.Status);
        }

        [Fact]
        public async Task User_Cannot_Create_Duplicate_Pending_Request()
        {
            // Arrange
            var user = CreateTestUser();
            var controller = CreateController(user.Id);

            var dto = new CreateDataDeletionRequestDto { Reason = "Yêu cầu lần 1" };
            await controller.CreateDeletionRequest(dto);

            // Act - Gửi lần 2 khi lần 1 đang Pending
            var secondResult = await controller.CreateDeletionRequest(new CreateDataDeletionRequestDto { Reason = "Yêu cầu lần 2" });

            // Assert
            var badRequest = Assert.IsType<BadRequestObjectResult>(secondResult);
            Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);
        }

        [Fact]
        public async Task User_Can_Check_Deletion_Status()
        {
            // Arrange
            var user = CreateTestUser();
            var controller = CreateController(user.Id);

            // Act 1: Chưa gửi yêu cầu
            var statusResult1 = await controller.GetDeletionStatus();
            var okResult1 = Assert.IsType<OkObjectResult>(statusResult1);
            var statusDto1 = Assert.IsType<DataDeletionStatusDto>(okResult1.Value);
            Assert.False(statusDto1.HasPendingRequest);
            Assert.False(statusDto1.IsAnonymized);
            Assert.Null(statusDto1.LatestRequest);

            // Act 2: Đã gửi yêu cầu
            await controller.CreateDeletionRequest(new CreateDataDeletionRequestDto { Reason = "Lý do cá nhân" });
            var statusResult2 = await controller.GetDeletionStatus();
            var okResult2 = Assert.IsType<OkObjectResult>(statusResult2);
            var statusDto2 = Assert.IsType<DataDeletionStatusDto>(okResult2.Value);
            Assert.True(statusDto2.HasPendingRequest);
            Assert.NotNull(statusDto2.LatestRequest);
            Assert.Equal("Pending", statusDto2.LatestRequest.Status);
        }

        [Fact]
        public async Task Admin_Can_Get_All_Deletion_Requests_And_Filter()
        {
            // Arrange
            var admin = CreateAdminUser();
            var user1 = CreateTestUser("user1@example.com", "User One");
            var user2 = CreateTestUser("user2@example.com", "User Two");

            await _dataDeletionService.CreateRequestAsync(user1.Id, new CreateDataDeletionRequestDto { Reason = "Lý do 1" });
            var req2 = await _dataDeletionService.CreateRequestAsync(user2.Id, new CreateDataDeletionRequestDto { Reason = "Lý do 2" });
            await _dataDeletionService.ReviewRequestAsync(req2.Id, admin.Id, new ReviewDataDeletionRequestDto { Approved = false, AdminNote = "Chưa đủ điều kiện" });

            var adminController = CreateController(admin.Id, "Admin");

            // Act: Lấy tất cả
            var allResult = await adminController.GetAllDeletionRequests(null);
            var okAll = Assert.IsType<OkObjectResult>(allResult);
            var allList = Assert.IsType<List<DataDeletionRequestDto>>(okAll.Value);
            Assert.Equal(2, allList.Count);

            // Act: Lọc Pending
            var pendingResult = await adminController.GetAllDeletionRequests("Pending");
            var okPending = Assert.IsType<OkObjectResult>(pendingResult);
            var pendingList = Assert.IsType<List<DataDeletionRequestDto>>(okPending.Value);
            Assert.Single(pendingList);
            Assert.Equal(user1.Id, pendingList[0].UserId);

            // Act: Lọc Rejected
            var rejectedResult = await adminController.GetAllDeletionRequests("Rejected");
            var okRejected = Assert.IsType<OkObjectResult>(rejectedResult);
            var rejectedList = Assert.IsType<List<DataDeletionRequestDto>>(okRejected.Value);
            Assert.Single(rejectedList);
            Assert.Equal(user2.Id, rejectedList[0].UserId);
        }

        [Fact]
        public async Task Admin_Can_Reject_Request_With_Reason_And_Data_Is_Not_Anonymized()
        {
            // Arrange
            var admin = CreateAdminUser();
            var user = CreateTestUser();
            var req = await _dataDeletionService.CreateRequestAsync(user.Id, new CreateDataDeletionRequestDto { Reason = "Muốn xoá" });

            var adminController = CreateController(admin.Id, "Admin");
            var reviewDto = new ReviewDataDeletionRequestDto
            {
                Approved = false,
                AdminNote = "Tài khoản đang có tranh chấp khiếu nại, từ chối xoá theo quy định."
            };

            // Act
            var reviewResult = await adminController.ReviewDeletionRequest(req.Id, reviewDto);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(reviewResult);
            var resultDto = Assert.IsType<DataDeletionRequestDto>(okResult.Value);
            Assert.Equal("Rejected", resultDto.Status);
            Assert.Equal(reviewDto.AdminNote, resultDto.AdminNote);

            // Kiểm tra user: không bị ẩn danh
            var userInDb = await _db.Users.FindAsync(user.Id);
            Assert.NotNull(userInDb);
            Assert.False(userInDb.IsAnonymized);
            Assert.True(userInDb.IsActive);
            Assert.Equal(user.Email, userInDb.Email);
            Assert.Equal(user.FullName, userInDb.FullName);
        }

        [Fact]
        public async Task Admin_Approve_Request_Executes_Full_Anonymization_And_Preserves_Accounting_Records()
        {
            // Arrange: Chuẩn bị User có Đơn hàng và Lịch sử giao dịch kế toán
            var admin = CreateAdminUser();
            var user = CreateTestUser("khach.hang.vip@gmail.com", "Trần Thị Khách Hàng");

            // Tạo Event và Showtime
            var ev = new Event { Id = Guid.NewGuid(), Title = "Concert Hòa Nhạc 2026", Location = "Hà Nội", TotalSeats = 1000 };
            var st = new Showtime { Id = Guid.NewGuid(), EventId = ev.Id, StartTime = DateTime.UtcNow.AddDays(10), EndTime = DateTime.UtcNow.AddDays(10).AddHours(3), AvailableSeats = 900 };
            _db.Events.Add(ev);
            _db.Showtimes.Add(st);

            // Tạo 2 Đơn hàng đã thanh toán (Sổ kế toán doanh thu: 500k + 1200k = 1.7tr)
            var order1 = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ShowtimeId = st.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 500000,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-5)
            };
            var order2 = new Order
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ShowtimeId = st.Id,
                Status = OrderStatus.Paid,
                TotalAmount = 1200000,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
            };
            _db.Orders.AddRange(order1, order2);

            // Giao dịch thanh toán liên quan
            var tx1 = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order1.Id,
                OrderCode = 123456,
                Amount = 500000,
                Status = "PAID",
                TransactionId = "TX-PAY-001"
            };
            var tx2 = new PaymentTransaction
            {
                Id = Guid.NewGuid(),
                OrderId = order2.Id,
                OrderCode = 123457,
                Amount = 1200000,
                Status = "PAID",
                TransactionId = "TX-PAY-002"
            };
            _db.PaymentTransactions.AddRange(tx1, tx2);

            // Nhật ký thao tác của người dùng này (S-49 Audit Trail)
            _db.OrderAuditLogs.Add(new OrderAuditLog
            {
                Id = Guid.NewGuid(),
                OrderId = order1.Id,
                Action = "ORDER_CREATED",
                Actor = user.Email,
                ActorType = "USER",
                ActorUserId = user.Id,
                NewStatus = "Pending"
            });

            // Email failure log nếu có
            _db.EmailFailureLogs.Add(new EmailFailureLog
            {
                Id = Guid.NewGuid(),
                OrderId = order1.Id,
                TargetEmail = user.Email,
                ErrorMessage = "SMTP timeout"
            });

            await _db.SaveChangesAsync();

            // Khách gửi yêu cầu xoá
            var req = await _dataDeletionService.CreateRequestAsync(user.Id, new CreateDataDeletionRequestDto
            {
                Reason = "Thực hiện quyền xoá dữ liệu theo Nghị định 13/2023/NĐ-CP"
            });

            var adminController = CreateController(admin.Id, "Admin");
            var reviewDto = new ReviewDataDeletionRequestDto
            {
                Approved = true,
                AdminNote = "Hồ sơ hợp lệ, phê duyệt ẩn danh thông tin cá nhân."
            };

            // Act: Admin DUYỆT yêu cầu
            var reviewResult = await adminController.ReviewDeletionRequest(req.Id, reviewDto);

            // Assert 1: Trạng thái yêu cầu
            var okResult = Assert.IsType<OkObjectResult>(reviewResult);
            var resultDto = Assert.IsType<DataDeletionRequestDto>(okResult.Value);
            Assert.Equal("Approved", resultDto.Status);
            Assert.Equal(admin.Id, resultDto.ReviewedByUserId);

            // Assert 2: Dữ liệu cá nhân trong Users đã bị ẩn danh
            var userAfter = await _db.Users.FindAsync(user.Id);
            Assert.NotNull(userAfter);
            Assert.True(userAfter.IsAnonymized);
            Assert.False(userAfter.IsActive);
            Assert.NotNull(userAfter.AnonymizedAt);
            Assert.Equal("[Dữ liệu đã ẩn danh]", userAfter.FullName);
            Assert.StartsWith("anonymized_", userAfter.Email);
            Assert.EndsWith("@privacy.deleted", userAfter.Email);
            Assert.StartsWith("anonymized_", userAfter.Username);

            // Assert 3: Nhật ký gửi email & nhật ký thao tác đã ẩn danh
            var emailLogAfter = await _db.EmailFailureLogs.FirstOrDefaultAsync(e => e.OrderId == order1.Id);
            Assert.NotNull(emailLogAfter);
            Assert.Equal(userAfter.Email, emailLogAfter.TargetEmail);

            var auditLogAfter = await _db.OrderAuditLogs.FirstOrDefaultAsync(l => l.ActorUserId == user.Id && l.Action == "ORDER_CREATED");
            Assert.NotNull(auditLogAfter);
            Assert.Equal("[Người dùng đã ẩn danh]", auditLogAfter.Actor);

            // Assert 4: BẢO TOÀN SỔ KẾ TOÁN & DOANH THU (AC BẮT BUỘC S-53)
            var ordersInDb = await _db.Orders.Where(o => o.UserId == user.Id).ToListAsync();
            Assert.Equal(2, ordersInDb.Count);
            Assert.Equal(500000, ordersInDb[0].TotalAmount);
            Assert.Equal(1200000, ordersInDb[1].TotalAmount);
            Assert.Equal(OrderStatus.Paid, ordersInDb[0].Status);
            Assert.Equal(OrderStatus.Paid, ordersInDb[1].Status);

            var transactionsInDb = await _db.PaymentTransactions.Where(t => t.OrderId == order1.Id || t.OrderId == order2.Id).ToListAsync();
            Assert.Equal(2, transactionsInDb.Count);
            Assert.Equal(1700000, transactionsInDb.Sum(t => t.Amount));

            // Tổng doanh thu hệ thống không bị mất 1 đồng nào
            var totalSystemRevenue = await _db.Orders.Where(o => o.Status == OrderStatus.Paid).SumAsync(o => o.TotalAmount);
            Assert.Equal(1700000, totalSystemRevenue);

            // Kiểm tra ghi nhận kiểm toán cho hành động ẩn danh
            var anonymizedAuditLogs = await _db.OrderAuditLogs.Where(l => l.Action == "DATA_PRIVACY_ANONYMIZED").ToListAsync();
            Assert.Equal(2, anonymizedAuditLogs.Count); // Ghi nhận cho 2 đơn hàng

            var adminAudit = await _db.AuditLogs.FirstOrDefaultAsync(a => a.Action == "DATA_PRIVACY_ERASURE_APPROVED");
            Assert.NotNull(adminAudit);
            Assert.Equal(user.Id, adminAudit.TargetUserId);
        }

        [Fact]
        public async Task Anonymized_User_Cannot_Log_In_Ever_Again()
        {
            // Arrange
            var admin = CreateAdminUser();
            var originalEmail = "khach.xoa@example.com";
            var rawPassword = "Password@123";
            var user = CreateTestUser(originalEmail);
            user.PasswordHash = _passwordHasher.Hash(rawPassword);
            _db.SaveChanges();

            // Khách gửi yêu cầu và Admin duyệt
            var req = await _dataDeletionService.CreateRequestAsync(user.Id, new CreateDataDeletionRequestDto { Reason = "Xóa tài khoản" });
            await _dataDeletionService.ReviewRequestAsync(req.Id, admin.Id, new ReviewDataDeletionRequestDto { Approved = true });

            var authService = new AuthService(_db, _passwordHasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance);

            // Act 1: Thử đăng nhập bằng email gốc ban đầu
            var loginAttemptOldEmail = await authService.LoginAsync(new LoginRequestDto
            {
                Email = originalEmail,
                Password = rawPassword
            });

            // Assert 1: Email gốc không còn tồn tại trong hệ thống (Đã được đổi sang anonymized)
            Assert.False(loginAttemptOldEmail.Success);

            // Act 2: Thử đăng nhập bằng email ẩn danh mới với mật khẩu cũ
            var userInDb = await _db.Users.FindAsync(user.Id);
            var loginAttemptAnonymized = await authService.LoginAsync(new LoginRequestDto
            {
                Email = userInDb!.Email,
                Password = rawPassword
            });

            // Assert 2: Không thành công
            Assert.False(loginAttemptAnonymized.Success);
        }

        [Fact]
        public void DataPrivacyController_Role_Security_Attributes_Are_Configured_Correctly()
        {
            // Check Controller endpoints metadata
            var userCreateMethod = typeof(DataPrivacyController).GetMethod(nameof(DataPrivacyController.CreateDeletionRequest));
            var userStatusMethod = typeof(DataPrivacyController).GetMethod(nameof(DataPrivacyController.GetDeletionStatus));
            var adminGetAllMethod = typeof(DataPrivacyController).GetMethod(nameof(DataPrivacyController.GetAllDeletionRequests));
            var adminReviewMethod = typeof(DataPrivacyController).GetMethod(nameof(DataPrivacyController.ReviewDeletionRequest));

            Assert.NotNull(userCreateMethod);
            Assert.NotNull(userStatusMethod);
            Assert.NotNull(adminGetAllMethod);
            Assert.NotNull(adminReviewMethod);

            // [RequireRole] for users
            var userCreateAttr = userCreateMethod.GetCustomAttributes(typeof(RequireRoleAttribute), true).FirstOrDefault() as RequireRoleAttribute;
            var userStatusAttr = userStatusMethod.GetCustomAttributes(typeof(RequireRoleAttribute), true).FirstOrDefault() as RequireRoleAttribute;
            Assert.NotNull(userCreateAttr);
            Assert.NotNull(userStatusAttr);

            // [RequireRole("Admin")] for Admin endpoints
            var adminGetAllAttr = adminGetAllMethod.GetCustomAttributes(typeof(RequireRoleAttribute), true).FirstOrDefault() as RequireRoleAttribute;
            var adminReviewAttr = adminReviewMethod.GetCustomAttributes(typeof(RequireRoleAttribute), true).FirstOrDefault() as RequireRoleAttribute;
            Assert.NotNull(adminGetAllAttr);
            Assert.NotNull(adminReviewAttr);
            Assert.Contains("Admin", adminGetAllAttr.Roles);
            Assert.Contains("Admin", adminReviewAttr.Roles);
        }
    }
}
