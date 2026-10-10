using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using EventTicketBooking.Api.Controllers;
using EventTicketBooking.Api.Data;
using EventTicketBooking.Api.DTOs;
using EventTicketBooking.Api.Models;
using EventTicketBooking.Api.Services;
using EventTicketBooking.Api.Services.Implementations;
using EventTicketBooking.Api.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EventTicketBooking.Tests
{
    public class UserTermsS54Tests : IDisposable
    {
        private readonly AppDbContext _db;
        private readonly IPasswordHasher _passwordHasher;
        private readonly Mock<ITokenService> _tokenServiceMock;
        private readonly Mock<IEmailService> _emailServiceMock;

        public UserTermsS54Tests()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: $"UserTermsTestDb_{Guid.NewGuid()}")
                .Options;

            _db = new AppDbContext(options);
            _passwordHasher = new Argon2PasswordHasher();
            _tokenServiceMock = new Mock<ITokenService>();
            _tokenServiceMock
                .Setup(s => s.GenerateAccessToken(It.IsAny<User>(), It.IsAny<IEnumerable<string>>()))
                .Returns("mock_jwt_token_for_s54");

            _emailServiceMock = new Mock<IEmailService>();
            _emailServiceMock
                .Setup(s => s.SendOtpEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            _emailServiceMock
                .Setup(s => s.SendMarketingEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(true);

            // Đảm bảo role Customer tồn tại
            if (!_db.Roles.Any(r => r.Name == "Customer"))
            {
                _db.Roles.Add(new Role
                {
                    Id = Guid.NewGuid(),
                    Name = "Customer",
                    Description = "Khách hàng mua vé",
                    CreatedAt = DateTime.UtcNow
                });
                _db.SaveChanges();
            }

            // Reset TermsPolicy về mặc định trước mỗi test
            TermsPolicy.ResetToDefault();
        }

        public void Dispose()
        {
            TermsPolicy.ResetToDefault();
            _db.Dispose();
        }

        private AuthController CreateAuthController()
        {
            var configMock = new Mock<IConfiguration>();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            return new AuthController(
                new AuthService(_db, _passwordHasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance),
                _db,
                _passwordHasher,
                _emailServiceMock.Object,
                _tokenServiceMock.Object,
                configMock.Object,
                httpClientFactoryMock.Object,
                NullLogger<AuthController>.Instance);
        }

        private UserTermsController CreateUserTermsController(User currentUser)
        {
            var controller = new UserTermsController(
                _db,
                _emailServiceMock.Object,
                NullLogger<UserTermsController>.Instance);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()),
                new Claim("id", currentUser.Id.ToString()),
                new Claim(ClaimTypes.Email, currentUser.Email)
            };
            var identity = new ClaimsIdentity(claims, "TestAuth");
            var principal = new ClaimsPrincipal(identity);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            };

            return controller;
        }

        private async Task<User> SeedActiveUserAsync(string email, string termsVersion = "v1.0", bool marketingOptIn = true)
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = email.Split('@')[0],
                Email = email,
                FullName = "Nguyễn Văn Test",
                PasswordHash = _passwordHasher.Hash("SecurePass@123"),
                IsActive = true,
                TermsVersion = termsVersion,
                TermsAcceptedAt = DateTime.UtcNow.AddDays(-1),
                MarketingEmailOptIn = marketingOptIn,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow.AddDays(-1)
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
            return user;
        }

        [Fact]
        public async Task Register_WithoutAcceptingTerms_ShouldReturnBadRequest()
        {
            // Arrange
            var controller = CreateAuthController();
            var request = new RegisterRequestDto
            {
                FullName = "Trần Thị Không Đồng Ý",
                Email = "no_terms@example.com",
                Password = "Password@123",
                AcceptTerms = false // Không tích đồng ý điều khoản
            };

            // Act
            var result = await controller.Register(request);

            // Assert: Bị từ chối HTTP 400 Bad Request
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(badRequestResult.Value);
        }

        [Fact]
        public async Task Register_WithAcceptingTerms_ShouldPersistVersionTimestampAndMarketingOptIn()
        {
            // Arrange
            var controller = CreateAuthController();
            var beforeTime = DateTime.UtcNow.AddSeconds(-1);
            var request = new RegisterRequestDto
            {
                FullName = "Lê Đồng Ý",
                Email = "accept_terms@example.com",
                Password = "Password@123",
                AcceptTerms = true // Tích đồng ý
            };

            // Act
            var result = await controller.Register(request);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            // Kiểm tra trong cơ sở dữ liệu: Hệ thống lưu đúng phiên bản và thời điểm
            var savedUser = await _db.Users.FirstOrDefaultAsync(u => u.Email == request.Email.ToLowerInvariant());
            Assert.NotNull(savedUser);
            Assert.Equal(TermsPolicy.CurrentVersion, savedUser.TermsVersion);
            Assert.NotNull(savedUser.TermsAcceptedAt);
            Assert.True(savedUser.TermsAcceptedAt >= beforeTime);
            Assert.True(savedUser.MarketingEmailOptIn);
        }

        [Fact]
        public async Task Login_WithMatchingTermsVersion_ShouldSucceed()
        {
            // Arrange
            var user = await SeedActiveUserAsync("matched_terms@example.com", termsVersion: TermsPolicy.CurrentVersion);
            var authService = new AuthService(_db, _passwordHasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance);

            // Act
            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "SecurePass@123",
                AcceptCurrentTerms = false
            });

            // Assert
            Assert.True(result.Success);
            Assert.False(result.RequiresTermsAcceptance);
            Assert.NotNull(result.Data);
            Assert.Equal("mock_jwt_token_for_s54", result.Data.AccessToken);
        }

        [Fact]
        public async Task Login_WhenPolicyVersionChanged_WithoutAccepting_ShouldRequireReAcceptance()
        {
            // Arrange: Người dùng trước đó đã đồng ý bản v1.0
            var user = await SeedActiveUserAsync("old_version@example.com", termsVersion: "v1.0");

            // Hệ thống nâng cấp phiên bản chính sách lên v1.1
            TermsPolicy.CurrentVersion = "v1.1";

            var authService = new AuthService(_db, _passwordHasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance);

            // Act: Đăng nhập bình thường mà chưa gửi AcceptCurrentTerms = true
            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "SecurePass@123",
                AcceptCurrentTerms = false
            });

            // Assert: Phải yêu cầu đồng ý lại (HTTP 428 Precondition Required)
            Assert.False(result.Success);
            Assert.True(result.RequiresTermsAcceptance);
            Assert.Equal(428, result.StatusCode);
            Assert.Equal("v1.1", result.RequiredTermsVersion);
        }

        [Fact]
        public async Task Login_WhenPolicyVersionChanged_WithAccepting_ShouldUpdateUserTermsAndSucceed()
        {
            // Arrange: Người dùng phiên bản cũ v1.0
            var user = await SeedActiveUserAsync("upgrade_user@example.com", termsVersion: "v1.0");

            // Hệ thống đổi phiên bản lên v1.1
            TermsPolicy.CurrentVersion = "v1.1";
            var beforeUpdate = DateTime.UtcNow.AddSeconds(-1);

            var authService = new AuthService(_db, _passwordHasher, _tokenServiceMock.Object, NullLogger<AuthService>.Instance);

            // Act: Đăng nhập kèm chấp thuận phiên bản mới
            var result = await authService.LoginAsync(new LoginRequestDto
            {
                Email = user.Email,
                Password = "SecurePass@123",
                AcceptCurrentTerms = true
            });

            // Assert: Đăng nhập thành công và cập nhật phiên bản mới cùng thời điểm
            Assert.True(result.Success);
            Assert.NotNull(result.Data);

            var updatedUser = await _db.Users.FindAsync(user.Id);
            Assert.NotNull(updatedUser);
            Assert.Equal("v1.1", updatedUser.TermsVersion);
            Assert.NotNull(updatedUser.TermsAcceptedAt);
            Assert.True(updatedUser.TermsAcceptedAt >= beforeUpdate);
        }

        [Fact]
        public async Task UserTermsController_GetStatus_ShouldReturnAccurateInfo()
        {
            // Arrange
            var user = await SeedActiveUserAsync("status_user@example.com", termsVersion: "v1.0", marketingOptIn: true);
            var controller = CreateUserTermsController(user);

            // Act
            var result = await controller.GetTermsStatus();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            var statusDto = Assert.IsType<UserTermsStatusDto>(okResult.Value);

            Assert.Equal(user.Id, statusDto.UserId);
            Assert.Equal("v1.0", statusDto.TermsVersion);
            Assert.True(statusDto.IsTermsCurrent);
            Assert.True(statusDto.MarketingEmailOptIn);
            Assert.True(statusDto.CanReceiveMarketingEmails);
        }

        [Fact]
        public async Task UserTermsController_RevokeMarketing_ShouldDisableMarketingOptIn()
        {
            // Arrange: Người dùng ban đầu đang nhận email tiếp thị
            var user = await SeedActiveUserAsync("revoke_user@example.com", marketingOptIn: true);
            var controller = CreateUserTermsController(user);

            // Act: Người dùng bấm thu hồi quyền nhận email tiếp thị
            var result = await controller.RevokeMarketingConsent();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            // Kiểm tra DB: MarketingEmailOptIn đã chuyển sang false
            var updatedUser = await _db.Users.FindAsync(user.Id);
            Assert.NotNull(updatedUser);
            Assert.False(updatedUser.MarketingEmailOptIn);
        }

        [Fact]
        public async Task UserTermsController_OptInMarketing_ShouldEnableMarketingOptIn()
        {
            // Arrange: Người dùng trước đó đã thu hồi
            var user = await SeedActiveUserAsync("optin_user@example.com", marketingOptIn: false);
            var controller = CreateUserTermsController(user);

            // Act: Người dùng kích hoạt lại
            var result = await controller.OptInMarketingConsent();

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            var updatedUser = await _db.Users.FindAsync(user.Id);
            Assert.NotNull(updatedUser);
            Assert.True(updatedUser.MarketingEmailOptIn);
        }

        [Fact]
        public async Task UserTermsController_SendMarketingTest_WhenConsentRevoked_ShouldReturn403Forbidden()
        {
            // Arrange: Người dùng ĐÃ THU HỒI QUYỀN NHẬN TIẾP THỊ
            var user = await SeedActiveUserAsync("no_marketing@example.com", marketingOptIn: false);
            var controller = CreateUserTermsController(user);

            // Act: Yêu cầu gửi email tiếp thị
            var result = await controller.SendMarketingTest(new SendMarketingTestDto
            {
                Subject = "Ưu đãi đặc biệt",
                Content = "Giảm giá 50%"
            });

            // Assert: Hệ thống CHẶN GỬI và trả về 403 Forbidden!
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);

            // Không bao giờ gọi dịch vụ gửi email
            _emailServiceMock.Verify(s => s.SendMarketingEmailAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        [Fact]
        public async Task UserTermsController_SendMarketingTest_WhenConsentGranted_ShouldSendEmail()
        {
            // Arrange: Người dùng ĐANG ĐỒNG Ý NHẬN TIẾP THỊ
            var user = await SeedActiveUserAsync("yes_marketing@example.com", marketingOptIn: true);
            var controller = CreateUserTermsController(user);

            // Act: Gửi email tiếp thị
            var result = await controller.SendMarketingTest(new SendMarketingTestDto
            {
                Subject = "Ưu đãi độc quyền",
                Content = "Vé Concert cuối tuần"
            });

            // Assert: Thành công HTTP 200 OK
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);

            // Dịch vụ gửi email được triệu gọi thành công
            _emailServiceMock.Verify(s => s.SendMarketingEmailAsync(
                user.Email, It.IsAny<string>(), "Ưu đãi độc quyền", "Vé Concert cuối tuần"),
                Times.Once);
        }

        [Fact]
        public void UserTermsController_AdminChangePolicyVersion_ShouldUpdateCurrentVersion()
        {
            // Arrange
            var controller = new UserTermsController(_db, _emailServiceMock.Object, NullLogger<UserTermsController>.Instance);

            // Act: Admin nâng cấp version lên v2.0
            var result = controller.ChangePolicyVersion(new UpdatePolicyVersionDto
            {
                NewVersion = "v2.0"
            });

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult.Value);
            Assert.Equal("v2.0", TermsPolicy.CurrentVersion);
        }
    }
}
